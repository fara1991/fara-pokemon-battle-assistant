using System.Net;
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
            Revision = 0;
            RosterJson = null;
            Deleted = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (Deleted) return Json(HttpStatusCode.Gone, """{"error":"key_retired"}""");
        var pokemon = path == "/api/my/pokemon";
        var rival = path == "/api/my/rival";
        if (request.Method == HttpMethod.Get)
        {
            BeforeGet?.Invoke(this, path);
            return View(HttpStatusCode.OK, pokemon, rival);
        }

        BeforePut?.Invoke(this);
        var node = JsonNode.Parse(body!)!.AsObject();
        var baseRevision = node["baseRevision"]!.GetValue<long>();
        if (baseRevision != Revision) return View(HttpStatusCode.Conflict, pokemon, rival);
        var state = RosterRepository.Deserialize(RosterJson);
        foreach (var (key, value) in node["dataSets"]!.AsObject())
        {
            var roster = state.For(key);
            if (rival)
            {
                roster.Rivals = value!["rivals"].Deserialize<List<RivalPokemon>>()!;
                roster.History = value["history"].Deserialize<List<RivalMatch>>()!;
                roster.NextRivalId = Math.Max(roster.NextRivalId, Math.Max(value["nextId"]?.GetValue<int>() ?? 1, roster.Rivals.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1));
                roster.NextMatchId = Math.Max(roster.NextMatchId, Math.Max(value["nextMatchId"]?.GetValue<int>() ?? 1, roster.History.Select(h => h.Id).DefaultIfEmpty(0).Max() + 1));
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
                var active = value["activeTeam"]?.GetValue<int>();
                var ids = roster.Pokemon.Select(p => p.Id).ToHashSet();
                if (teams.Any(t => t.Slots.Count is 0 or > 6 || t.Slots.Any(slot => !ids.Contains(slot.EntryId)))
                    || (active is { } n && teams.All(t => t.Number != n)))
                    return Json(HttpStatusCode.BadRequest, """{"error":"unknown_pokemon","message":"チーム1に、登録に無いポケモン（Id 99）が入っています"}""");
                roster.Teams = teams.OrderBy(t => t.Number).ToList();
                roster.ActiveTeam = active;
            }
        }
        RosterJson = JsonNode.Parse(RosterRepository.Serialize(state))!.ToJsonString();
        Revision++;
        return Json(HttpStatusCode.OK, $$"""{"revision":{{Revision}}}""");
    }

    private HttpResponseMessage View(HttpStatusCode status, bool pokemon, bool rival = false)
    {
        var dataSets = new JsonObject();
        foreach (var (key, roster) in RosterRepository.Deserialize(RosterJson).DataSets)
        {
            dataSets[key] = rival
                ? new JsonObject { ["nextId"] = roster.NextRivalId, ["rivals"] = JsonSerializer.SerializeToNode(roster.Rivals), ["nextMatchId"] = roster.NextMatchId, ["history"] = JsonSerializer.SerializeToNode(roster.History) }
                : pokemon
                ? new JsonObject { ["nextId"] = roster.NextId, ["pokemon"] = JsonSerializer.SerializeToNode(roster.Pokemon) }
                : new JsonObject { ["teams"] = JsonSerializer.SerializeToNode(roster.Teams), ["activeTeam"] = roster.ActiveTeam };
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
