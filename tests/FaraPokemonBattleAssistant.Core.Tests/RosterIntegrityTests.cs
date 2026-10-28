using System.Net;
using System.Text.Json;
using FaraPokemonBattleAssistant.Core.Models;
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
        Assert.Equal(new[] { 5, 6 }, roster.Pokemon.Select(p => p.Id).ToArray());   // 重なった ID は、2 つ目に新しい ID を振る
        Assert.Equal(7, roster.NextId);
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
        var groups = await repo.ChoiceGroupsAsync(Ds, data, BattleFormat.Singles);   // 以前は ToDictionary が ArgumentException で落ちた
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

    [Fact]
    public void Keys_that_differ_only_by_case_are_merged_and_null_lists_are_tolerated()
    {
        var json = """
        {"Version":1,"DataSets":{
          "Gen9":{"NextId":2,"Pokemon":[{"Id":1,"PokemonId":445,"PokemonName":"ガブリアス","Nature":"ようき"}]},
          "gen9":{"NextId":3,"Pokemon":[{"Id":2,"PokemonId":6,"PokemonName":"リザードン","Nature":"ようき"}],"Rivals":null,"History":null}}}
        """;
        var state = RosterRepository.Deserialize(json);
        var roster = state.For("Gen9");
        Assert.Equal(2, roster.Pokemon.Count);   // 片方を黙って捨てない
        Assert.Empty(roster.Rivals);
        Assert.Equal(3, roster.NextId);
        Assert.NotNull(RosterRepository.Deserialize("""{"Version":1,"DataSets":null}""").DataSets);
    }

    [Fact]
    public async Task Team_unknown_fields_are_sent_to_the_server()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var key = RemoteRosterStore.GenerateKey();
        var server = new FakeRosterServer { ExpectedKey = key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", key);
        var state = new RosterState();
        var roster = state.For(Ds);
        roster.Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonId = 445, PokemonName = "ガブリアス", Nature = "ようき" });
        roster.NextId = 2;
        var team = new Team { Number = 1, Slots = { new TeamSlot { EntryId = 1 } } };
        team.Extra = new Dictionary<string, JsonElement> { ["Label"] = JsonDocument.Parse("\"エース\"").RootElement.Clone() };
        roster.Teams.Add(team);
        await store.SavePartAsync(state, Ds, RosterPart.Pokemon);
        await store.SavePartAsync(state, Ds, RosterPart.Teams);
        var put = server.Bodies.Last(b => b is not null && b.Contains("teams"));
        Assert.Contains("エース", put);   // チームの新しい項目が、保存のたびに落ちない
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHandler(Exception exception) => _exception = exception;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw _exception;
    }

    [Fact]
    public async Task Network_failures_and_timeouts_are_reported_as_network_errors_without_the_key()
    {
        var key = RemoteRosterStore.GenerateKey();
        foreach (Exception failure in new Exception[] { new HttpRequestException("boom"), new TaskCanceledException("timeout") })
        {
            var store = new RemoteRosterStore(new HttpClient(new ThrowingHandler(failure)), "https://pokemon.example/", key);
            var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync());
            Assert.Equal(RosterUnavailableReason.Network, ex.Reason);
            Assert.DoesNotContain(key, ex.Message);
        }
    }

    [Fact]
    public async Task Saving_invalid_json_is_an_argument_error_and_delete_expects_204()
    {
        var key = RemoteRosterStore.GenerateKey();
        var server = new FakeRosterServer { ExpectedKey = key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", key);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("not json"));
        server.Override = r => r.Method == HttpMethod.Delete ? FakeRosterServer.Json(HttpStatusCode.OK, "{}") : null;
        await Assert.ThrowsAsync<RosterUnavailableException>(() => store.DeleteAsync());
    }

    [Fact]
    public async Task Rival_unreadable_flag_clears_after_a_successful_load()
    {
        var key = RemoteRosterStore.GenerateKey();
        var server = new FakeRosterServer { ExpectedKey = key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", key);
        server.Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/my/rival" ? FakeRosterServer.Json(HttpStatusCode.TooManyRequests, "{}") : null;
        await store.LoadAsync();
        Assert.True(store.RivalUnreadable);
        server.Override = null;
        await store.LoadAsync();
        Assert.False(store.RivalUnreadable);   // 読めたら、相手チームの保存が再び許される
    }

    [Fact]
    public async Task Whole_save_is_refused_while_the_rival_team_is_unreadable()
    {
        var key = RemoteRosterStore.GenerateKey();
        var server = new FakeRosterServer { ExpectedKey = key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", key);
        server.Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/my/rival" ? FakeRosterServer.Json(HttpStatusCode.TooManyRequests, "{}") : null;
        await store.LoadAsync();
        Assert.True(store.RivalUnreadable);
        server.Override = null;
        server.Requests.Clear();
        var json = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = new DataSetRoster() } });
        await Assert.ThrowsAsync<RosterUnavailableException>(() => store.SaveAsync(json));
        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Put);   // 何も送らない（クラウドの相手チームを空で上書きしない）
    }

    [Theory]
    [InlineData("{ \"DataSets\": { \"Gen9\": { \"Pokemon\": \"not a list\" } } }")]
    [InlineData("{ not json")]
    public async Task Broken_stored_data_is_reported_as_unavailable_not_as_a_raw_json_error(string broken)
    {
        var store = new MemoryRosterStore { Json = broken };
        var repo = new RosterRepository(store);
        var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.GetAsync());
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        // 変更する操作も、読めないまま空として進めず、失敗する（読めなかったデータを書き換えない）
        await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.RemovePokemonAsync(Ds, 1));
        await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.ClearRivalsAsync(Ds));
        Assert.Equal(broken, store.Json);
        // 失敗は覚えない（データが直れば、次の読み込みで読める）
        store.Json = RosterRepository.Serialize(new RosterState());
        Assert.NotNull(await repo.GetAsync());
    }

    [Fact]
    public async Task Null_items_inside_the_lists_are_dropped_on_load()
    {
        var store = new MemoryRosterStore { Json = "{ \"DataSets\": { \"Gen9\": { \"Pokemon\": [ null ], \"Rivals\": [ null ], \"Teams\": [ null ], \"History\": [ null ] } } }" };
        var state = await new RosterRepository(store).GetAsync();
        var set = state.For(Ds);
        Assert.Empty(set.Pokemon);
        Assert.Empty(set.Rivals);
        Assert.Empty(set.Teams);
        Assert.Empty(set.History);
    }

    /// <summary>大文字小文字だけ違うゲームのキーを統合するとき、同じ番号のチームを捨てず、枠は同じポケモンを指したままにする。</summary>
    [Fact]
    public void Merging_case_variant_keys_keeps_both_teams_and_points_slots_at_the_right_pokemon()
    {
        const string json = """
            {"Version":2,"DataSets":{
              "Gen9":{"NextId":2,"Pokemon":[{"Id":1,"PokemonId":6,"PokemonName":"リザードン"}],
                      "Teams":[{"Number":1,"Slots":[{"EntryId":1}]}]},
              "gen9":{"NextId":2,"Pokemon":[{"Id":1,"PokemonId":3,"PokemonName":"フシギバナ"}],
                      "Teams":[{"Number":1,"Slots":[{"EntryId":1}]}]}
            }}
            """;
        var state = RosterRepository.Deserialize(json);
        var set = Assert.Single(state.DataSets).Value;

        Assert.Equal(2, set.Teams.Count);                                      // 同じ番号のチームも捨てない
        Assert.Equal(2, set.Teams.Select(t => t.Number).Distinct().Count());   // チーム番号は重ならない
        Assert.Equal(2, set.Pokemon.Select(p => p.Id).Distinct().Count());     // ポケモンの ID も重ならない
        // 各チームの枠は、そのチームのポケモンを指したまま（ID を振り直しても取り違えない）
        var nameByTeam = set.Teams.ToDictionary(
            t => set.Pokemon.Single(p => p.Id == Assert.Single(t.Slots).EntryId).PokemonName,
            t => t.Number);
        Assert.Equal(new Dictionary<string, int> { ["リザードン"] = 1, ["フシギバナ"] = 2 }, nameByTeam);
    }
}
