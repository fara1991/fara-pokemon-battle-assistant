using System.Text.Json;
using System.Text.Json.Nodes;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>チーム番号は、シングルとダブルで別々に数える。使用中のチームも形式ごとに 1 つ。</summary>
public class TeamNumberPerFormatTests
{
    private const string Ds = "Champions";
    private static readonly string Key = RemoteRosterStore.GenerateKey();

    private static async Task<(RosterRepository Repo, RegisteredPokemon A, RegisteredPokemon B)> NewAsync(IRosterStore? store = null)
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(store ?? new MemoryRosterStore());
        var a = await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "マニューラ"), new StatSet { Attack = 32 }, data.Natures.First(), null);
        var b = await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "ガブリアス"), new StatSet { Speed = 32 }, data.Natures.First(), null);
        return (repo, a, b);
    }

    // ---- 同じ番号を両方の形式に作れる

    [Fact]
    public async Task The_same_number_can_exist_in_singles_and_doubles()
    {
        var (repo, a, b) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, b.Id, null);
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Equal(2, roster.Teams.Count);
        Assert.Equal(a.Id, roster.FindTeam(BattleFormat.Singles, 1)!.Slots.Single().EntryId);
        Assert.Equal(b.Id, roster.FindTeam(BattleFormat.Doubles, 1)!.Slots.Single().EntryId);
        // 順序: 同じ番号ならシングルが先
        Assert.Equal(new[] { BattleFormat.Singles, BattleFormat.Doubles }, roster.Teams.Select(t => t.BattleFormat));
    }

    [Fact]
    public async Task Active_team_is_kept_per_format()
    {
        var (repo, a, b) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, b.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 2, a.Id, null);
        Assert.True(await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1));
        Assert.True(await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 2));
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Equal(2, roster.ActiveNumber(BattleFormat.Doubles));

        // 片方の形式の使用中を変えても、もう片方は変わらない
        Assert.True(await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Doubles));
        Assert.True(await repo.ClearActiveTeamAsync(Ds, BattleFormat.Singles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Singles));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Doubles));
        Assert.False(await repo.ClearActiveTeamAsync(Ds, BattleFormat.Singles));   // もともと無ければ false
    }

    [Fact]
    public async Task Use_fails_for_a_number_that_exists_only_in_the_other_format()
    {
        var (repo, a, _) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 3, a.Id, null);
        Assert.False(await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 3));
        Assert.Null((await repo.GetAsync()).For(Ds).ActiveNumber(BattleFormat.Doubles));
    }

    [Fact]
    public async Task Removing_a_team_touches_only_that_format()
    {
        var (repo, a, b) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, b.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1);
        Assert.True(await repo.RemoveTeamAsync(Ds, BattleFormat.Doubles, 1));
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.NotNull(roster.FindTeam(BattleFormat.Singles, 1));
        Assert.Null(roster.FindTeam(BattleFormat.Doubles, 1));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));
        Assert.False(await repo.RemoveTeamAsync(Ds, BattleFormat.Doubles, 1));
    }

    [Fact]
    public async Task Removing_a_pokemon_deletes_emptied_teams_and_releases_only_their_format()
    {
        var (repo, a, b) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, b.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, a.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1);
        Assert.True(await repo.RemovePokemonAsync(Ds, a.Id));   // ダブルのチーム1 は 0 体になって消える。シングルのチーム1 は残る
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Null(roster.FindTeam(BattleFormat.Doubles, 1));
        Assert.Equal(new[] { b.Id }, roster.FindTeam(BattleFormat.Singles, 1)!.Slots.Select(s => s.EntryId));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));
    }

    [Fact]
    public async Task RemoveEmptyTeams_reports_format_and_number()
    {
        var roster = new DataSetRoster { ActiveTeams = new Dictionary<string, int?>() };
        roster.Teams.Add(new Team { Number = 2, Format = "Doubles" });
        roster.Teams.Add(new Team { Number = 2, Format = "Singles", Slots = { new TeamSlot { EntryId = 1 } } });
        roster.SetActiveNumber(BattleFormat.Doubles, 2);
        roster.SetActiveNumber(BattleFormat.Singles, 2);
        var removed = RosterRepository.RemoveEmptyTeams(roster);
        Assert.Equal((BattleFormat.Doubles, 2), Assert.Single(removed));
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));
        Assert.Equal(2, roster.ActiveNumber(BattleFormat.Singles));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Team_is_limited_to_six_per_format_team()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new MemoryRosterStore());
        var ids = new List<int>();
        for (var i = 0; i < 7; i++)
            ids.Add((await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "ガブリアス"), new StatSet(), data.Natures.First(), null)).Id);
        foreach (var id in ids.Take(6)) await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, id, null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, ids[6], null));
        Assert.Contains("ダブルのチーム1", ex.Message);
        // 同じ番号でも、シングルのチームは別なので入れられる
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, ids[6], null);
    }

    // ---- 「登録から選ぶ」

    [Fact]
    public async Task Choice_groups_and_active_members_use_only_the_given_format()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var (repo, a, b) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, b.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1);

        var singles = await repo.ChoiceGroupsAsync(Ds, data, BattleFormat.Singles);
        Assert.Equal(new[] { "チーム1（使用中）", "チームに入っていない登録" }, singles.Select(g => g.Label));
        Assert.Equal($"s1-{a.Id}", singles[0].Choices.Single().Key);
        var doubles = await repo.ChoiceGroupsAsync(Ds, data, BattleFormat.Doubles);
        Assert.Equal(new[] { "チーム1（使用中）", "チームに入っていない登録" }, doubles.Select(g => g.Label));
        Assert.Equal($"d1-{b.Id}", doubles[0].Choices.Single().Key);   // 同じ番号でも、キーは形式ごとに別
        // 他の形式のチームに入っているだけのポケモンは、「チームに入っていない登録」に出る
        Assert.Equal(b.Id, singles[1].Choices.Single().Entry.Id);
        Assert.Equal(a.Id, doubles[1].Choices.Single().Entry.Id);

        Assert.Equal(a.Id, Assert.Single(await repo.ActiveTeamMembersAsync(Ds, data, BattleFormat.Singles)).Entry.Id);
        Assert.Equal(b.Id, Assert.Single(await repo.ActiveTeamMembersAsync(Ds, data, BattleFormat.Doubles)).Entry.Id);
        // 使用中の無い形式は、他の形式の使用中にフォールバックしない
        await repo.ClearActiveTeamAsync(Ds, BattleFormat.Doubles);
        Assert.Empty(await repo.ActiveTeamMembersAsync(Ds, data, BattleFormat.Doubles));
    }

    // ---- 別の形式への移動

    [Fact]
    public async Task Move_changes_the_format_and_keeps_the_number_when_free()
    {
        var (repo, a, _) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 4, a.Id, null);
        var result = await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 4, BattleFormat.Doubles);
        Assert.Equal(new TeamMoveResult(TeamMoveStatus.Moved, 4, false), result);
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Null(roster.FindTeam(BattleFormat.Singles, 4));
        Assert.Equal("Doubles", roster.FindTeam(BattleFormat.Doubles, 4)!.Format);
    }

    [Fact]
    public async Task Move_refuses_when_the_target_number_is_taken_and_never_renumbers()
    {
        var (repo, a, b) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 2, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 2, b.Id, null);
        var result = await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 2, BattleFormat.Doubles);
        Assert.Equal(TeamMoveStatus.Occupied, result.Status);
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Equal(new[] { 2, 2 }, roster.Teams.Select(t => t.Number));   // どちらも動かない
        Assert.Equal(a.Id, roster.FindTeam(BattleFormat.Singles, 2)!.Slots.Single().EntryId);
        Assert.Equal(b.Id, roster.FindTeam(BattleFormat.Doubles, 2)!.Slots.Single().EntryId);
    }

    [Fact]
    public async Task Move_with_a_new_number_and_active_handling()
    {
        var (repo, a, b) = await NewAsync();
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 2, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 2, b.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 2);
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 2);
        var result = await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 2, BattleFormat.Doubles, 5);
        Assert.Equal(new TeamMoveResult(TeamMoveStatus.Moved, 5, true), result);
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Null(roster.ActiveNumber(BattleFormat.Singles));      // 移した元の使用中は解除
        Assert.Equal(2, roster.ActiveNumber(BattleFormat.Doubles));  // 移した先の使用中は変えない
        Assert.NotNull(roster.FindTeam(BattleFormat.Doubles, 5));
    }

    [Fact]
    public async Task Move_missing_and_unchanged()
    {
        var (repo, a, _) = await NewAsync();
        Assert.Equal(TeamMoveStatus.NotFound, (await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 9, BattleFormat.Doubles)).Status);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        Assert.Equal(TeamMoveStatus.Unchanged, (await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 1, BattleFormat.Singles)).Status);
        // 同じ形式の中で番号だけ変える
        Assert.Equal(TeamMoveStatus.Moved, (await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 1, BattleFormat.Singles, 7)).Status);
        Assert.NotNull((await repo.GetAsync()).For(Ds).FindTeam(BattleFormat.Singles, 7));
    }

    // ---- 既存データの移行（すべてシングル）

    private const string LegacyJson = """
    {"Version":1,"DataSets":{"Champions":{"NextId":4,
      "Pokemon":[{"Id":1,"PokemonId":1,"PokemonName":"a"},{"Id":2,"PokemonId":1,"PokemonName":"b"},{"Id":3,"PokemonId":1,"PokemonName":"c"}],
      "Teams":[{"Number":1,"Slots":[{"EntryId":1}]},{"Number":2,"Slots":[{"EntryId":2}],"Format":"Doubles"},{"Number":3,"Slots":[{"EntryId":3}],"Format":"Singles"}],
      "ActiveTeam":2}}}
    """;

    [Fact]
    public void Legacy_teams_all_become_singles_even_if_they_were_saved_as_doubles()
    {
        var roster = RosterRepository.Deserialize(LegacyJson).For(Ds);
        Assert.Equal(new[] { BattleFormat.Singles, BattleFormat.Singles, BattleFormat.Singles }, roster.Teams.Select(t => t.BattleFormat));
        Assert.All(roster.Teams, t => Assert.Equal("Singles", t.Format));
        Assert.Equal(new[] { 1, 2, 3 }, roster.Teams.Select(t => t.Number));
        // 使用中だったチーム（ダブルで保存されていたチーム2）は、シングルの使用中になる
        Assert.Equal(2, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));
        Assert.Null(roster.ActiveTeam);   // 廃止した項目（読み込み時に必ず null）
    }

    [Fact]
    public void Migration_is_idempotent_and_does_not_flatten_doubles_created_afterwards()
    {
        var first = RosterRepository.Deserialize(LegacyJson);
        first.For(Ds).Teams.Add(new Team { Number = 1, Format = "Doubles", Slots = { new TeamSlot { EntryId = 1 } } });
        first.For(Ds).SetActiveNumber(BattleFormat.Doubles, 1);
        var saved = RosterRepository.Serialize(first);
        var second = RosterRepository.Deserialize(saved).For(Ds);
        Assert.Equal(4, second.Teams.Count);
        Assert.Equal(BattleFormat.Doubles, second.FindTeam(BattleFormat.Doubles, 1)!.BattleFormat);
        Assert.Equal(2, second.ActiveNumber(BattleFormat.Singles));
        Assert.Equal(1, second.ActiveNumber(BattleFormat.Doubles));
        // もう一度読み書きしても同じ
        var third = RosterRepository.Deserialize(RosterRepository.Serialize(RosterRepository.Deserialize(saved))).For(Ds);
        Assert.Equal(second.Teams.Select(t => (t.BattleFormat, t.Number)), third.Teams.Select(t => (t.BattleFormat, t.Number)));
    }

    [Theory]
    [InlineData("""{"Version":2,"DataSets":{"Champions":{"Teams":[{"Number":1,"Format":"Singles","Slots":[{"EntryId":1}]},{"Number":1,"Format":"Doubles","Slots":[{"EntryId":2}]}],"ActiveTeams":null}}}""")]
    [InlineData("""{"Version":2,"DataSets":{"Champions":{"Teams":[{"Number":1,"Format":"Singles","Slots":[{"EntryId":1}]},{"Number":1,"Format":"Doubles","Slots":[{"EntryId":2}]}]}}}""")]
    public void Current_data_without_active_teams_is_never_flattened_to_singles(string json)
    {
        // 世代 2 以上（または世代の無い）データは、ActiveTeams が無くても移行しない（コードで作ったデータが、次に読むときにシングルになって消えないように）
        var roster = RosterRepository.Deserialize(json).For(Ds);
        Assert.Equal(2, roster.Teams.Count);
        Assert.Equal(2, roster.FindTeam(BattleFormat.Doubles, 1)!.Slots.Single().EntryId);
        Assert.Equal(1, roster.FindTeam(BattleFormat.Singles, 1)!.Slots.Single().EntryId);
    }

    [Fact]
    public void Data_without_a_version_and_active_teams_is_read_as_legacy()
    {
        // Version の無い古いデータは、Worker と同じく ActiveTeams の有無も見て、世代 1 として読み替える
        var json = """{"DataSets":{"Champions":{"Teams":[{"Number":1,"Slots":[{"EntryId":1}]},{"Number":2,"Format":"Doubles","Slots":[{"EntryId":2}]}],"ActiveTeam":2}}}""";
        var roster = RosterRepository.Deserialize(json).For(Ds);
        Assert.All(roster.Teams, t => Assert.Equal(BattleFormat.Singles, t.BattleFormat));
        Assert.Equal(2, roster.ActiveNumber(BattleFormat.Singles));
        // ActiveTeams があれば、Version が無くても新しい形
        var current = """{"DataSets":{"Champions":{"Teams":[{"Number":2,"Format":"Doubles","Slots":[{"EntryId":2}]}],"ActiveTeams":{"Doubles":2}}}}""";
        var kept = RosterRepository.Deserialize(current).For(Ds);
        Assert.Equal(BattleFormat.Doubles, kept.Teams.Single().BattleFormat);
        Assert.Equal(2, kept.ActiveNumber(BattleFormat.Doubles));
    }

    private static JsonNode StoredActive(MemoryRosterStore store) => JsonNode.Parse(store.Json!)!["DataSets"]![Ds]!["ActiveTeams"]!;

    [Fact]
    public async Task Stored_active_numbers_never_outlive_their_team()
    {
        var store = new MemoryRosterStore();
        var (repo, a, b) = await NewAsync(store);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, b.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1);

        // 形式の移動: 移す前の形式の使用中は保存データからも消え、同じ番号を作り直しても使用中に戻らない
        await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 1, BattleFormat.Doubles, 7);
        Assert.Null(StoredActive(store)["Singles"]);
        Assert.Equal(1, StoredActive(store)["Doubles"]!.GetValue<int>());   // 移した先の使用中は変えない
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        Assert.Null((await repo.GetAsync()).For(Ds).ActiveNumber(BattleFormat.Singles));

        // 同じ形式の中の番号の付け替え
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 7);
        await repo.MoveTeamAsync(Ds, BattleFormat.Doubles, 7, BattleFormat.Doubles, 9);
        Assert.Null(StoredActive(store)["Doubles"]);

        // チームごと削除
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.RemoveTeamAsync(Ds, BattleFormat.Singles, 1);
        Assert.Null(StoredActive(store)["Singles"]);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, b.Id, null);
        Assert.Null((await repo.GetAsync()).For(Ds).ActiveNumber(BattleFormat.Singles));

        // 0 体になって消えたチーム（rm -t N 名前 / ポケモンの削除）
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.RemoveFromTeamAsync(Ds, BattleFormat.Singles, 1, e => e.Id == b.Id);
        Assert.Null(StoredActive(store)["Singles"]);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        Assert.Null((await repo.GetAsync()).For(Ds).ActiveNumber(BattleFormat.Singles));
    }

    [Fact]
    public void RemoveEmptyTeams_clears_the_stored_number()
    {
        var roster = new DataSetRoster { ActiveTeams = new Dictionary<string, int?>() };
        roster.Teams.Add(new Team { Number = 2, Format = "Doubles" });
        roster.SetActiveNumber(BattleFormat.Doubles, 2);
        RosterRepository.RemoveEmptyTeams(roster);
        var stored = JsonNode.Parse(RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = roster } }))!["DataSets"]![Ds]!["ActiveTeams"]!;
        Assert.Null(stored["Doubles"]);
        roster.Teams.Add(new Team { Number = 2, Format = "Doubles", Slots = { new TeamSlot { EntryId = 1 } } });
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));   // 作り直しても使用中に戻らない
    }

    [Fact]
    public void Team_number_limit_matches_the_worker()
    {
        Assert.Equal(2147483646, RosterLimits.MaxTeamNumber);
        Assert.True(RosterLimits.TryParseTeamNumber("2147483646", out var max));
        Assert.Equal(2147483646, max);
        Assert.False(RosterLimits.TryParseTeamNumber("2147483647", out _));
        Assert.False(RosterLimits.TryParseTeamNumber("0", out _));
        Assert.False(RosterLimits.TryParseTeamNumber("x", out _));
        // Worker の MAX_ID と同じ値であること（片方だけ変えると落ちる）
        var js = File.ReadAllText(Path.Combine(TestData.RepositoryRoot, "worker", "index.js"));
        Assert.Contains($"const MAX_ID = {RosterLimits.MaxTeamNumber};", js);
    }

    [Fact]
    public void A_data_set_built_in_code_keeps_its_doubles_teams_through_a_save_and_load()
    {
        var state = new RosterState();
        state.DataSets[Ds] = new DataSetRoster();   // ActiveTeams を持たない（For を通さない）作り方
        state.DataSets[Ds].Teams.Add(new Team { Number = 1, Format = "Doubles", Slots = { new TeamSlot { EntryId = 1 } } });
        state.DataSets[Ds].Teams.Add(new Team { Number = 1, Format = "Singles", Slots = { new TeamSlot { EntryId = 2 } } });
        var json = RosterRepository.Serialize(state);
        Assert.DoesNotContain("\"ActiveTeams\": null", json);
        var roster = RosterRepository.Deserialize(json).For(Ds);
        Assert.Equal(1, roster.FindTeam(BattleFormat.Doubles, 1)!.Slots.Single().EntryId);
        Assert.Equal(2, roster.FindTeam(BattleFormat.Singles, 1)!.Slots.Single().EntryId);
    }

    [Fact]
    public void Legacy_active_team_pointing_nowhere_is_dropped()
    {
        var json = """{"Version":1,"DataSets":{"Champions":{"Teams":[{"Number":1,"Slots":[{"EntryId":1}]}],"ActiveTeam":9}}}""";
        var roster = RosterRepository.Deserialize(json).For(Ds);
        Assert.Null(roster.ActiveNumber(BattleFormat.Singles));
    }

    [Fact]
    public void New_data_with_a_dangling_active_team_is_repaired_on_read()
    {
        var json = """{"Version":2,"DataSets":{"Champions":{"Teams":[{"Number":1,"Format":"Singles","Slots":[{"EntryId":1}]}],"ActiveTeams":{"Singles":1,"Doubles":1}}}}""";
        var roster = RosterRepository.Deserialize(json).For(Ds);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));
    }

    [Fact]
    public void Unknown_fields_are_ignored_not_fatal()
    {
        var json = """{"Version":9,"Future":1,"DataSets":{"Champions":{"Future":[1],"Teams":[{"Number":1,"Format":"Doubles","Future":true,"Slots":[{"EntryId":1,"Future":1}]}],"ActiveTeams":{"Doubles":1,"Triples":3}}}}""";
        var roster = RosterRepository.Deserialize(json).For(Ds);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Doubles));
        Assert.Contains("Future", RosterRepository.Serialize(RosterRepository.Deserialize(json)));
    }

    [Fact]
    public async Task Legacy_local_data_is_fixed_on_the_next_save()
    {
        var store = new MemoryRosterStore { Json = LegacyJson };
        var repo = new RosterRepository(store);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, 1, null);   // 次の保存で新しい形になる
        var saved = JsonNode.Parse(store.Json!)!["DataSets"]![Ds]!;
        Assert.NotNull(saved["ActiveTeams"]);
        Assert.Null(saved["ActiveTeam"]);
        Assert.Equal(2, saved["ActiveTeams"]!["Singles"]!.GetValue<int>());
        Assert.Equal(new[] { "Singles", "Singles", "Singles", "Doubles" }, saved["Teams"]!.AsArray().Select(t => t!["Format"]!.GetValue<string>()).OrderBy(x => x == "Doubles"));
        Assert.Equal(2, JsonNode.Parse(store.Json!)!["Version"]!.GetValue<int>());
    }

    [Fact]
    public void Merging_case_variant_keys_keeps_teams_of_both_formats_with_the_same_number()
    {
        var json = """
        {"DataSets":{
          "Champions":{"Teams":[{"Number":1,"Format":"Singles","Slots":[{"EntryId":1}]}],"ActiveTeams":{"Singles":1}},
          "champions":{"Teams":[{"Number":1,"Format":"Doubles","Slots":[{"EntryId":2}]},{"Number":1,"Format":"Singles","Slots":[{"EntryId":9}]}],"ActiveTeams":{"Doubles":1}}}}
        """;
        var roster = RosterRepository.Deserialize(json).For(Ds);
        Assert.Equal(2, roster.Teams.Count);
        Assert.Equal(1, roster.FindTeam(BattleFormat.Singles, 1)!.Slots.Single().EntryId);
        Assert.Equal(2, roster.FindTeam(BattleFormat.Doubles, 1)!.Slots.Single().EntryId);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Doubles));
    }

    // ---- クラウド

    [Fact]
    public async Task Same_number_in_both_formats_round_trips_through_the_cloud()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        var (repo, a, b) = await NewAsync(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, b.Id, null);   // 同じ番号の 2 つ目が黙って落とされない
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);

        var saved = server.State.For(Ds);
        Assert.Equal(2, saved.Teams.Count);
        Assert.Equal(1, saved.ActiveNumber(BattleFormat.Singles));
        Assert.Equal(1, saved.ActiveNumber(BattleFormat.Doubles));
        var put = JsonNode.Parse(server.Bodies.Last()!)!["dataSets"]![Ds]!;
        Assert.Equal(1, put["activeTeams"]!["Singles"]!.GetValue<int>());
        Assert.Equal(1, put["activeTeams"]!["Doubles"]!.GetValue<int>());
        Assert.Null(put["activeTeam"]);

        // 別の端末で読み直しても同じ
        var other = new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        var loaded = (await other.GetAsync()).For(Ds);
        Assert.Equal(2, loaded.Teams.Count);
        Assert.Equal(b.Id, loaded.FindTeam(BattleFormat.Doubles, 1)!.Slots.Single().EntryId);
        Assert.Equal(1, loaded.ActiveNumber(BattleFormat.Doubles));
    }

    [Fact]
    public async Task Move_through_the_cloud_is_saved()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        var (repo, a, _) = await NewAsync(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, a.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        Assert.Equal(TeamMoveStatus.Moved, (await repo.MoveTeamAsync(Ds, BattleFormat.Singles, 1, BattleFormat.Doubles)).Status);
        var saved = server.State.For(Ds);
        Assert.Equal("Doubles", saved.Teams.Single().Format);
        Assert.Null(saved.ActiveNumber(BattleFormat.Singles));
    }

    [Fact]
    public void Legacy_server_data_is_read_as_singles_when_the_response_has_no_active_teams()
    {
        // 古いサーバーの応答（activeTeams なし・activeTeam あり）を組み立てても、移行で全部シングルの使用中になる
        var server = new FakeRosterServer { ExpectedKey = Key };
        server.RosterJson = LegacyJson;
        server.Revision = 1;
        var roster = server.State.For(Ds);
        Assert.Equal(2, roster.ActiveNumber(BattleFormat.Singles));
        Assert.All(roster.Teams, t => Assert.Equal(BattleFormat.Singles, t.BattleFormat));
    }

    [Fact]
    public void Team_key_combines_format_and_number()
    {
        Assert.Equal((BattleFormat.Doubles, 3), new Team { Number = 3, Format = "Doubles" }.Key);
        Assert.Equal((BattleFormat.Singles, 3), new Team { Number = 3 }.Key);
        Assert.Equal("ダブルのチーム3", TeamNames.Full(BattleFormat.Doubles, 3));
        Assert.Equal("シングルのチーム1", TeamNames.Full(BattleFormat.Singles, 1));
    }

    [Fact]
    public void Version_is_written_as_two()
    {
        Assert.Equal(2, JsonNode.Parse(RosterRepository.Serialize(new RosterState()))!["Version"]!.GetValue<int>());
        _ = JsonSerializer.Serialize(new Team());
    }
}
