using System.Net;
using System.Net.Http;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>キーの作り直し（<see cref="RosterRotation"/>）。サイトと配信用BOTで同じ処理を使うので、ここで手順と止まり方を確かめる。</summary>
public class RosterRotationTests
{
    private const string Base = "https://pokemon.example/";
    private const string Ds = "Gen9";

    private static RosterState Roster(string name, bool withRival = false) => new()
    {
        DataSets =
        {
            [Ds] = new DataSetRoster
            {
                NextId = 2,
                Pokemon = { new RegisteredPokemon { Id = 1, PokemonId = 6, PokemonName = name } },
                NextRivalId = withRival ? 2 : 1,
                Rivals = withRival ? new List<RivalPokemon> { new() { Id = 1, PokemonName = "相手" } } : new(),
            },
        },
    };

    /// <summary>キーの置き場に登録データを入れておく（履歴は、必要なら <see cref="AddMatch"/> で足す）。</summary>
    private static async Task<string> SeedAsync(KeyRoutedServer server, RosterState state)
    {
        var key = RemoteRosterStore.GenerateKey();
        await new RemoteRosterStore(new HttpClient(server), Base, key).SaveAsync(RosterRepository.Serialize(state));
        return key;
    }

    private static void AddMatch(FakeRosterServer bucket, string memo) => bucket.WriteFromElsewhere(state =>
    {
        var set = state.For(Ds);
        set.History.Add(new RivalMatch { Id = set.NextMatchId, Memo = memo });
        set.NextMatchId++;
    });

    /// <summary>試合を足す（登録データの版は変えない。履歴は登録データの版とは別に数える）。</summary>
    private static void AddMatchKeepingRevision(FakeRosterServer bucket, string memo)
    {
        var state = RosterRepository.Deserialize(bucket.RosterJson);
        var set = state.For(Ds);
        set.History.Add(new RivalMatch { Id = set.NextMatchId, Memo = memo });
        set.NextMatchId++;
        bucket.RosterJson = RosterRepository.Serialize(state);
    }

    private static bool IsRetire(HttpRequestMessage request) =>
        request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/api/my/access-key";

    private static bool IsRosterPut(HttpRequestMessage request) =>
        request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath is "/api/my/pokemon" or "/api/my/team" or "/api/my/rival";

    private static HttpResponseMessage Changed(long? revision) => FakeRosterServer.Json(HttpStatusCode.Conflict,
        revision is { } rev ? $$"""{"error":"changed","dataSets":[],"revision":{{rev}}}""" : """{"error":"changed","dataSets":[]}""");

    [Fact]
    public async Task Prepare_copies_the_roster_and_history_and_keeps_the_old_key()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン", withRival: true));
        AddMatch(server.For(oldKey), "前の試合");

