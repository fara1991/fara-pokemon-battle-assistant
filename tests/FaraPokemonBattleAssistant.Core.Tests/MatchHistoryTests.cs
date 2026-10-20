using System.Net;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>試合の履歴（クラウドでは登録データとは別の API。ブラウザ・ファイルでは登録データの中）。</summary>
public class MatchHistoryTests
{
    private const string Ds = "Gen9";
    private static readonly string Key = RemoteRosterStore.GenerateKey();

    private static (FakeRosterServer Server, RosterRepository Repo) NewCloud()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        return (server, new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key)));
    }

    private static Task AddRivalAsync(RosterRepository repo, string name) =>
        repo.ChangeRivalsAsync(Ds, roster =>
        {
            roster.Rivals.Add(new RivalPokemon { Id = roster.NextRivalId++, PokemonId = 6, PokemonName = name });
            return (0, true);
        });

    private static async Task<int> EndAsync(RosterRepository repo, string memo, string rival = "リザードン")
    {
        await AddRivalAsync(repo, rival);
        return (await repo.EndMatchAsync(Ds, memo, DateTimeOffset.Now)).Match!.Id;
    }

    [Fact]
    public async Task Cloud_end_match_appends_to_the_history_api_then_clears_the_rivals()
    {
        var (server, repo) = NewCloud();
        await AddRivalAsync(repo, "リザードン");
        server.Requests.Clear();
        var ended = await repo.EndMatchAsync(Ds, "  1 試合目 ", DateTimeOffset.Now);

        Assert.Equal(1, ended.Match!.Id);                       // Id はサーバーが割り当てる
        Assert.Equal("1 試合目", ended.Match.Memo);
        Assert.Equal(1, ended.Moved);
        var writes = server.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").ToList();
        Assert.Equal(new[] { "POST /api/my/history", "PUT /api/my/rival" }, writes);   // 先に履歴、そのあと相手を消す
        Assert.Empty(server.State.For(Ds).Rivals);
        Assert.Equal("リザードン", server.State.For(Ds).History.Single().Foes.Single().PokemonName);
    }

    [Fact]
    public async Task Cloud_history_is_read_in_pages_and_can_be_removed_and_trimmed()
    {
        var (_, repo) = NewCloud();
        for (var i = 1; i <= 5; i++) await EndAsync(repo, $"{i} 試合目");

        var first = await repo.ListHistoryAsync(Ds, 2);
        Assert.Equal(5, first.Total);
        Assert.Equal(new[] { 5, 4 }, first.Items.Select(m => m.Id));      // 新しい順
        Assert.True(first.HasMore);
        var next = await repo.ListHistoryAsync(Ds, 2, beforeId: 4);
        Assert.Equal(new[] { 3, 2 }, next.Items.Select(m => m.Id));
        var last = await repo.ListHistoryAsync(Ds, 2, beforeId: 2);
        Assert.Equal(new[] { 1 }, last.Items.Select(m => m.Id));
        Assert.False(last.HasMore);

        Assert.Equal("3 試合目", (await repo.GetMatchAsync(Ds, 3))!.Memo);
        Assert.Null(await repo.GetMatchAsync(Ds, 99));
        Assert.True(await repo.RemoveMatchAsync(Ds, 3));
        Assert.False(await repo.RemoveMatchAsync(Ds, 3));
        Assert.Equal(2, await repo.TrimHistoryAsync(Ds, 2));              // 新しい 2 件（5・4）を残す
        Assert.Equal(new[] { 5, 4 }, (await repo.ListHistoryAsync(Ds, 10)).Items.Select(m => m.Id));
        Assert.Equal(6, await EndAsync(repo, "6 試合目"));                 // Id は使い回さない
    }

    [Fact]
    public async Task Cloud_end_match_at_the_limit_fails_without_touching_the_rivals()
    {
        var (server, repo) = NewCloud();
        server.HistoryLimit = 1;
        await EndAsync(repo, "1 試合目");
        await AddRivalAsync(repo, "ハバタクカミ");
        var ex = await Assert.ThrowsAsync<HistoryLimitException>(() => repo.EndMatchAsync(Ds, "2 試合目", DateTimeOffset.Now));
        Assert.Contains("上限", ex.Message);
        Assert.Equal("ハバタクカミ", (await repo.GetAsync()).For(Ds).Rivals.Single().PokemonName);   // 対戦中の相手は残る
        Assert.Equal(1, server.State.For(Ds).History.Count);
        // 古い履歴を消せば、また終えられる
        await repo.TrimHistoryAsync(Ds, 0);
        Assert.Equal(2, (await repo.EndMatchAsync(Ds, "2 試合目", DateTimeOffset.Now)).Match!.Id);
    }

    [Fact]
    public async Task Cloud_end_match_tells_the_user_when_only_the_rival_clear_failed()
    {
        var (server, repo) = NewCloud();
        await AddRivalAsync(repo, "リザードン");
        server.Override = request => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/my/rival"
            ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}")
            : null;
        var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now));
        Assert.Contains("履歴 #1 に記録しました", ex.Message);       // 履歴には残っている。もう一度 end しない
        Assert.Single(server.State.For(Ds).History);
    }

    [Fact]
    public async Task Whole_save_moves_the_history_in_batches_oldest_first()
    {
        var (server, _) = NewCloud();
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key);
        var set = new DataSetRoster { NextMatchId = 1000 };
        // Id は飛んでいて、並びもばらばら
        foreach (var id in Enumerable.Range(1, 250).Reverse()) set.History.Add(new RivalMatch { Id = id * 3, Memo = $"m{id}", Foes = { new RivalPokemon { Id = 1, PokemonName = "a" } } });
        await store.SaveAsync(RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } }));

        var posts = server.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(3, posts.Count);                                    // 250 件 = 100 + 100 + 50
        var saved = server.State.For(Ds).History;
        Assert.Equal(250, saved.Count);
        Assert.Equal(Enumerable.Range(1, 250), saved.Select(m => m.Id));          // Id は振り直される
        Assert.Equal(Enumerable.Range(1, 250).Select(i => $"m{i}"), saved.Select(m => m.Memo));   // 古い順
    }

    [Fact]
    public async Task Old_clients_cannot_delete_history_through_the_rival_put_and_new_clients_do_not_send_it()
    {
        var (server, repo) = NewCloud();
        await EndAsync(repo, "1 試合目");
        server.Requests.Clear();
        await AddRivalAsync(repo, "ハバタクカミ");
        var put = server.Bodies.Last(b => b is not null)!;
        Assert.DoesNotContain("\"history\"", put);                 // 新しいクライアントは、相手チームの保存に履歴を付けない
        Assert.DoesNotContain("nextMatchId", put);
        Assert.Single(server.State.For(Ds).History);
    }

    // ---- ブラウザ・ファイル（履歴は登録データの中。上限は 2,000 件）

    [Fact]
    public async Task Local_history_is_listed_in_pages_and_can_be_removed_and_trimmed()
    {
        var repo = new RosterRepository(new MemoryRosterStore());
        for (var i = 1; i <= 4; i++) await EndAsync(repo, $"{i} 試合目");
        var page = await repo.ListHistoryAsync(Ds, 3);
        Assert.Equal(4, page.Total);
        Assert.Equal(new[] { 4, 3, 2 }, page.Items.Select(m => m.Id));
        Assert.True(page.HasMore);
        Assert.Equal(new[] { 1 }, (await repo.ListHistoryAsync(Ds, 3, beforeId: 2)).Items.Select(m => m.Id));
        Assert.Equal("2 試合目", (await repo.GetMatchAsync(Ds, 2))!.Memo);
        Assert.True(await repo.RemoveMatchAsync(Ds, 2));
        Assert.Equal(2, await repo.TrimHistoryAsync(Ds, 1));
        Assert.Equal(new[] { 4 }, (await repo.ListHistoryAsync(Ds, 10)).Items.Select(m => m.Id));
    }

    [Fact]
    public async Task Local_end_match_refuses_to_exceed_the_browser_limit_and_keeps_the_rivals()
    {
        var store = new MemoryRosterStore();
        var set = new DataSetRoster { NextMatchId = RosterRepository.LocalHistoryLimit + 1 };
        for (var i = 1; i <= RosterRepository.LocalHistoryLimit; i++) set.History.Add(new RivalMatch { Id = i });
        store.Json = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } });
        var repo = new RosterRepository(store);
        await AddRivalAsync(repo, "リザードン");
        var ex = await Assert.ThrowsAsync<HistoryLimitException>(() => repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now));
        Assert.Contains("2,000", ex.Message);
        var state = (await repo.GetAsync()).For(Ds);
        Assert.Single(state.Rivals);                                                   // 対戦中の相手は残る
        Assert.Equal(RosterRepository.LocalHistoryLimit, state.History.Count);
        // 古い履歴を整理すれば、また終えられる
        await repo.TrimHistoryAsync(Ds, 100);
        Assert.NotNull((await repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now)).Match);
    }
}
