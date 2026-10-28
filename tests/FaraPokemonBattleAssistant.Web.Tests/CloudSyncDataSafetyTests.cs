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

    /// <summary>データ共有の設定（キー）を読めないときは、ブラウザへ保存先を黙って切り替えず、保存・読み込みを止める。</summary>
    [Fact]
    public async Task Unreadable_sharing_key_stops_saves_instead_of_silently_writing_to_the_browser()
    {
        var (sync, js, _, key) = Create();
        js.Storage[CloudSync.KeyStorageKey] = key;
        js.FailGet = true;
        var before = js.Storage[LocalKey];

        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.SaveAsync(OnePokemonJson()));
        Assert.NotNull(sync.LastError);
        Assert.Equal(before, js.Storage[LocalKey]);   // ブラウザのデータへは書かない
    }

    /// <summary>正常な登録データ（ポケモン・有効なチーム）は、件数が合うので、共有の開始は成功し、ブラウザのデータは消える。</summary>
    [Fact]
    public async Task Starting_sharing_with_valid_pokemon_and_team_moves_everything_and_clears_local()
    {
        var local = RosterRepository.Deserialize(OnePokemonJson());
        local.DataSets["Gen9"].Teams.Add(new Team { Number = 1, Slots = { new TeamSlot { EntryId = 1 } } });
        var js = new FakeJsRuntime { Storage = { [LocalKey] = RosterRepository.Serialize(local) } };
        var server = new FakeRosterServer();
        var sync = new CloudSync(js, new HttpClient(server), Base, new ServiceCollection().AddSingleton(new RosterRepository(new MemoryRosterStore())).BuildServiceProvider());

        await sync.StartNewAsync();
        Assert.True(sync.IsSyncing);
        Assert.False(js.Storage.ContainsKey(LocalKey));
        var cloud = RosterRepository.Deserialize(server.RosterJson);
        Assert.Single(cloud.DataSets["Gen9"].Pokemon);
        Assert.Single(cloud.DataSets["Gen9"].Teams);
    }

    /// <summary>読むだけなら、共有の設定を読めなくても止めない（保存だけを止める）。</summary>
    [Fact]
    public async Task Reading_is_not_blocked_when_the_sharing_key_cannot_be_read()
    {
        var js = new FakeJsRuntime { Storage = { [LocalKey] = OnePokemonJson() }, FailGet = true };
        var store = new CloudSync(js, new HttpClient(new FakeRosterServer()), Base, new ServiceCollection().AddSingleton(new RosterRepository(new MemoryRosterStore())).BuildServiceProvider());
        await store.LoadAsync();                  // 例外にしない（ブラウザの内容が読めないときは空として扱う）
        Assert.NotNull(store.LastError);          // ただし、失敗は利用者に見せる
    }

    /// <summary>登録に無いポケモンを指す枠があるチームは、クラウドで削られる。そのまま共有を始めると、ブラウザのデータを消して中身が減るので、中止する。</summary>
    [Fact]
    public async Task Starting_sharing_keeps_local_data_when_the_cloud_would_drop_a_team()
    {
        var local = RosterRepository.Deserialize(OnePokemonJson());
        local.DataSets["Gen9"].Teams.Add(new Team { Number = 1, Slots = { new TeamSlot { EntryId = 99 } } });
        var before = RosterRepository.Serialize(local);
        var js = new FakeJsRuntime { Storage = { [LocalKey] = before } };
        var server = new FakeRosterServer();
        var sync = new CloudSync(js, new HttpClient(server), Base, new ServiceCollection().AddSingleton(new RosterRepository(new MemoryRosterStore())).BuildServiceProvider());

        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.StartNewAsync());
        Assert.Equal(before, js.Storage[LocalKey]);                       // ブラウザのデータは残る
        Assert.False(js.Storage.ContainsKey(CloudSync.KeyStorageKey));    // 共有は始まらない
        Assert.False(sync.IsSyncing);
    }

}
