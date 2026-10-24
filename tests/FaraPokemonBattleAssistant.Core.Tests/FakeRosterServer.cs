using System.Net;
using System.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>
/// データ共有 API（worker/index.js の /api/my/pokemon・/api/my/team・/api/my/access-key）と同じ動きをするメモリ上の偽サーバー。
/// </summary>
internal sealed class FakeRosterServer : HttpMessageHandler
{
    public long Revision { get; set; }
    /// <summary>保存している登録データ（RosterState の JSON。サーバーと同じく 1 つの文書）。</summary>
    public string? RosterJson { get; set; }
    public bool Deleted { get; set; }
    public string? ExpectedKey { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> Bodies { get; } = new();
    /// <summary>PUT を処理する直前に呼ぶ（他の端末の書き込みを割り込ませる）。</summary>
    public Action<FakeRosterServer>? BeforePut { get; set; }
    /// <summary>GET を処理する直前に呼ぶ。</summary>
    public Action<FakeRosterServer, string>? BeforeGet { get; set; }
    /// <summary>設定するとその応答を返す。</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    /// <summary>履歴の上限（ゲームごとの件数）。本物のサーバーは 10,000 件。テストでは小さくできる。</summary>
    public int HistoryLimit { get; set; } = 10000;

    /// <summary>別のハンドラー（アクセスキーごとに振り分ける偽サーバー）から呼ぶための入口。</summary>
    public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Bodies.Add(body);
        if (Override?.Invoke(request) is { } overridden) return overridden;
        var path = request.RequestUri!.AbsolutePath;
        var methods = path switch
        {
            "/api/my/pokemon" or "/api/my/team" or "/api/my/rival" => new[] { "GET", "PUT" },
            "/api/my/history" => new[] { "GET", "POST", "DELETE" },
            "/api/my/access-key" => new[] { "DELETE" },
            _ => null,
        };
        if (methods is null) return Json(HttpStatusCode.NotFound, "{}");
        if (!methods.Contains(request.Method.Method)) return Json(HttpStatusCode.MethodNotAllowed, "{}");
        var auth = request.Headers.Authorization;
        if (auth?.Scheme != "Bearer" || (ExpectedKey is not null && auth.Parameter != ExpectedKey))
            return Json(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");

        if (path == "/api/my/access-key")
        {
            // ?expect=ゲーム:件数:次の Id,…（本物と同じ。履歴の状態が違えば消さずに 409 changed）
            if (HttpUtility.ParseQueryString(request.RequestUri!.Query)["expect"] is { } expect && !Deleted)
            {
                var current = RosterRepository.Deserialize(RosterJson).DataSets.Where(kv => kv.Value.History.Count > 0)
                    .Select(kv => $"{kv.Key.ToLowerInvariant()}:{kv.Value.History.Count}:{kv.Value.NextMatchId}").OrderBy(x => x, StringComparer.Ordinal).ToList();
                var wanted = expect.Length == 0 ? new List<string>() : expect.Split(',').OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (!current.SequenceEqual(wanted))
                {
                    var sets = new JsonArray(RosterRepository.Deserialize(RosterJson).DataSets.Where(kv => kv.Value.History.Count > 0)
                        .Select(kv => (JsonNode)new JsonObject { ["dataSet"] = kv.Key.ToLowerInvariant(), ["total"] = kv.Value.History.Count, ["nextId"] = kv.Value.NextMatchId }).ToArray());
                    return Json(HttpStatusCode.Conflict, new JsonObject { ["error"] = "changed", ["dataSets"] = sets }.ToJsonString());
                }
            }
            Revision = 0;
            RosterJson = null;
            Deleted = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (Deleted) return Json(HttpStatusCode.Gone, """{"error":"key_retired"}""");
        if (path == "/api/my/history")
        {
            // 本物と同じ本文の上限（256 KB）
            if (body is not null && Encoding.UTF8.GetByteCount(body) > 256 * 1024) return Json(HttpStatusCode.RequestEntityTooLarge, """{"error":"too_large"}""");
            return History(request, body);
        }
        var pokemon = path == "/api/my/pokemon";
        var rival = path == "/api/my/rival";
        var historyLimit = int.TryParse(HttpUtility.ParseQueryString(request.RequestUri.Query)["historyLimit"], out var hl) ? hl : (int?)null;
        if (request.Method == HttpMethod.Get)
        {
            BeforeGet?.Invoke(this, path);
            return View(HttpStatusCode.OK, pokemon, rival, historyLimit);
        }

        BeforePut?.Invoke(this);
        var node = JsonNode.Parse(body!)!.AsObject();
        var baseRevision = node["baseRevision"]!.GetValue<long>();
        if (baseRevision != Revision) return View(HttpStatusCode.Conflict, pokemon, rival, historyLimit);
        var state = RosterRepository.Deserialize(RosterJson);
        foreach (var (key, value) in node["dataSets"]!.AsObject())
        {
            var roster = state.For(key);
            if (rival)
            {
                roster.Rivals = value!["rivals"].Deserialize<List<RivalPokemon>>()!;
                roster.NextRivalId = Math.Max(roster.NextRivalId, Math.Max(value["nextId"]?.GetValue<int>() ?? 1, roster.Rivals.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1));
                // 履歴は置き換えない。「次の Id 以降の、まだ無い試合」だけを足す（本物のサーバーと同じ。古い版のクライアント用）
                if (value["history"]?.Deserialize<List<RivalMatch>>() is { } sent)
                {
                    foreach (var match in sent.OrderBy(m => m.Id))
                    {
                        if (match.Id < roster.NextMatchId || roster.History.Count >= HistoryLimit) continue;
                        roster.History.Add(match);
                        roster.NextMatchId = match.Id + 1;
                    }
                }
                roster.NextMatchId = Math.Max(roster.NextMatchId, value["nextMatchId"]?.GetValue<int>() ?? 1);
            }
            else if (pokemon)
            {
                roster.Pokemon = value!["pokemon"].Deserialize<List<RegisteredPokemon>>()!;
                var nextId = value["nextId"]?.GetValue<int>() ?? 1;
                roster.NextId = Math.Max(roster.NextId, Math.Max(nextId, roster.Pokemon.Select(p => p.Id).DefaultIfEmpty(0).Max() + 1));
                var ids = roster.Pokemon.Select(p => p.Id).ToHashSet();
                foreach (var team in roster.Teams) team.Slots.RemoveAll(slot => !ids.Contains(slot.EntryId));
                RosterRepository.RemoveEmptyTeams(roster);
            }
            else
            {
                var teams = value!["teams"].Deserialize<List<Team>>()!;
                var actives = value["activeTeams"]?.AsObject();
                var ids = roster.Pokemon.Select(p => p.Id).ToHashSet();
                // 本物のサーバーと同じ検証: 形式ごとの番号の重複・空のチーム・登録に無いポケモン・存在しない使用中は 400
                if (actives is null)
                    return Json(HttpStatusCode.BadRequest, """{"error":"invalid_active_teams","message":"activeTeams を指定してください"}""");
                if (teams.GroupBy(t => t.Key).Any(g => g.Count() > 1))
                    return Json(HttpStatusCode.BadRequest, """{"error":"duplicate_team","message":"同じチームが 2 つあります"}""");
                if (teams.Any(t => t.Slots.Count is 0 or > 6 || t.Slots.Any(slot => !ids.Contains(slot.EntryId))))
                    return Json(HttpStatusCode.BadRequest, """{"error":"unknown_pokemon","message":"チーム1に、登録に無いポケモン（Id 99）が入っています"}""");
                var activeNumbers = new Dictionary<string, int?>();
                foreach (var (format, number) in actives)
                {
                    if (number is null) continue;
                    var n = number.GetValue<int>();
                    if (!teams.Any(t => TeamFormats.Name(t.BattleFormat) == format && t.Number == n))
                        return Json(HttpStatusCode.BadRequest, """{"error":"unknown_active_team","message":"使用中のチームがありません"}""");
                    activeNumbers[format] = n;
                }
                roster.Teams = teams.Select(t => { t.Format = TeamFormats.Name(t.BattleFormat); return t; })
                    .OrderBy(t => t.Number).ThenBy(t => t.BattleFormat).ToList();
                roster.ActiveTeams = activeNumbers;
            }
        }
        RosterJson = JsonNode.Parse(RosterRepository.Serialize(state))!.ToJsonString();
        Revision++;
        return Json(HttpStatusCode.OK, $$"""{"revision":{{Revision}}}""");
    }

    /// <summary>/api/my/history（本物のサーバーと同じ動き。履歴の変更では revision は増えない。履歴は登録データの History を表の代わりにする）。</summary>
    private HttpResponseMessage History(HttpRequestMessage request, string? body)
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var state = RosterRepository.Deserialize(RosterJson);
        if (request.Method == HttpMethod.Get && query["dataSet"] is null)
        {
            // 履歴のあるゲームの一覧（ゲームのキーは小文字）
            var sets = new JsonArray(state.DataSets.Where(kv => kv.Value.History.Count > 0)
                .Select(kv => (JsonNode)new JsonObject { ["dataSet"] = kv.Key.ToLowerInvariant(), ["total"] = kv.Value.History.Count, ["nextId"] = kv.Value.NextMatchId }).ToArray());
            return Json(HttpStatusCode.OK, new JsonObject { ["dataSets"] = sets }.ToJsonString());
        }
        if (request.Method == HttpMethod.Post)
        {
            var node = JsonNode.Parse(body!)!.AsObject();
            var roster = state.For(node["dataSet"]!.GetValue<string>());
            var sent = node["matches"] is { } many
                ? many.Deserialize<List<RivalMatch>>()!
                : new List<RivalMatch> { node["match"].Deserialize<RivalMatch>()! };
            // 本物と同じ検証: 1 試合は 64 KB まで、まとめて足すのは 100 件まで
            var parts = node["matches"] is JsonArray array ? array.ToList() : new List<JsonNode?> { node["match"] };
            if (sent.Count is 0 or > 100 || parts.Any(p => Encoding.UTF8.GetByteCount(p!.ToJsonString()) > 64 * 1024))
                return Json(HttpStatusCode.BadRequest, """{"error":"invalid_match","message":"試合が大きすぎます"}""");
            // 同じ ClientId の試合がすでにあれば、足さずにその Id を返す（本物と同じ。単発の match だけ）
            if (node["match"] is not null && sent[0].ClientId is { } clientId && roster.History.FirstOrDefault(m => m.ClientId == clientId) is { } existing)
                return Json(HttpStatusCode.OK, new JsonObject { ["id"] = existing.Id, ["total"] = roster.History.Count, ["duplicate"] = true }.ToJsonString());
            if (roster.History.Count + sent.Count > HistoryLimit)
                return Json(HttpStatusCode.Conflict, """{"error":"history_limit","message":"試合の履歴が上限に達しています。古い履歴を削除してください"}""");
            var ids = new List<int>();
            foreach (var match in sent)
            {
                match.Id = roster.NextMatchId++;
                roster.History.Add(match);
                ids.Add(match.Id);
            }
            Save(state);
            return Json(HttpStatusCode.Created, node["matches"] is not null
                ? new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()), ["total"] = roster.History.Count }.ToJsonString()
                : new JsonObject { ["id"] = ids[0], ["total"] = roster.History.Count }.ToJsonString());
        }
        var set = state.For(query["dataSet"]!);
        if (request.Method == HttpMethod.Delete)
        {
            if (query["keepLatest"] is { } keep)
            {
                var keepIds = set.History.OrderByDescending(m => m.Id).Take(int.Parse(keep)).Select(m => m.Id).ToHashSet();
                var deleted = set.History.RemoveAll(m => !keepIds.Contains(m.Id));
                Save(state);
                return Json(HttpStatusCode.OK, new JsonObject { ["deleted"] = deleted, ["total"] = set.History.Count }.ToJsonString());
            }
            var removed = set.History.RemoveAll(m => m.Id == int.Parse(query["id"]!));
            if (removed == 0) return Json(HttpStatusCode.NotFound, """{"error":"not_found"}""");
            Save(state);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (query["id"] is { } id)
        {
            var found = set.History.FirstOrDefault(m => m.Id == int.Parse(id));
            return found is null
                ? Json(HttpStatusCode.NotFound, """{"error":"not_found"}""")
                : Json(HttpStatusCode.OK, new JsonObject { ["total"] = set.History.Count, ["item"] = JsonSerializer.SerializeToNode(found) }.ToJsonString());
        }
        var limit = int.TryParse(query["limit"], out var l) ? l : 50;
        if (limit is < 1 or > 200) return Json(HttpStatusCode.BadRequest, """{"error":"invalid_query"}""");
        var before = int.TryParse(query["before"], out var b) ? b : int.MaxValue;
        var older = set.History.Where(m => m.Id < before).OrderByDescending(m => m.Id).Take(limit + 1).ToList();
        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["total"] = set.History.Count,
            ["items"] = JsonSerializer.SerializeToNode(older.Take(limit).ToList()),
            ["hasMore"] = older.Count > limit,
        }.ToJsonString());
    }

    private void Save(RosterState state) => RosterJson = JsonNode.Parse(RosterRepository.Serialize(state))!.ToJsonString();

    private HttpResponseMessage View(HttpStatusCode status, bool pokemon, bool rival = false, int? historyLimit = null)
    {
        var dataSets = new JsonObject();
        foreach (var (key, roster) in RosterRepository.Deserialize(RosterJson).DataSets)
        {
            // historyLimit なしは、古い版のクライアント用に直近 200 件（古い順）。0 は空
            var history = roster.History.OrderByDescending(m => m.Id).Take(historyLimit ?? 200).OrderBy(m => m.Id).ToList();
            dataSets[key] = rival
                ? new JsonObject { ["nextId"] = roster.NextRivalId, ["rivals"] = JsonSerializer.SerializeToNode(roster.Rivals), ["nextMatchId"] = roster.NextMatchId, ["history"] = JsonSerializer.SerializeToNode(history) }
                : pokemon
                ? new JsonObject { ["nextId"] = roster.NextId, ["pokemon"] = JsonSerializer.SerializeToNode(roster.Pokemon) }
                : new JsonObject
                {
                    ["teams"] = JsonSerializer.SerializeToNode(roster.Teams.OrderBy(t => t.Number).ThenBy(t => t.BattleFormat).ToList()),
                    ["activeTeams"] = new JsonObject
                    {
                        ["Singles"] = roster.ActiveNumber(BattleFormat.Singles),
                        ["Doubles"] = roster.ActiveNumber(BattleFormat.Doubles),
                    },
                };
        }
        return Json(status, new JsonObject { ["revision"] = Revision, ["dataSets"] = dataSets }.ToJsonString());
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>他の端末が書き込んだことにする（revision を進める）。</summary>
    public void WriteFromElsewhere(Action<RosterState> change)
    {
        var state = RosterRepository.Deserialize(RosterJson);
        change(state);
        RosterJson = JsonNode.Parse(RosterRepository.Serialize(state))!.ToJsonString();
        Revision++;
    }

    /// <summary>保存されている登録データ。</summary>
    public RosterState State => RosterRepository.Deserialize(RosterJson);
}
