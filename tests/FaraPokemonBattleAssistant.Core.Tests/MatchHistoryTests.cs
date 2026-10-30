using System.Net;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>試合の履歴（クラウドでは登録データとは別の API。ブラウザ・ファイルでは登録データの中）。</summary>
public sealed class MatchHistoryTests : IDisposable
{
    private readonly TestResources _owned = new();

    public void Dispose() => _owned.Dispose();

    private const string Ds = "Gen9";
    private static readonly string Key = RemoteRosterStore.GenerateKey();

    private (FakeRosterServer Server, RosterRepository Repo) NewCloud()
    {
        var server = _owned.Own(new FakeRosterServer { ExpectedKey = Key });
        return (server, new RosterRepository(new RemoteRosterStore(_owned.Own(new HttpClient(server)), "https://pokemon.example/", Key)));
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
        Assert.Single(server.State.For(Ds).History);
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
        Assert.Contains("履歴 #1 に記録しました", ex.Message);       // 履歴には残っている
        Assert.Contains("もう一度 end", ex.Message);
        Assert.Single(server.State.For(Ds).History);
    }

    [Fact]
    public async Task Ending_again_after_the_rival_clear_failed_links_to_the_recorded_match_without_duplicating()
    {
        var (server, repo) = NewCloud();
        await AddRivalAsync(repo, "リザードン");
        server.Override = request => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/my/rival"
            ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}")
            : null;
        await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now));
        server.Override = null;

        var ended = await repo.EndMatchAsync(Ds, "やり直し", DateTimeOffset.Now);

        Assert.Equal(1, ended.Match!.Id);                              // 記録済みの試合に紐づく
        Assert.Single(server.State.For(Ds).History);                   // 二重にならない
        Assert.Equal("x", server.State.For(Ds).History.Single().Memo); // 内容は最初のまま
        Assert.Empty(server.State.For(Ds).Rivals);                     // 相手は消える
    }

    [Fact]
    public async Task Ending_again_after_the_append_response_was_lost_does_not_record_twice()
    {
        using var server = new FakeRosterServer { ExpectedKey = Key };
        using var lossy = new LostResponseHandler(server);
        using var lossyHttp = new HttpClient(lossy);
        var repo = new RosterRepository(new RemoteRosterStore(lossyHttp, "https://pokemon.example/", Key));
        await AddRivalAsync(repo, "リザードン");
        lossy.LoseNextHistoryPost = true;   // 履歴は記録されるが、応答が届かない
        await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now));

        var ended = await repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now);

        Assert.Equal(1, ended.Match!.Id);
        Assert.Single(server.State.For(Ds).History);
        Assert.Empty(server.State.For(Ds).Rivals);
    }

    [Fact]
    public async Task Whole_save_moves_the_history_in_batches_oldest_first()
    {
        var (server, _) = NewCloud();
        using var http = new HttpClient(server);
        var store = new RemoteRosterStore(http, "https://pokemon.example/", Key);
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


    // ---- レビューで見つかった抜け（まとめて追加の大きさ・文書にないゲーム・応答の検証・途中で足された相手）

    private static RivalMatch BigMatch(int kb) => new()
    {
        Memo = "big",
        Foes = { new RivalPokemon { Id = 1, PokemonName = "a", Scans = { new RivalScan { Defense = "Defense", Notes = { new string('x', kb * 1024) } } } } },
    };

    [Fact]
    public async Task Batches_are_split_by_size_so_that_no_request_exceeds_the_server_body_limit()
    {
        var (server, _) = NewCloud();
        using var http = new HttpClient(server);
        var store = new RemoteRosterStore(http, "https://pokemon.example/", Key);
        var matches = Enumerable.Range(1, 40).Select(_ => BigMatch(30)).ToList();   // 30 KB × 40 件 = 1.2 MB（件数だけで 100 件ずつ区切ると 1 回が 3 MB）
        await store.AppendManyAsync(Ds, matches);
        Assert.Equal(40, server.State.For(Ds).History.Count);
        var posts = server.Bodies.Where(b => b is not null).ToList();
        Assert.All(posts, body => Assert.True(System.Text.Encoding.UTF8.GetByteCount(body!) <= 256 * 1024));
        Assert.True(posts.Count > 1);
    }

    [Fact]
    public async Task A_match_over_the_server_limit_is_sent_without_its_scan_notes()
    {
        var (server, _) = NewCloud();
        using var http = new HttpClient(server);
        var store = new RemoteRosterStore(http, "https://pokemon.example/", Key);
        await store.AppendManyAsync(Ds, new[] { BigMatch(80) });                    // 80 KB（以前のブラウザ保存には 1 試合の上限が無かった）
        var saved = server.State.For(Ds).History.Single();
        Assert.Equal("big", saved.Memo);
        Assert.Empty(saved.Foes.Single().Scans);                                    // 推定の根拠だけを省いて、試合は移る
    }

    [Fact]
    public async Task Only_matches_over_the_server_limit_lose_content_and_the_count_is_returned()
    {
        var (server, _) = NewCloud();
        using var http = new HttpClient(server);
        var store = new RemoteRosterStore(http, "https://pokemon.example/", Key);
        // 62 KB（サーバーの 64 KB に収まる）は、そのまま移る。80 KB は省く。メモが 501 文字の試合は切る
        var okMatch = BigMatch(62);
        var tooBig = BigMatch(80);
        var longMemo = new RivalMatch { Memo = new string('あ', 501), Foes = { new RivalPokemon { Id = 1, PokemonName = "a" } } };
        var trimmed = await store.AppendManyAsync(Ds, new[] { okMatch, tooBig, longMemo });
        Assert.Equal(2, trimmed);
        var saved = server.State.For(Ds).History;
        Assert.NotEmpty(saved[0].Foes.Single().Scans);                              // 62 KB の試合の根拠は残る
        Assert.Empty(saved[1].Foes.Single().Scans);                                 // 80 KB の試合だけ、根拠を省く
        Assert.Equal(500, saved[2].Memo.Length);                                    // メモは 500 文字まで
        Assert.Equal(0, await store.AppendManyAsync(Ds, new[] { okMatch }));
    }

    /// <summary>メモを切る位置が、サロゲートペア（絵文字など）の途中にならない（途中で切ると、不正な文字になって保存が壊れる）。</summary>
    [Fact]
    public async Task Trimming_a_memo_never_splits_a_surrogate_pair()
    {
        var (server, _) = NewCloud();
        using var http = new HttpClient(server);
        var store = new RemoteRosterStore(http, "https://pokemon.example/", Key);
        // 499 文字の後に絵文字（2 文字ぶん）があるメモ。500 文字目で切ると、絵文字が半分になる
        var memo = new string('a', 499) + "😀";
        var trimmed = await store.AppendManyAsync(Ds, new[] { new RivalMatch { Memo = memo, Foes = { new RivalPokemon { Id = 1, PokemonName = "a" } } } });
        Assert.Equal(1, trimmed);
        var saved = server.State.For(Ds).History.Single().Memo;
        Assert.Equal(new string('a', 499), saved);   // 絵文字の半分を残さず、その手前で切る
    }

    [Fact]
    public async Task Cloud_end_match_keeps_rivals_that_were_added_while_it_was_running()
    {
        var (server, repo) = NewCloud();
        await AddRivalAsync(repo, "リザードン");
        // 履歴に追記している間に、別の端末（BOT）が相手を足す
        server.Override = request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/my/history")
            {
                server.Override = null;
                server.WriteFromElsewhere(state => { var roster = state.For(Ds); roster.Rivals.Add(new RivalPokemon { Id = roster.NextRivalId++, PokemonId = 445, PokemonName = "ガブリアス" }); });
            }
            return null;
        };
        var ended = await repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now);
        Assert.Equal(1, ended.Moved);                                               // 記録したのは、読んだ時点の相手だけ
        Assert.Equal("ガブリアス", (await repo.GetAsync()).For(Ds).Rivals.Single().PokemonName);   // あとから足された相手は消えずに残る
    }

    [Fact]
    public async Task Two_end_calls_at_the_same_time_record_the_match_only_once()
    {
        var (server, repo) = NewCloud();
        await AddRivalAsync(repo, "リザードン");
        var results = await Task.WhenAll(repo.EndMatchAsync(Ds, "a", DateTimeOffset.Now), repo.EndMatchAsync(Ds, "b", DateTimeOffset.Now));
        Assert.Equal(1, results.Count(r => r.Match is not null));
        Assert.Single(server.State.For(Ds).History);
    }

    [Fact]
    public async Task A_history_page_that_does_not_move_forward_is_rejected()
    {
        var (server, repo) = NewCloud();
        for (var i = 0; i < 3; i++) await EndAsync(repo, $"{i}");
        // サーバーが、頼んだ before より後ろの試合（同じページ）を返し続ける
        server.Override = request => request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("before=")
            ? FakeRosterServer.Json(HttpStatusCode.OK, """{"total":3,"items":[{"Id":3,"Foes":[]},{"Id":2,"Foes":[]}],"hasMore":true}""")
            : null;
        await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.ListHistoryAsync(Ds, 2, beforeId: 2));
        server.Override = request => request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("limit=")
            ? FakeRosterServer.Json(HttpStatusCode.OK, """{"total":3,"items":[{"Id":1,"Foes":[]},{"Id":2,"Foes":[]}],"hasMore":false}""")   // 新しい順になっていない
            : null;
        await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.ListHistoryAsync(Ds, 5));
    }

    [Fact]
    public async Task Data_set_totals_list_the_games_that_have_history()
    {
        var (_, repo) = NewCloud();
        await EndAsync(repo, "1");
        await EndAsync(repo, "2");
        var totals = await ((RemoteRosterStore)(await repo.Store.GetRemoteHistoryAsync())!).DataSetTotalsAsync();
        Assert.Equal(new HistoryTotal(2, 3), totals["gen9"]);
        Assert.Single(totals);
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

    /// <summary>ブラウザの localStorage と同じく、履歴に上限があるメモリ上の保存先。</summary>
    private sealed class BrowserLikeStore : IRosterStore
    {
        private readonly MemoryRosterStore _inner = new();
        public int? LocalHistoryLimit => RosterRepository.BrowserHistoryLimit;
        public string? Json { get => _inner.Json; set => _inner.Json = value; }
        public Task<string?> LoadAsync(CancellationToken ct = default) => _inner.LoadAsync(ct);
        public Task SaveAsync(string json, CancellationToken ct = default) => _inner.SaveAsync(json, ct);
        public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) => _inner.SavePartAsync(state, dataSetKey, part, ct);
    }

    [Fact]
    public async Task File_and_memory_stores_have_no_history_limit()
    {
        var store = new MemoryRosterStore();
        var set = new DataSetRoster { NextMatchId = RosterRepository.BrowserHistoryLimit + 1 };
        for (var i = 1; i <= RosterRepository.BrowserHistoryLimit; i++) set.History.Add(new RivalMatch { Id = i });
        store.Json = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } });
        var repo = new RosterRepository(store);
        await AddRivalAsync(repo, "リザードン");
        Assert.NotNull((await repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now)).Match);   // BOT（ファイル保存）の end は上限で断られない
    }

    [Fact]
    public async Task Local_end_match_refuses_to_exceed_the_browser_limit_and_keeps_the_rivals()
    {
        var store = new BrowserLikeStore();
        var set = new DataSetRoster { NextMatchId = RosterRepository.BrowserHistoryLimit + 1 };
        for (var i = 1; i <= RosterRepository.BrowserHistoryLimit; i++) set.History.Add(new RivalMatch { Id = i });
        store.Json = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } });
        var repo = new RosterRepository(store);
        await AddRivalAsync(repo, "リザードン");
        var ex = await Assert.ThrowsAsync<HistoryLimitException>(() => repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now));
        Assert.Contains("2,000", ex.Message);
        var state = (await repo.GetAsync()).For(Ds);
        Assert.Single(state.Rivals);                                                   // 対戦中の相手は残る
        Assert.Equal(RosterRepository.BrowserHistoryLimit, state.History.Count);
        // 古い履歴を整理すれば、また終えられる
        await repo.TrimHistoryAsync(Ds, 100);
        Assert.NotNull((await repo.EndMatchAsync(Ds, "x", DateTimeOffset.Now)).Match);
    }
}

/// <summary>履歴の追加を本体に通したあと、応答を捨てて通信エラーにする（応答が届かなかった状況を作る）。</summary>
internal sealed class LostResponseHandler(HttpMessageHandler inner) : HttpMessageHandler
{
    public bool LoseNextHistoryPost { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var invoker = new HttpMessageInvoker(inner, disposeHandler: false);   // inner は呼び出し側のもの（ここでは破棄しない）
        var response = await invoker.SendAsync(request, cancellationToken);
        if (LoseNextHistoryPost && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/my/history")
        {
            LoseNextHistoryPost = false;
            throw new HttpRequestException("lost");
        }
        return response;
    }
}
