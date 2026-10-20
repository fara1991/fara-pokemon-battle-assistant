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

    private static readonly JsonSerializerOptions HistoryReadOptions = new() { PropertyNameCaseInsensitive = true };

    public ValueTask<IMatchHistory?> GetRemoteHistoryAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IMatchHistory?>(this);

    private Uri HistoryUri(string dataSetKey, string extraQuery = "") =>
        new(_rivalEndpoint, "history?dataSet=" + Uri.EscapeDataString(dataSetKey) + extraQuery);

    public async Task<MatchPage> ListAsync(string dataSetKey, int limit, int? beforeId = null, CancellationToken ct = default)
    {
        var query = $"&limit={limit}" + (beforeId is { } before ? $"&before={before}" : "");
        using var response = await SendAsync(HttpMethod.Get, HistoryUri(dataSetKey, query), null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("total", out var total) || !total.TryGetInt32(out var count)
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("hasMore", out var hasMore) || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidResponse();
        return new MatchPage(count, ReadMatches(items), hasMore.GetBoolean());
    }

    public async Task<RivalMatch?> GetAsync(string dataSetKey, int id, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, HistoryUri(dataSetKey, $"&id={id}"), null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("item", out var item)) throw InvalidResponse();
        return ReadMatch(item);
    }

    public async Task<RivalMatch> AppendAsync(string dataSetKey, RivalMatch match, CancellationToken ct = default)
    {
        using var content = BuildHistoryContent(dataSetKey, "match", MatchNode(match));
        using var response = await SendAsync(HttpMethod.Post, new Uri(_rivalEndpoint, "history"), content, ct).ConfigureAwait(false);
        await ThrowIfHistoryLimitAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt32(out var assigned))
            throw InvalidResponse();
        LastSyncedAt = DateTimeOffset.Now;
        return new RivalMatch { Id = assigned, Date = match.Date, Memo = match.Memo, Foes = match.Foes, Extra = match.Extra };
    }

    /// <summary>
    /// 試合をまとめて足す（データの移し替え用。100 件ずつ送る）。古い順に並べて送り、Id はサーバーが割り当て直す。
    /// 途中で失敗したら、そこまでに足した分は残る（呼び出し側は、新しいキーを捨てる）。
    /// </summary>
    public async Task AppendManyAsync(string dataSetKey, IReadOnlyList<RivalMatch> matches, CancellationToken ct = default)
    {
        for (var i = 0; i < matches.Count; i += MatchesPerPost)
        {
            var array = new JsonArray();
            foreach (var match in matches.Skip(i).Take(MatchesPerPost)) array.Add(MatchNode(match));
            using var content = BuildHistoryContent(dataSetKey, "matches", array);
            using var response = await SendAsync(HttpMethod.Post, new Uri(_rivalEndpoint, "history"), content, ct).ConfigureAwait(false);
            await ThrowIfHistoryLimitAsync(response, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        }
        if (matches.Count > 0) LastSyncedAt = DateTimeOffset.Now;
    }

    public async Task<bool> RemoveAsync(string dataSetKey, int id, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, HistoryUri(dataSetKey, $"&id={id}"), null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return true;
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        throw await UnavailableAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<int> TrimAsync(string dataSetKey, int keepLatest, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, HistoryUri(dataSetKey, $"&keepLatest={keepLatest}"), null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("deleted", out var deleted) || !deleted.TryGetInt32(out var count))
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
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.GetString() == "history_limit")
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