        var plan = await RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey);

        Assert.NotEqual(oldKey, plan.NewKey);
        Assert.Equal("リザードン", server.For(plan.NewKey).State.For(Ds).Pokemon.Single().PokemonName);
        Assert.Equal("前の試合", server.For(plan.NewKey).State.For(Ds).History.Single().Memo);
        Assert.False(server.For(oldKey).Deleted);
        Assert.Equal(1, plan.CopiedTotals[Ds.ToLowerInvariant()].Total);
    }

    [Fact]
    public async Task Plan_text_does_not_show_either_key()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン"));
        var plan = await RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey);

        var text = plan.ToString();

        Assert.DoesNotContain(oldKey, text);
        Assert.DoesNotContain(plan.NewKey, text);
    }

    [Fact]
    public async Task A_changed_answer_without_a_revision_rewrites_the_roster_before_the_old_key_is_deleted()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン"));
        var old = server.For(oldKey);
        var plan = await RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey);
        var answered = false;
        old.Override = request =>
        {
            // 版の無い 409 を 1 回返す（版が分からないときは、登録データを写し直してから消す）
            if (!answered && IsRetire(request))
            {
                answered = true;
                return Changed(null);
            }
            return null;
        };
        var putsBefore = server.For(plan.NewKey).Requests.Count(IsRosterPut);

        var result = await RosterRotation.FinishAsync(new HttpClient(server), Base, plan);

        Assert.True(result.OldKeyDeleted);
        Assert.True(old.Deleted);
        Assert.True(server.For(plan.NewKey).Requests.Count(IsRosterPut) > putsBefore);   // 写し直した
        Assert.Equal("リザードン", server.For(plan.NewKey).State.For(Ds).Pokemon.Single().PokemonName);
    }

    [Fact]
    public async Task A_roster_written_after_the_copy_is_copied_again_before_the_old_key_is_deleted()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン"));
        var old = server.For(oldKey);
        var plan = await RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey);
        var written = false;
        old.Override = request =>
        {
            // 古いキーを消す直前に、別の端末がポケモンを登録する（版が変わる）
            if (!written && IsRetire(request))
            {
                written = true;
                old.WriteFromElsewhere(state =>
                {
                    var set = state.For(Ds);
                    set.Pokemon.Add(new RegisteredPokemon { Id = 2, PokemonId = 445, PokemonName = "ガブリアス" });
                    set.NextId = 3;
                });
            }
            return null;
        };

        var result = await RosterRotation.FinishAsync(new HttpClient(server), Base, plan);

        Assert.True(result.OldKeyDeleted);
        Assert.Equal(new[] { "ガブリアス", "リザードン" },server.For(plan.NewKey).State.For(Ds).Pokemon.Select(p => p.PokemonName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_history_only_change_copies_the_new_matches_without_rewriting_the_roster()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン"));
        var old = server.For(oldKey);
        var plan = await RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey);
        var written = false;
        old.Override = request =>
        {
            // 消す直前に、別の端末が試合だけを足す（登録データの版は変わらない）
            if (!written && IsRetire(request))
            {
                written = true;
                AddMatchKeepingRevision(old, "あとから");
            }
            return null;
        };
        var rosterPutsBefore = server.For(plan.NewKey).Requests.Count(IsRosterPut);

        var result = await RosterRotation.FinishAsync(new HttpClient(server), Base, plan);

        Assert.True(result.OldKeyDeleted);
        Assert.Equal(0, server.For(plan.NewKey).Requests.Count(IsRosterPut) - rosterPutsBefore);   // 登録データは写し直さない
        Assert.Equal(new[] { "あとから" }, server.For(plan.NewKey).State.For(Ds).History.Select(m => m.Memo));
    }

    [Fact]
    public async Task Changes_that_keep_coming_stop_after_five_attempts_and_the_old_key_stays_readable()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン"));
        var old = server.For(oldKey);
        var plan = await RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey);
        var deletes = 0;
        old.Override = request =>
        {
            if (!IsRetire(request)) return null;
            deletes++;
            return Changed(999);   // 毎回、版が違う（写し直して、もう一度試すが、また変わっている）
        };

        var result = await RosterRotation.FinishAsync(new HttpClient(server), Base, plan);

        Assert.False(result.OldKeyDeleted);
        Assert.Equal(RosterRotationStop.RetriesExhausted, result.Stop);
        Assert.Equal(RosterRotation.MaxRetries, deletes);
        Assert.False(old.Deleted);
        var reread = await new RemoteRosterStore(new HttpClient(server), Base, oldKey).LoadAsync();
        Assert.Contains("リザードン", reread);
    }

    [Fact]
    public async Task A_failed_reread_of_the_old_roster_keeps_the_old_key()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン", withRival: true));
        var old = server.For(oldKey);
        var plan = await RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey);
        old.Override = request =>
        {
            if (IsRetire(request)) return Changed(999);
            // 写し直しのために読み直すと、相手チームの応答が想定と違う（読めないまま写すと、相手チームを空にしてしまう）
            return request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/my/rival" ? FakeRosterServer.Json(HttpStatusCode.OK, "{}") : null;
        };

        var result = await RosterRotation.FinishAsync(new HttpClient(server), Base, plan);

        Assert.False(result.OldKeyDeleted);
        Assert.Equal(RosterRotationStop.Failed, result.Stop);
        Assert.False(old.Deleted);
    }

    [Fact]
    public async Task A_copy_that_fails_removes_the_new_key_and_the_old_key_stays()
    {
        var server = new KeyRoutedServer();
        var oldKey = await SeedAsync(server, Roster("リザードン"));
        AddMatch(server.For(oldKey), "試合");
        var created = new List<string>();
        server.OnCreate = (key, bucket) =>
        {
            created.Add(key);
            // 新しいキーへの履歴の書き込みが失敗する
            bucket.Override = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/my/history"
                ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}")
                : null;
        };

        await Assert.ThrowsAsync<RosterUnavailableException>(() => RosterRotation.PrepareAsync(new HttpClient(server), Base, oldKey));

        var newKey = Assert.Single(created);
        Assert.True(server.For(newKey).Deleted);   // 使わない新しいキーの置き場は消す
        Assert.False(server.For(oldKey).Deleted);
    }

    [Fact]
    public async Task Discard_removes_the_new_key_and_reports_a_failure_when_it_cannot()
    {
        var server = new KeyRoutedServer();
        var key = RemoteRosterStore.GenerateKey();
        await new RemoteRosterStore(new HttpClient(server), Base, key).LoadAsync();

        Assert.True(await RosterRotation.DiscardAsync(new HttpClient(server), Base, key));
        Assert.True(server.For(key).Deleted);

        var stuck = RemoteRosterStore.GenerateKey();
        await new RemoteRosterStore(new HttpClient(server), Base, stuck).LoadAsync();
        server.For(stuck).Override = request => IsRetire(request) ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}") : null;

        Assert.False(await RosterRotation.DiscardAsync(new HttpClient(server), Base, stuck));
    }
}
