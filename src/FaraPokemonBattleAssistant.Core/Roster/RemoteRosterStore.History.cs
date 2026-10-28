using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FaraPokemonBattleAssistant.Core.Roster;

// 試合の履歴（/api/my/history）。登録データの版（revision）とは独立していて、履歴の変更では版が変わらない。
public sealed partial class RemoteRosterStore : IMatchHistory
{
    /// <summary>履歴をまとめて足すときの 1 回の件数（サーバーの上限と同じ）。</summary>
    private const int MatchesPerPost = 100;
    /// <summary>まとめて足すときの 1 回の本文の目安（サーバーの上限は 256 KB。余裕を見る）。</summary>
    private const int MaxBatchBytes = 200 * 1024;
    /// <summary>1 試合の上限（サーバーは 64 KB。余裕を 1 KB 見る）。超えるものだけ、推定の根拠（Scans）を省いて送る。</summary>
    private const int MaxMatchBytes = 63 * 1024;
    /// <summary>メモの文字数の上限（サーバーと同じ）。</summary>
    private const int MaxMemoLength = 500;
    /// <summary>一覧の 1 回の件数の範囲（サーバーと同じ）。</summary>
    public const int MaxPageSize = 200;

    private static readonly JsonSerializerOptions HistoryReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>直近の <see cref="SaveAsync"/> で、サーバーの上限に収めるため内容を省いた試合の数。</summary>
    public int HistoryTrimmed { get; private set; }

    public ValueTask<IMatchHistory?> GetRemoteHistoryAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IMatchHistory?>(this);

    private Uri HistoryUri(string? dataSetKey, string extraQuery = "") =>
        dataSetKey is null
            ? new Uri(_rivalEndpoint, "history")
            : new Uri(_rivalEndpoint, "history?dataSet=" + Uri.EscapeDataString(dataSetKey) + extraQuery);

