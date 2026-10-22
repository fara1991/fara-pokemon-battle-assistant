using System.Text.Json;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>チームのバトル形式（シングル・ダブル）。コマンド・保存・クラウドで同じ表記にそろえる。</summary>
public class TeamFormatTests
{
    private const string Ds = "Champions";
    private static readonly string Key = RemoteRosterStore.GenerateKey();

    private static async Task<(PokeCommand Command, RosterRepository Repo)> NewAsync()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "マニューラ"), new StatSet { Attack = 32 }, data.Natures.First(), null);
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "ガブリアス"), new StatSet { Speed = 32 }, data.Natures.First(), null);
        return (new PokeCommand(TestData.Catalog(), repo), repo);
    }

    private static async Task<Team> TeamAsync(RosterRepository repo, int number) =>
        (await repo.GetAsync()).For(Ds).Teams.Single(t => t.Number == number);

    // ---- 表記の読み書き

    [Theory]
    [InlineData("シングル", BattleFormat.Singles)]
    [InlineData("しんぐる", BattleFormat.Singles)]
    [InlineData("single", BattleFormat.Singles)]
    [InlineData("SINGLES", BattleFormat.Singles)]
    [InlineData("ダブル", BattleFormat.Doubles)]
    [InlineData("だぶる", BattleFormat.Doubles)]
    [InlineData("Double", BattleFormat.Doubles)]
    [InlineData("doubles", BattleFormat.Doubles)]
    [InlineData(" ダブル ", BattleFormat.Doubles)]
    public void Words_are_read_as_a_format(string word, BattleFormat expected)
    {
        Assert.True(TeamFormats.TryParseWord(word, out var format));
        Assert.Equal(expected, format);
    }

    [Fact]
    public void Dbl_is_accepted_like_the_damage_command()
    {
        Assert.True(TeamFormats.TryParseWord("dbl", out var format));
        Assert.Equal(BattleFormat.Doubles, format);
    }

    [Theory]
    [InlineData("1 1 ダブル", 2, "1 1", true, BattleFormat.Doubles)]
    [InlineData("1 1 シングル", 2, "1 1", true, BattleFormat.Singles)]
    [InlineData("1 1 こだわりスカーフ ダブル", 2, "1 1 こだわりスカーフ", true, BattleFormat.Doubles)]
    [InlineData("1 1 こだわりスカーフ", 2, "1 1 こだわりスカーフ", false, BattleFormat.Singles)]
    [InlineData("1 ダブル", 2, "1 ダブル", false, BattleFormat.Singles)]   // 残りが足りないときは切り離さない（書式エラーにする）
    [InlineData("", 2, "", false, BattleFormat.Singles)]
    public void Trailing_format_word_is_split_off_only_when_enough_remains(string text, int min, string rest, bool found, BattleFormat expected)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = TeamFormats.SplitTrailingFormat(words, min, out var format);
        Assert.Equal(rest, string.Join(" ", result));
        Assert.Equal(found, format is not null);
        if (found) Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Singles", "Singles")]
    [InlineData("Doubles", "Doubles")]
    [InlineData("doubles", "Doubles")]     // 大文字小文字の違いは直して送る
    [InlineData("Triples", "Singles")]     // 知らない値は、ローカルの読み方と同じシングル（1 つの値でチーム全体の保存が断られないように）
    [InlineData("", "Singles")]
    public void Format_is_normalized_before_it_is_sent_to_the_cloud(string? stored, string? expected) =>
        Assert.Equal(expected, RemoteRosterStore.NormalizedFormat(stored));

    [Fact]
    public async Task Team_with_an_unknown_format_is_still_saved_to_the_cloud()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        server.WriteFromElsewhere(state =>
        {
            var r = state.For(Ds);
            r.Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonName = "ガブリアス" });
            r.NextId = 2;
            r.Teams.Add(new Team { Number = 1, Slots = { new TeamSlot { EntryId = 1 } }, Format = "doubles" });
            r.Teams.Add(new Team { Number = 2, Slots = { new TeamSlot { EntryId = 1 } }, Format = "Triples" });
        });
        var repo = new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        await repo.AddToTeamAsync(Ds, 3, 1, null);   // 別のチームの操作で、チーム全体を保存し直す
        var saved = server.State.For(Ds).Teams.OrderBy(t => t.Number).ToList();
        Assert.Equal(new[] { "Doubles", "Singles", null }, saved.Select(t => t.Format).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("トリプル")]
    [InlineData("こだわりスカーフ")]
    [InlineData("d")]
    public void Other_words_are_not_a_format(string? word) => Assert.False(TeamFormats.TryParseWord(word, out _));

    [Fact]
    public void Stored_names_round_trip_and_unknown_values_are_singles()
    {
        Assert.Equal("Doubles", TeamFormats.Name(BattleFormat.Doubles));
        Assert.Equal("Singles", TeamFormats.Name(BattleFormat.Singles));
        Assert.Equal(BattleFormat.Doubles, TeamFormats.Parse("Doubles"));
        Assert.Equal(BattleFormat.Doubles, TeamFormats.Parse("doubles"));
        Assert.Equal(BattleFormat.Singles, TeamFormats.Parse("Singles"));
        Assert.Equal(BattleFormat.Singles, TeamFormats.Parse(null));
        Assert.Equal(BattleFormat.Singles, TeamFormats.Parse(""));
        Assert.Equal(BattleFormat.Singles, TeamFormats.Parse("Triples"));
        Assert.Equal("ダブル", TeamFormats.Label(BattleFormat.Doubles));
        Assert.Equal("シングル", TeamFormats.Label(BattleFormat.Singles));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Singles", true)]
    [InlineData("Doubles", true)]
    [InlineData("doubles", false)]
    [InlineData("Triples", false)]
    [InlineData("", false)]
    public void Only_the_stored_names_are_valid(string? stored, bool valid) => Assert.Equal(valid, TeamFormats.IsValidStored(stored));

    // ---- 保存の形（古いデータ・新しい版の項目との互換）

    [Fact]
    public void Teams_saved_by_an_older_version_have_no_format_and_read_as_singles()
    {
        var team = JsonSerializer.Deserialize<Team>("{\"Number\":3,\"Slots\":[{\"EntryId\":1}]}")!;
        Assert.Null(team.Format);
        Assert.Equal(BattleFormat.Singles, team.BattleFormat);
    }

    [Fact]
    public void Format_is_saved_as_text_and_unknown_fields_are_kept()
    {
        var team = JsonSerializer.Deserialize<Team>("{\"Number\":1,\"Slots\":[],\"Format\":\"Doubles\",\"Future\":{\"a\":1}}")!;
        Assert.Equal(BattleFormat.Doubles, team.BattleFormat);
        var json = JsonSerializer.Serialize(team);
        Assert.Contains("\"Format\":\"Doubles\"", json);
        Assert.Contains("\"Future\"", json);
        Assert.DoesNotContain("BattleFormat", json);   // 読み取り専用の読み替えは保存しない
    }

    // ---- コマンド

    [Fact]
    public async Task Add_with_a_trailing_format_word_makes_a_doubles_team()
    {
        var (command, repo) = await NewAsync();
        var added = await command.ExecuteAsync("!pokech add -t 1 1 ダブル");
        Assert.True(added.Success, added.Message);
        Assert.Contains("ダブル", added.Message);
        Assert.Equal(BattleFormat.Doubles, (await TeamAsync(repo, 1)).BattleFormat);
        Assert.Null((await TeamAsync(repo, 1)).Slots.Single().ItemId);   // 「ダブル」を持ち物として読まない
    }

    [Fact]
    public async Task Add_with_an_item_and_a_format_reads_both()
    {
        var (command, repo) = await NewAsync();
        var added = await command.ExecuteAsync("!pokech add -t 2 2 こだわりスカーフ ダブル");
        Assert.True(added.Success, added.Message);
        var team = await TeamAsync(repo, 2);
        Assert.Equal(BattleFormat.Doubles, team.BattleFormat);
        Assert.NotNull(team.Slots.Single().ItemId);
    }

    [Fact]
    public async Task Add_without_a_format_keeps_singles_and_does_not_reset_an_existing_format()
    {
        var (command, repo) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -t 1 1")).Success);
        Assert.Equal(BattleFormat.Singles, (await TeamAsync(repo, 1)).BattleFormat);
        Assert.DoesNotContain("ダブル", (await command.ExecuteAsync("!pokech add -t 1 2")).Message);

        Assert.True((await command.ExecuteAsync("!pokech set -t 1 ダブル")).Success);
        // 形式を書かずに追加しても、ダブルのまま
        var again = await command.ExecuteAsync("!pokech add -t 1 2");
        Assert.True(again.Success, again.Message);
        Assert.Equal(BattleFormat.Doubles, (await TeamAsync(repo, 1)).BattleFormat);
    }

    [Fact]
    public async Task Set_changes_the_format_back_and_forth()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1");
        var toDoubles = await command.ExecuteAsync("!pokech set -t 1 ダブル");
        Assert.True(toDoubles.Success, toDoubles.Message);
        Assert.Contains("ダブル", toDoubles.Message);
        Assert.Equal(BattleFormat.Doubles, (await TeamAsync(repo, 1)).BattleFormat);
        var toSingles = await command.ExecuteAsync("!pokech set -t 1 シングル");
        Assert.True(toSingles.Success, toSingles.Message);
        Assert.Equal(BattleFormat.Singles, (await TeamAsync(repo, 1)).BattleFormat);
        Assert.Equal("Singles", (await TeamAsync(repo, 1)).Format);
    }

    [Theory]
    [InlineData("!pokech set -t")]
    [InlineData("!pokech set -t 1")]
    [InlineData("!pokech set -t 1 トリプル")]
    [InlineData("!pokech set -t x ダブル")]
    [InlineData("!pokech set -t 1 ダブル 余分")]
    public async Task Set_explains_the_usage_for_bad_input(string input)
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1");
        var result = await command.ExecuteAsync(input);
        Assert.False(result.Success);
        Assert.Contains("set -t", result.Message);
        Assert.Equal(BattleFormat.Singles, (await TeamAsync(repo, 1)).BattleFormat);
    }

    [Fact]
    public async Task Set_for_a_missing_team_fails()
    {
        var (command, _) = await NewAsync();
        var result = await command.ExecuteAsync("!pokech set -t 9 ダブル");
        Assert.False(result.Success);
        Assert.Contains("チーム9", result.Message);
    }

    [Fact]
    public async Task Only_streamer_and_moderators_can_change_the_format()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1");
        var denied = await command.ExecuteAsync("!pokech set -t 1 ダブル", new PokeCommandOptions { AllowMutations = false });
        Assert.False(denied.Success);
        Assert.Equal(BattleFormat.Singles, (await TeamAsync(repo, 1)).BattleFormat);
        var deniedAdd = await command.ExecuteAsync("!pokech add -t 2 1 ダブル", new PokeCommandOptions { AllowMutations = false });
        Assert.False(deniedAdd.Success);
        Assert.DoesNotContain((await repo.GetAsync()).For(Ds).Teams, t => t.Number == 2);
    }

    [Fact]
    public async Task Ls_and_detail_show_doubles_only()
    {
        var (command, _) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1");
        await command.ExecuteAsync("!pokech add -t 2 2 ダブル");
        var list = (await command.ExecuteAsync("!pokech ls -t")).Message;
        Assert.Contains("チーム2(ダブル)", list);
        Assert.DoesNotContain("チーム1(ダブル)", list);
        Assert.Contains("(ダブル)", (await command.ExecuteAsync("!pokech detail -t 2")).Message);
        Assert.DoesNotContain("(ダブル)", (await command.ExecuteAsync("!pokech detail -t 1")).Message);
    }

    [Fact]
    public async Task Help_mentions_the_format()
    {
        Assert.Contains("ダブル", PokeCommand.Usage("pokech"));
        var (command, _) = await NewAsync();
        Assert.Contains("set -t", (await command.ExecuteAsync("!pokech help")).Message);
    }

    // ---- クラウド（共有）

    [Fact]
    public async Task Format_survives_a_round_trip_through_the_cloud()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        var entry = await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "ガブリアス"), new StatSet(), data.Natures.First(), null);
        await repo.AddToTeamAsync(Ds, 1, entry.Id, null, BattleFormat.Doubles);
        Assert.Equal("Doubles", server.State.For(Ds).Teams.Single().Format);

        // 別の端末（新しく読み込んだリポジトリ）でも、ダブルのまま
        var other = new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        Assert.Equal(BattleFormat.Doubles, (await other.GetAsync()).For(Ds).Teams.Single().BattleFormat);

        // 形式を変えて、別のチームの操作をしても、変えた形式が消えない
        Assert.True(await other.SetTeamFormatAsync(Ds, 1, BattleFormat.Singles));
        await other.AddToTeamAsync(Ds, 2, entry.Id, null);
        Assert.Equal("Singles", server.State.For(Ds).Teams.First(t => t.Number == 1).Format);
    }

    [Fact]
    public async Task Repository_set_format_returns_false_for_a_missing_team()
    {
        var (_, repo) = await NewAsync();
        Assert.False(await repo.SetTeamFormatAsync(Ds, 5, BattleFormat.Doubles));
    }
}
