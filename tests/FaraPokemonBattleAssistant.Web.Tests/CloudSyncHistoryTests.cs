using System.Net;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Tests;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>アクセスキーごとに別の置き場を持つ偽サーバー（キーの作り直しで、新しいキーの空の置き場に移せるように）。</summary>
internal sealed class KeyRoutedServer : HttpMessageHandler
{
    private readonly Dictionary<string, FakeRosterServer> _buckets = new();

    public FakeRosterServer For(string key)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            _buckets[key] = bucket = new FakeRosterServer { ExpectedKey = key };
            OnCreate?.Invoke(key, bucket);
        }
        return bucket;
    }

    /// <summary>新しいアクセスキーの置き場を作ったときに呼ぶ（その置き場の動作を変えるため）。</summary>
    public Action<string, FakeRosterServer>? OnCreate { get; set; }

    /// <summary>true の間、すべての要求が接続エラーになる。</summary>
    public bool Offline { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline) throw new HttpRequestException("offline");
        var key = request.Headers.Authorization?.Parameter ?? "";
        return For(key).HandleAsync(request, ct);
    }
}

/// <summary>キーの作り直しで履歴を新しいキーへ写すこと・データ共有の解除でクラウドのデータを消すこと。</summary>
public class CloudSyncHistoryTests
{
    private const string Base = "https://pokemon.example/";
    private const string Ds = "Gen9";

    private static CloudSync NewSync(FakeJsRuntime js, KeyRoutedServer server) =>
        new(js, new HttpClient(server), Base, new ServiceCollection().AddSingleton(new RosterRepository(new MemoryRosterStore())).BuildServiceProvider());

