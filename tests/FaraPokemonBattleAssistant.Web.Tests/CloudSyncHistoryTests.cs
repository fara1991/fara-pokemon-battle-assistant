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
        if (!_buckets.TryGetValue(key, out var bucket)) _buckets[key] = bucket = new FakeRosterServer { ExpectedKey = key };
        return bucket;
    }

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

    private static async Task<(CloudSync Sync, FakeJsRuntime Js, KeyRoutedServer Server, string Key)> JoinedWithHistoryAsync(int matches)
    {
        var js = new FakeJsRuntime();
        var server = new KeyRoutedServer();
        var key = RemoteRosterStore.GenerateKey();
        var cloud = new RemoteRosterStore(new HttpClient(server), Base, key);
        var set = new DataSetRoster { NextId = 2, Pokemon = { new RegisteredPokemon { Id = 1, PokemonId = 6, PokemonName = "リザードン" } } };
        await cloud.SaveAsync(RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = set } }));
        await cloud.AppendManyAsync(Ds, Enumerable.Range(1, matches).Select(i => new RivalMatch { Memo = $"m{i}", Foes = { new RivalPokemon { Id = 1, PokemonName = "a" } } }).ToList());
        var sync = new CloudSync(js, new HttpClient(server), Base, new ServiceCollection().AddSingleton(new RosterRepository(new MemoryRosterStore())).BuildServiceProvider());
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
}
