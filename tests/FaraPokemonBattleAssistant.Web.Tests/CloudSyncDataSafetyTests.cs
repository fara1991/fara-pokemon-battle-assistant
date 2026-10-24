using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Tests;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>localStorage の代わりになるメモリ上の偽の JS 実行環境（getItem / setItem / removeItem だけ）。</summary>
internal sealed class FakeJsRuntime : IJSRuntime
{
    public Dictionary<string, string> Storage { get; } = new();
    /// <summary>true の間、getItem が失敗する（ブラウザが一時的に保存領域を読めない状況）。</summary>
    public bool FailGet { get; set; }
    public List<string> Calls { get; } = new();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Calls.Add(identifier);
        var key = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
        switch (identifier)
        {
            case "localStorage.getItem":
                if (FailGet) throw new JSException("storage is not available");
                return new ValueTask<TValue>((TValue)(object?)(Storage.TryGetValue(key, out var v) ? v : null)!);
            case "localStorage.setItem":
                Storage[key] = args![1]?.ToString() ?? "";
                break;
            case "localStorage.removeItem":
                Storage.Remove(key);
                break;
        }
        return new ValueTask<TValue>(default(TValue)!);
    }
}

/// <summary>データ共有の開始・参加で、このブラウザの登録データを失わないことの確認。</summary>
public class CloudSyncDataSafetyTests
{
    private const string Base = "https://pokemon.example/";
    private const string LocalKey = "fara-pokemon-battle-assistant.roster";

    private static string OnePokemonJson() => RosterRepository.Serialize(new RosterState
    {
        DataSets = { ["Gen9"] = new DataSetRoster { NextId = 2, Pokemon = { new RegisteredPokemon { Id = 1, PokemonId = 6, PokemonName = "リザードン" } } } },
    });

    private static (CloudSync Sync, FakeJsRuntime Js, FakeRosterServer Server, string Key) Create(bool localHasData = true)
    {
        var js = new FakeJsRuntime();
        if (localHasData) js.Storage[LocalKey] = OnePokemonJson();
        var key = RemoteRosterStore.GenerateKey();
        var server = new FakeRosterServer { ExpectedKey = key };
        var sync = new CloudSync(js, new HttpClient(server), Base, new ServiceCollection().AddSingleton(new RosterRepository(new MemoryRosterStore())).BuildServiceProvider());
        return (sync, js, server, key);
    }

    [Fact]
    public async Task Joining_an_empty_cloud_does_not_touch_local_data_until_confirmed()
    {
        var (sync, js, _, key) = Create();
        Assert.Equal(JoinResult.CloudEmpty, await sync.JoinAsync(key));
        Assert.True(js.Storage.ContainsKey(LocalKey));                  // ブラウザの登録データは残る
        Assert.False(js.Storage.ContainsKey(CloudSync.KeyStorageKey));  // 共有も始まらない
        Assert.False(sync.IsSyncing);
    }

    [Fact]
    public async Task Joining_an_empty_cloud_after_confirmation_clears_local_data_and_starts_sharing()
    {
        var (sync, js, _, key) = Create();
        Assert.Equal(JoinResult.JoinedEmpty, await sync.JoinAsync(key, allowEmptyCloud: true));
        Assert.False(js.Storage.ContainsKey(LocalKey));
        Assert.Equal(key, js.Storage[CloudSync.KeyStorageKey]);
        Assert.True(sync.IsSyncing);
    }

    [Fact]
    public async Task Joining_a_cloud_with_data_replaces_local_data()
    {
        var (sync, js, server, key) = Create();
        var cloud = new RemoteRosterStore(new HttpClient(server), Base, key);
        await cloud.SaveAsync(OnePokemonJson());
        Assert.Equal(JoinResult.Joined, await sync.JoinAsync(key));
        Assert.False(js.Storage.ContainsKey(LocalKey));
        Assert.True(sync.IsSyncing);
    }

    [Fact]
    public async Task Joining_an_empty_cloud_without_local_data_just_starts_sharing()
    {
        var (sync, js, _, key) = Create(localHasData: false);
        Assert.Equal(JoinResult.JoinedEmpty, await sync.JoinAsync(key));
        Assert.True(sync.IsSyncing);
        Assert.Equal(key, js.Storage[CloudSync.KeyStorageKey]);
    }

    [Fact]
    public async Task Unreadable_local_storage_blocks_sharing_instead_of_being_treated_as_empty()
    {
        var (sync, js, _, key) = Create();
        js.FailGet = true;
        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.JoinAsync(key, allowEmptyCloud: true));
        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.StartNewAsync());
        Assert.DoesNotContain("localStorage.removeItem", js.Calls);        // 何も消さない
        Assert.True(js.Storage.ContainsKey(LocalKey));
        Assert.False(sync.IsSyncing);
    }

    [Fact]
    public async Task Corrupted_local_data_is_assumed_to_exist_so_the_confirmation_is_shown()
    {
        var (sync, js, _, _) = Create(localHasData: false);
        js.Storage[LocalKey] = "{ not json";
        Assert.True(await sync.LocalHasDataAsync());
        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.StartNewAsync());   // 壊れたデータをクラウドに上げない・消さない
        Assert.Equal("{ not json", js.Storage[LocalKey]);
    }

    [Fact]
    public async Task Browser_store_refuses_to_save_or_clear_after_a_failed_read_even_if_a_later_read_works()
    {
        var js = new FakeJsRuntime();
        js.Storage[LocalKey] = OnePokemonJson();
        var store = new BrowserRosterStore(js);
        js.FailGet = true;
        Assert.Null(await store.LoadAsync());                  // 見るだけなら空として使える
        Assert.True(store.LoadFailed);
        js.FailGet = false;
        Assert.NotNull(await store.LoadAsync());               // 読めるようになっても、リポジトリのキャッシュは空のままなので
        await Assert.ThrowsAsync<RosterUnavailableException>(() => store.SaveAsync("{}"));      // 保存・削除はページを読み込み直すまで拒否する
        await Assert.ThrowsAsync<RosterUnavailableException>(() => store.ClearAsync());
        Assert.Equal(OnePokemonJson(), js.Storage[LocalKey]);
    }
}