    private static async Task<(CloudSync Sync, FakeJsRuntime Js, KeyRoutedServer Server, string Key)> JoinedWithHistoryAsync(int matches)
    {
        var js = new FakeJsRuntime();
        var server = new KeyRoutedServer();
        var key = RemoteRosterStore.GenerateKey();
        var cloud = new RemoteRosterStore(new HttpClient(server), Base, key);
        var set = new DataSetRoster { NextId = 2, Pokemon = { new RegisteredPokemon { Id = 1, PokemonId = 6, PokemonName = "リザードン" } } };
        await cloud.SaveAsync(RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } }));
        await cloud.AppendManyAsync(Ds, Enumerable.Range(1, matches).Select(i => new RivalMatch { Memo = $"m{i}", Foes = { new RivalPokemon { Id = 1, PokemonName = "a" } } }).ToList());
        var sync = NewSync(js, server);
        Assert.Equal(JoinResult.Joined, await sync.JoinAsync(key));
        return (sync, js, server, key);
    }

    [Fact]
    public async Task Rotating_the_key_copies_all_the_history_oldest_first_and_retires_the_old_key()
    {
        var (sync, js, server, oldKey) = await JoinedWithHistoryAsync(450);   // 200 件ずつ 3 ページ・100 件ずつ 5 回

        Assert.True(await sync.RotateAsync());

        var newKey = sync.Key!;
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(newKey, js.Storage[CloudSync.KeyStorageKey]);
        var copied = server.For(newKey).State.For(Ds);
        Assert.Equal("リザードン", copied.Pokemon.Single().PokemonName);        // 登録データも移る
        Assert.Equal(450, copied.History.Count);                                   // 履歴は全件
        Assert.Equal(Enumerable.Range(1, 450).Select(i => $"m{i}"), copied.History.Select(m => m.Memo));   // 古い順のまま
        Assert.Equal(Enumerable.Range(1, 450), copied.History.Select(m => m.Id));
        Assert.True(server.For(oldKey).Deleted);                                   // 古いキーは使えず、履歴も消える
    }

    [Fact]
    public async Task Stopping_sharing_deletes_the_cloud_data_and_forgets_the_key()
    {
        var (sync, js, server, key) = await JoinedWithHistoryAsync(3);

        await sync.StopAsync();

        Assert.True(server.For(key).Deleted);                                      // クラウドのデータ（履歴を含む）は削除される
        Assert.False(js.Storage.ContainsKey(CloudSync.KeyStorageKey));
        Assert.False(sync.IsSyncing);
        Assert.Null(sync.Key);
    }

    [Fact]
    public async Task Stopping_sharing_keeps_the_key_when_the_cloud_cannot_be_reached()
    {
        var (sync, js, server, key) = await JoinedWithHistoryAsync(3);
        server.Offline = true;

        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.StopAsync());

        Assert.True(sync.IsSyncing);                                               // もう一度試せるように、キーは忘れない
        Assert.Equal(key, js.Storage[CloudSync.KeyStorageKey]);
        Assert.False(server.For(key).Deleted);
        server.Offline = false;
        await sync.StopAsync();
        Assert.True(server.For(key).Deleted);
    }

    [Fact]
    public async Task Stopping_sharing_works_even_if_the_key_was_already_retired_elsewhere()
    {
        var (sync, js, server, key) = await JoinedWithHistoryAsync(1);
        server.For(key).Override = request => request.Method == HttpMethod.Delete ? FakeRosterServer.Json(HttpStatusCode.Gone, """{"error":"key_retired"}""") : null;

        await sync.StopAsync();                                                    // すでに別の場所で消えていても、このブラウザでは解除できる

        Assert.False(sync.IsSyncing);
        Assert.False(js.Storage.ContainsKey(CloudSync.KeyStorageKey));
    }

    [Fact]
    public async Task Rotating_the_key_keeps_the_scan_notes_of_a_match_that_fits_the_server_limit()
    {
        var js = new FakeJsRuntime();
        var server = new KeyRoutedServer();
        var oldKey = RemoteRosterStore.GenerateKey();
        var cloud = new RemoteRosterStore(new HttpClient(server), Base, oldKey);
        var scans = new RivalPokemon { Id = 1, PokemonName = "a", Scans = { new RivalScan { Defense = "Defense", Notes = { new string('x', 62 * 1024) } } } };
        await cloud.AppendAsync(Ds, new RivalMatch { Memo = "62KB", Foes = { scans } });     // 単発の追記（end）と同じ経路
        var sync = NewSync(js, server);
        await sync.JoinAsync(oldKey);

        Assert.True(await sync.RotateAsync());

        var copied = server.For(sync.Key!).State.For(Ds).History.Single();
        Assert.NotEmpty(copied.Foes.Single().Scans);                                // 推定の根拠は、黙って消えない
        Assert.Null(sync.LastNotice);
    }

    [Fact]
    public async Task Starting_sharing_tells_the_user_when_a_match_had_to_be_trimmed()
    {
        var js = new FakeJsRuntime();
        var set = new DataSetRoster { NextMatchId = 2 };
        set.History.Add(new RivalMatch { Id = 1, Foes = { new RivalPokemon { Id = 1, PokemonName = "a", Scans = { new RivalScan { Defense = "Defense", Notes = { new string('x', 80 * 1024) } } } } } });
        js.Storage["fara-pokemon-battle-assistant.roster"] = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } });
        var server = new KeyRoutedServer();
        var sync = NewSync(js, server);

        await sync.StartNewAsync();

        Assert.Contains("1 件", sync.LastNotice);
        Assert.Empty(server.For(sync.Key!).State.For(Ds).History.Single().Foes.Single().Scans);
    }

    [Fact]
    public async Task Rotating_the_key_also_copies_history_of_a_game_that_has_no_roster_document()
    {
        // 登録データ（文書）が空で、履歴だけがあるゲーム（外部の API 利用など）
        var js = new FakeJsRuntime();
        var server = new KeyRoutedServer();
        var oldKey = RemoteRosterStore.GenerateKey();
        var cloud = new RemoteRosterStore(new HttpClient(server), Base, oldKey);
        await cloud.AppendManyAsync("onlyhistory", Enumerable.Range(1, 5).Select(i => new RivalMatch { Memo = $"h{i}" }).ToList());
        var sync = NewSync(js, server);
        await sync.JoinAsync(oldKey);

        Assert.True(await sync.RotateAsync());

        var copied = server.For(sync.Key!).State.For("onlyhistory").History;
        Assert.Equal(new[] { "h1", "h2", "h3", "h4", "h5" }, copied.Select(m => m.Memo));
        Assert.True(server.For(oldKey).Deleted);
    }

    [Fact]
    public async Task Rotation_that_fails_while_copying_keeps_the_old_key_and_its_history()
    {
        var (sync, js, server, oldKey) = await JoinedWithHistoryAsync(5);
        server.OnCreate = (key, bucket) => bucket.Override = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/my/history"
            ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}")
            : null;

        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.RotateAsync());

        Assert.Equal(oldKey, sync.Key);                                             // キーは切り替わらない
        Assert.Equal(oldKey, js.Storage[CloudSync.KeyStorageKey]);
        Assert.False(server.For(oldKey).Deleted);                                   // 古いキーと履歴は残る
        Assert.Equal(5, server.For(oldKey).State.For(Ds).History.Count);
    }

    [Fact]
    public async Task Rotation_copies_matches_added_to_the_old_key_before_it_is_deleted()
    {
        var (sync, js, server, oldKey) = await JoinedWithHistoryAsync(3);
        var old = server.For(oldKey);
        var added = false;
        old.Override = request =>
        {
            // 古いキーを消す直前に、別の端末が試合を足す（サーバーは状態の違いで 409 を返す）
            if (!added && request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/api/my/access-key")
            {
                added = true;
                old.WriteFromElsewhere(state =>
                {
                    var set = state.For(Ds);
                    set.History.Add(new RivalMatch { Id = set.NextMatchId, Memo = "late" });
                    set.NextMatchId++;
                });
            }
            return null;
        };

        Assert.True(await sync.RotateAsync());

        Assert.NotEqual(oldKey, sync.Key);
        Assert.True(old.Deleted);
        var copied = server.For(sync.Key!).State.For(Ds).History;
        Assert.Equal(4, copied.Count);
        Assert.Contains(copied, m => m.Memo == "late");   // 足された試合も新しいキーに残る
    }

    [Fact]
    public async Task Rotation_that_cannot_delete_the_old_key_reports_failure_but_keeps_the_new_key()
    {
        var (sync, js, server, oldKey) = await JoinedWithHistoryAsync(3);
        var old = server.For(oldKey);
        old.Override = request => request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/api/my/access-key"
            ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}")
            : null;

        Assert.False(await sync.RotateAsync());

        Assert.NotEqual(oldKey, sync.Key);                                          // 新しいキーへ切り替わっている
        Assert.Equal(3, server.For(sync.Key!).State.For(Ds).History.Count);
        Assert.False(old.Deleted);
    }

    [Fact]
    public async Task Starting_sharing_moves_all_the_local_history_and_clears_the_browser()
    {
        var js = new FakeJsRuntime();
        var set = new DataSetRoster { NextId = 2, Pokemon = { new RegisteredPokemon { Id = 1, PokemonId = 6, PokemonName = "リザードン" } }, NextMatchId = 4 };
        foreach (var i in new[] { 3, 1, 2 }) set.History.Add(new RivalMatch { Id = i, Memo = $"m{i}" });
        js.Storage["fara-pokemon-battle-assistant.roster"] = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } });
        var server = new KeyRoutedServer();
        var sync = NewSync(js, server);

        await sync.StartNewAsync();

        var saved = server.For(sync.Key!).State.For(Ds);
        Assert.Equal(new[] { "m1", "m2", "m3" }, saved.History.Select(m => m.Memo));   // 古い順に移る
        Assert.False(js.Storage.ContainsKey("fara-pokemon-battle-assistant.roster"));
    }

    [Fact]
    public async Task Starting_sharing_that_fails_keeps_the_browser_data_and_discards_the_new_key()
    {
        var js = new FakeJsRuntime();
        var set = new DataSetRoster { NextMatchId = 2 };
        set.History.Add(new RivalMatch { Id = 1, Memo = "m1" });
        var local = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } });
        js.Storage["fara-pokemon-battle-assistant.roster"] = local;
        var server = new KeyRoutedServer();
        string? newKey = null;
        server.OnCreate = (key, bucket) =>
        {
            newKey = key;
            bucket.Override = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/my/history"
                ? FakeRosterServer.Json(HttpStatusCode.RequestEntityTooLarge, "{}")
                : null;
        };
        var sync = NewSync(js, server);

        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.StartNewAsync());

        Assert.Equal(local, js.Storage["fara-pokemon-battle-assistant.roster"]);   // ブラウザのデータは残る
        Assert.False(sync.IsSyncing);
        Assert.True(server.For(newKey!).Deleted);                                   // 新しいキーは使わずに捨てる
    }
}