    /// <summary>履歴のあるゲームと、その件数（ゲームのキーは小文字。キーの作り直しで履歴を写すときに使う）。</summary>
    public async Task<IReadOnlyDictionary<string, HistoryTotal>> DataSetTotalsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, HistoryUri(null), null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("dataSets", out var sets) || sets.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        return ReadTotals(sets);
    }

    private static Dictionary<string, HistoryTotal> ReadTotals(JsonElement sets)
    {
        var totals = new Dictionary<string, HistoryTotal>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sets.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("dataSet", out var name) || name.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("total", out var total) || !total.TryGetInt32(out var count) || count < 0
                || !item.TryGetProperty("nextId", out var next) || !next.TryGetInt32(out var nextId) || nextId < 1)
                throw InvalidResponse();
            totals[name.GetString()!] = new HistoryTotal(count, nextId);
        }
        return totals;
    }

    /// <summary>
    /// このアクセスキーを使えなくし、置き場を消す。ただし、履歴の状態が <paramref name="expect"/>（写した時点の状態）と同じときだけ。
    /// 違うときは何も消さずに <see cref="HistoryChangedException"/>（キーの作り直しで、写している間に足された試合を取りこぼさないため）。
    /// </summary>
    public async Task DeleteIfHistoryUnchangedAsync(IReadOnlyDictionary<string, HistoryTotal> expect, CancellationToken ct = default)
    {
        var query = string.Join(",", expect.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key.ToLowerInvariant()}:{kv.Value.Total}:{kv.Value.NextId}"));
        var uri = new Uri(_accessKeyEndpoint + "?expect=" + Uri.EscapeDataString(query));
        using var response = await SendAsync(HttpMethod.Delete, uri, null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            Revision = null;
            return;
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "changed"
                && root.TryGetProperty("dataSets", out var sets) && sets.ValueKind == JsonValueKind.Array)
                throw new HistoryChangedException(ReadTotals(sets));
            throw InvalidResponse();
        }
        throw await UnavailableAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<MatchPage> ListAsync(string dataSetKey, int limit, int? beforeId = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var query = $"&limit={limit}" + (beforeId is { } before ? $"&before={before}" : "");
        using var response = await SendAsync(HttpMethod.Get, HistoryUri(dataSetKey, query), null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("total", out var total) || !total.TryGetInt32(out var count) || count < 0
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("hasMore", out var hasMore) || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidResponse();
        var matches = ReadMatches(items);
        // 新しい順（Id が減っていく）で、頼んだ件数を超えず、続きの頼み方（before）より前の試合だけが返る
        if (matches.Count > limit
            || matches.Zip(matches.Skip(1)).Any(p => p.First.Id <= p.Second.Id)
            || matches.Any(m => m.Id < 1 || (beforeId is { } b && m.Id >= b)))
            throw InvalidResponse();
        return new MatchPage(count, matches, hasMore.GetBoolean());
    }

    public async Task<RivalMatch?> GetAsync(string dataSetKey, int id, CancellationToken ct = default)
    {
        if (id < 1) return null;
        using var response = await SendAsync(HttpMethod.Get, HistoryUri(dataSetKey, $"&id={id}"), null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("item", out var item)) throw InvalidResponse();
        return ReadMatch(item);
    }

    public async Task<RivalMatch> AppendAsync(string dataSetKey, RivalMatch match, CancellationToken ct = default)
    {
        using var content = BuildHistoryContent(dataSetKey, "match", FitToServerLimit(MatchNode(match), out _, out _));
        using var response = await SendAsync(HttpMethod.Post, HistoryUri(null), content, ct).ConfigureAwait(false);
        await ThrowIfHistoryLimitAsync(response, ct).ConfigureAwait(false);
        // 201 = 足した、200 = 同じ ClientId の試合がすでにあった（その Id が返る）
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK)) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt32(out var assigned) || assigned < 1)
            throw InvalidResponse();
        LastSyncedAt = DateTimeOffset.Now;
        return new RivalMatch { Id = assigned, ClientId = match.ClientId, Date = match.Date, Memo = match.Memo, Foes = match.Foes, Extra = match.Extra };
    }

    /// <summary>
    /// 試合をまとめて足す（データの移し替え用）。古い順に並べて送り、Id はサーバーが割り当て直す。
    /// 1 回の本文が大きくなりすぎないよう、件数（100）と大きさ（約 200 KB）のどちらかで区切る。
    /// 途中で失敗したら、そこまでに足した分は残る（呼び出し側は、新しいキーを捨てる）。
    /// サーバーの 1 試合の上限（64 KB）・メモの上限（500 文字）を超える試合は、推定の根拠（Scans）を省き、メモを切って送る。
    /// 戻り値は、そうして内容を省いた試合の数（呼び出し側が利用者に知らせる）。
    /// </summary>
    public async Task<int> AppendManyAsync(string dataSetKey, IReadOnlyList<RivalMatch> matches, CancellationToken ct = default)
    {
        var batch = new JsonArray();
        var bytes = 0;
        var trimmed = 0;
        foreach (var match in matches)
        {
            var node = FitToServerLimit(MatchNode(match), out var size, out var wasTrimmed);
            if (wasTrimmed) trimmed++;
            if (batch.Count > 0 && (batch.Count >= MatchesPerPost || bytes + size > MaxBatchBytes))
            {
                await PostBatchAsync(dataSetKey, batch, ct).ConfigureAwait(false);
                batch = new JsonArray();
                bytes = 0;
            }
            batch.Add(node);
            bytes += size;
        }
        if (batch.Count > 0) await PostBatchAsync(dataSetKey, batch, ct).ConfigureAwait(false);
        return trimmed;
    }

    private async Task PostBatchAsync(string dataSetKey, JsonArray batch, CancellationToken ct)
    {
        var count = batch.Count;
        using var content = BuildHistoryContent(dataSetKey, "matches", batch);
        using var response = await SendAsync(HttpMethod.Post, HistoryUri(null), content, ct).ConfigureAwait(false);
        await ThrowIfHistoryLimitAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("ids", out var ids)
            || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != count)
            throw InvalidResponse();
        LastSyncedAt = DateTimeOffset.Now;
    }

    public async Task<bool> RemoveAsync(string dataSetKey, int id, CancellationToken ct = default)
    {
        if (id < 1) return false;
        using var response = await SendAsync(HttpMethod.Delete, HistoryUri(dataSetKey, $"&id={id}"), null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return true;
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        throw await UnavailableAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<int> TrimAsync(string dataSetKey, int keepLatest, CancellationToken ct = default)
    {
        keepLatest = Math.Max(0, keepLatest);
        using var response = await SendAsync(HttpMethod.Delete, HistoryUri(dataSetKey, $"&keepLatest={keepLatest}"), null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("deleted", out var deleted) || !deleted.TryGetInt32(out var count) || count < 0)
            throw InvalidResponse();
        return count;
    }

    /// <summary>試合を、サーバーが受け付ける形（Id は付けない。割り当てはサーバー）にする。</summary>
    private static JsonNode MatchNode(RivalMatch match)
    {
        var node = JsonSerializer.SerializeToNode(match, ItemOptions)!.AsObject();
        node.Remove("Id");
        return node;
    }

    /// <summary>
    /// サーバーの上限（1 試合 64 KB・メモ 500 文字）を超える試合だけ、推定の根拠（Scans）を省き、メモを切って収める
    /// （以前のブラウザ保存には上限が無く、データの移し替えが永久に断られてしまうのを防ぐ）。上限に収まる試合は、そのまま送る。
    /// </summary>
    private static JsonNode FitToServerLimit(JsonNode node, out int size, out bool trimmed)
    {
        trimmed = false;
        if (node["Memo"]?.GetValue<string>() is { } memo && memo.Length > MaxMemoLength)
        {
            // 切る位置がサロゲートペア（絵文字など）の途中なら、1 つ手前で切る（途中で切ると、不正な文字になって保存が壊れる）
            var cut = MaxMemoLength;
            if (char.IsHighSurrogate(memo[cut - 1])) cut--;
            node["Memo"] = memo[..cut];
            trimmed = true;
        }
        size = Encoding.UTF8.GetByteCount(node.ToJsonString(ItemOptions));
        if (size <= MaxMatchBytes) return node;
        if (node["Foes"] is JsonArray foes)
        {
            foreach (var foe in foes.OfType<JsonObject>()) foe.Remove("Scans");
            size = Encoding.UTF8.GetByteCount(node.ToJsonString(ItemOptions));
            trimmed = true;
        }
        return node;
    }

    private static ByteArrayContent BuildHistoryContent(string dataSetKey, string property, JsonNode value)
    {
        var body = new JsonObject { ["dataSet"] = dataSetKey, [property] = value };
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString(ItemOptions)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>409 で <c>history_limit</c> のときは、サーバーのメッセージを <see cref="HistoryLimitException"/> にする。</summary>
    private static async Task ThrowIfHistoryLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Conflict) return;
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String && error.GetString() == "history_limit")
        {
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 and <= 200 } text
                ? text
                : "試合の履歴が上限に達しています。古い履歴を削除してください";
            throw new HistoryLimitException(message);
        }
        throw InvalidResponse();
    }

    private static List<RivalMatch> ReadMatches(JsonElement items)
    {
        var matches = new List<RivalMatch>();
        foreach (var item in items.EnumerateArray()) matches.Add(ReadMatch(item));
        return matches;
    }

    private static RivalMatch ReadMatch(JsonElement item)
    {
        try
        {
            return item.Deserialize<RivalMatch>(HistoryReadOptions) ?? throw InvalidResponse();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }
}
