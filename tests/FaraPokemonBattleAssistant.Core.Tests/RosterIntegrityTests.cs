using System.Net;
using System.Text.Json;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>コードレビューで見つかった、登録データの保存・読み込みの整合性の問題の回帰テスト。</summary>
public class RosterIntegrityTests
{
    private const string Ds = "Gen9";

    /// <summary>保存だけを失敗させる保存先（ファイル・ブラウザの保存先と同じ AlwaysReload=false）。</summary>
    private sealed class FailingStore : IRosterStore
    {
        public string? Json { get; set; }
        public bool FailSave { get; set; }
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Json);
        public Task SaveAsync(string json, CancellationToken ct = default) => FailSave ? throw new IOException("disk full") : Task.Run(() => { Json = json; });
        public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
            SaveAsync(RosterRepository.Serialize(state), ct);
    }

    [Fact]
    public async Task A_failed_save_does_not_leave_the_change_in_memory_for_local_stores()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var store = new FailingStore();
        var repo = new RosterRepository(store);
        var pokemon = data.Pokemon.First(p => p.Name == "ガブリアス");
        await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), data.Natures.First(), null);

        store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => repo.AddPokemonAsync(Ds, pokemon, new StatSet(), data.Natures.First(), null));
        // 「失敗した」と伝えた変更が、次の読み込みや保存に混ざらない
        Assert.Single((await repo.GetAsync()).For(Ds).Pokemon);
        store.FailSave = false;
        await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), data.Natures.First(), null);
        Assert.Equal(2, (await repo.GetAsync()).For(Ds).Pokemon.Count);
    }

    [Fact]
    public void Loaded_state_keeps_case_insensitive_keys_and_repairs_ids()
    {
        var json = """
        {"Version":1,"DataSets":{"Gen9":{"NextId":1,"Pokemon":[{"Id":5,"PokemonId":445,"PokemonName":"ガブリアス","Nature":"ようき"},{"Id":5,"PokemonId":6,"PokemonName":"リザードン","Nature":"ようき"}],
          "NextRivalId":1,"Rivals":[{"Id":3,"PokemonId":6,"PokemonName":"リザードン"}]}}}
        """;
        var state = RosterRepository.Deserialize(json);
        Assert.True(state.DataSets.ContainsKey("gen9"));   // 大文字小文字を区別しない
        var roster = state.For("GEN9");
        Assert.Equal(6, roster.NextId);                     // 今ある最大の ID（5）の次
        Assert.Equal(4, roster.NextRivalId);
    }

    [Fact]
    public async Task Duplicate_ids_in_stored_data_do_not_break_the_choice_list()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var store = new MemoryRosterStore { Json = """
        {"Version":1,"DataSets":{"Gen9":{"NextId":9,"Pokemon":[{"Id":1,"PokemonId":445,"PokemonName":"ガブリアス","Nature":"ようき"},{"Id":1,"PokemonId":6,"PokemonName":"リザードン","Nature":"ようき"}],
          "Teams":[{"Number":1,"Slots":[{"EntryId":1}]}]}}}
        """ };
        var repo = new RosterRepository(store);
        var groups = await repo.ChoiceGroupsAsync(Ds, data);   // 以前は ToDictionary が ArgumentException で落ちた
        Assert.NotEmpty(groups);
    }

    [Fact]
    public async Task Unknown_fields_survive_a_load_and_save_round_trip()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var store = new MemoryRosterStore { Json = """
        {"Version":1,"FutureTop":true,"DataSets":{"Gen9":{"NextId":2,"FutureSet":[1,2],"Pokemon":[{"Id":1,"PokemonId":445,"PokemonName":"ガブリアス","Nature":"ようき","Nickname":"ガブ"}]}}}
        """ };
        var repo = new RosterRepository(store);
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "リザードン"), new StatSet(), data.Natures.First(), null);
        // 新しい版が足した項目を、古い版の保存で消さない
        using var doc = JsonDocument.Parse(store.Json!);
        Assert.True(doc.RootElement.TryGetProperty("FutureTop", out _));
        var set = doc.RootElement.GetProperty("DataSets").GetProperty("Gen9");
        Assert.True(set.TryGetProperty("FutureSet", out _));
        Assert.Equal("ガブ", set.GetProperty("Pokemon")[0].GetProperty("Nickname").GetString());
    }

    [Fact]
    public async Task Resolving_the_default_data_set_key_returns_the_canonical_key()
    {
        var catalog = TestData.Catalog();
        Assert.Equal("Gen9", await catalog.ResolveDefaultKeyAsync("gen9"));
    }

    [Theory]
    [InlineData("http://pokemon.example/")]
    [InlineData("ftp://pokemon.example/")]
    public void Remote_store_refuses_plain_http_and_other_schemes_except_localhost(string url)
    {
        Assert.Throws<ArgumentException>(() => new RemoteRosterStore(new HttpClient(), url, RemoteRosterStore.GenerateKey()));
    }

    [Fact]
    public void Remote_store_allows_plain_http_for_localhost()
    {
        _ = new RemoteRosterStore(new HttpClient(), "http://localhost:5199/", RemoteRosterStore.GenerateKey());
    }

    [Fact]
    public async Task A_rival_response_with_the_wrong_shape_does_not_break_loading_pokemon_and_blocks_saving_rivals()
    {
        var key = RemoteRosterStore.GenerateKey();
        var server = new FakeRosterServer { ExpectedKey = key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", key);
        // 相手チームの応答だけ、配列の無い 200
        server.Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/my/rival"
            ? FakeRosterServer.Json(HttpStatusCode.OK, """{"revision":0,"dataSets":{"Gen9":{"nextId":1}}}""") : null;
        await store.LoadAsync();   // 例外にならない
        Assert.True(store.RivalUnreadable);
    }

    [Fact]
    public async Task Out_of_range_numbers_from_the_server_are_reported_as_an_invalid_response()
    {
        var key = RemoteRosterStore.GenerateKey();
        var server = new FakeRosterServer { ExpectedKey = key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", key);
        server.Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/my/pokemon"
            ? FakeRosterServer.Json(HttpStatusCode.OK, """{"revision":0,"dataSets":{"Gen9":{"nextId":99999999999,"pokemon":[]}}}""") : null;
        var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync());
        Assert.Equal(RosterUnavailableReason.InvalidResponse, ex.Reason);
    }
}
