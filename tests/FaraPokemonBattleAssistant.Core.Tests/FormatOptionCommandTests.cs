using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>チャットコマンドの形式の指定（-f / --format）、形式の登録（format set / clear）、形式ごとのチーム操作。</summary>
public class FormatOptionCommandTests
{
    private const string Ds = "Champions";

    private static async Task<(PokeCommand Command, RosterRepository Repo)> NewAsync()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "マニューラ"), new StatSet { Attack = 32 }, data.Natures.First(), null);
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "ガブリアス"), new StatSet { Speed = 32 }, data.Natures.First(), null);
        return (new PokeCommand(TestData.Catalog(), repo), repo);
    }

    private static async Task<DataSetRoster> RosterAsync(RosterRepository repo) => (await repo.GetAsync()).For(Ds);

    private static PokeCommandOptions Registered(BattleFormat format) => new() { Format = format, FormatIsRegistered = true };

    // ---- オプションの読み取り

    [Theory]
    [InlineData("-f シングル", BattleFormat.Singles)]
    [InlineData("-f single", BattleFormat.Singles)]
    [InlineData("-f ダブル", BattleFormat.Doubles)]
    [InlineData("-f double", BattleFormat.Doubles)]
    [InlineData("--format シングル", BattleFormat.Singles)]
    [InlineData("--format single", BattleFormat.Singles)]
    [InlineData("--format ダブル", BattleFormat.Doubles)]
    [InlineData("--format double", BattleFormat.Doubles)]
    [InlineData("-f singles", BattleFormat.Singles)]
    [InlineData("-f doubles", BattleFormat.Doubles)]
    [InlineData("-F DOUBLE", BattleFormat.Doubles)]
    [InlineData("--FORMAT Single", BattleFormat.Singles)]
    [InlineData("-f　ダブル", BattleFormat.Doubles)]                    // 全角スペース区切り
    [InlineData("-f   ダブル", BattleFormat.Doubles)]                   // 連続した空白
    [InlineData("－ｆ　ｄｏｕｂｌｅ", BattleFormat.Doubles)]            // 全角の英数字・ハイフン
    [InlineData("--format=ダブル", BattleFormat.Doubles)]
    [InlineData("-f=single", BattleFormat.Singles)]
    [InlineData("-fダブル", BattleFormat.Doubles)]
    public void Every_notation_of_the_option_is_read(string option, BattleFormat expected)
    {
        var words = CommandText.Normalize("add -t 1 1 " + option).Split(' ').ToList();
        var (rest, format, error) = FormatOption.Extract(words);
        Assert.Null(error);
        Assert.Equal(expected, format);
        Assert.Equal("add -t 1 1", string.Join(" ", rest));   // オプションは言葉の並びから取り除く
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("--format")]
    [InlineData("-f トリプル")]
    [InlineData("--format=")]
    [InlineData("-f ダブル -f シングル")]
    public void Bad_option_gets_an_explanation(string option)
    {
        var (_, _, error) = FormatOption.Extract(option.Split(' '));
        Assert.NotNull(error);
    }

    [Fact]
    public void Same_format_written_twice_is_accepted()
    {
        var (rest, format, error) = FormatOption.Extract(new[] { "-f", "ダブル", "--format", "double" });
        Assert.Null(error);
        Assert.Equal(BattleFormat.Doubles, format);
        Assert.Empty(rest);
    }

    [Fact]
    public void No_option_leaves_the_words_alone()
    {
        var (rest, format, error) = FormatOption.Extract(new[] { "1", "2", "こだわりスカーフ", "-fire" });
        Assert.Null(error);
        Assert.Null(format);
        Assert.Equal(4, rest.Count);
    }

    // ---- 追加

    [Theory]
    [InlineData("!pokech add -t 1 1 -f ダブル")]
    [InlineData("!pokech add -t 1 1 -f double")]
    [InlineData("!pokech add -t 1 1 --format ダブル")]
    [InlineData("!pokech add -t 1 1 --format double")]
    [InlineData("!pokech add -t 1 -f　ダブル 1")]              // 全角スペース・番号の途中
    [InlineData("!pokech add -t 1 1 ダブル")]                  // 古い書き方（末尾の語）も受け付ける
    public async Task Add_with_the_option_makes_a_doubles_team(string input)
    {
        var (command, repo) = await NewAsync();
        var added = await command.ExecuteAsync(input);
        Assert.True(added.Success, added.Message);
        Assert.Contains("ダブルのチーム1", added.Message);
        var roster = await RosterAsync(repo);
        Assert.Equal(BattleFormat.Doubles, Assert.Single(roster.Teams).BattleFormat);
        Assert.Null(roster.Teams.Single().Slots.Single().ItemId);   // 「ダブル」を持ち物として読まない
    }

    [Fact]
    public async Task Option_before_the_target_is_not_a_target()
    {
        // 対象（-t など）はサブコマンドの直後に置く決まり。-f を先に置くと、存在しないオプションとして案内する
        var (command, repo) = await NewAsync();
        var result = await command.ExecuteAsync("!pokech add -f ダブル -t 1 1");
        Assert.False(result.Success);
        Assert.Contains("-f", result.Message);
        Assert.Empty((await RosterAsync(repo)).Teams);
    }

    [Fact]
    public async Task Add_with_an_item_and_the_option_reads_both()
    {
        var (command, repo) = await NewAsync();
        var added = await command.ExecuteAsync("!pokech add -t 2 2 こだわりスカーフ -f ダブル");
        Assert.True(added.Success, added.Message);
        var team = (await RosterAsync(repo)).FindTeam(BattleFormat.Doubles, 2)!;
        Assert.NotNull(team.Slots.Single().ItemId);
        var legacy = await command.ExecuteAsync("!pokech add -t 3 2 こだわりスカーフ ダブル");
        Assert.True(legacy.Success, legacy.Message);
        Assert.NotNull((await RosterAsync(repo)).FindTeam(BattleFormat.Doubles, 3)!.Slots.Single().ItemId);
    }

    [Fact]
    public async Task Option_and_trailing_word_must_agree()
    {
        var (command, repo) = await NewAsync();
        var result = await command.ExecuteAsync("!pokech add -t 1 1 -f シングル ダブル");
        Assert.False(result.Success);
        Assert.Empty((await RosterAsync(repo)).Teams);
        Assert.True((await command.ExecuteAsync("!pokech add -t 1 1 -f ダブル ダブル")).Success);
    }

    [Fact]
    public async Task Bad_format_value_creates_nothing()
    {
        var (command, repo) = await NewAsync();
        var result = await command.ExecuteAsync("!pokech add -t 1 1 -f トリプル");
        Assert.False(result.Success);
        Assert.Contains("トリプル", result.Message);
        Assert.Empty((await RosterAsync(repo)).Teams);
    }

    [Fact]
    public async Task Omitted_format_uses_the_registered_one()
    {
        var (command, repo) = await NewAsync();
        var inSingles = await command.ExecuteAsync("!pokech add -t 1 1");   // 登録なし = 既定のシングル
        Assert.Contains("シングルのチーム1", inSingles.Message);
        var inDoubles = await command.ExecuteAsync("!pokech add -t 1 2", Registered(BattleFormat.Doubles));
        Assert.True(inDoubles.Success, inDoubles.Message);
        Assert.Contains("ダブルのチーム1", inDoubles.Message);
        var roster = await RosterAsync(repo);
        Assert.Equal(1, roster.FindTeam(BattleFormat.Singles, 1)!.Slots.Single().EntryId);
        Assert.Equal(2, roster.FindTeam(BattleFormat.Doubles, 1)!.Slots.Single().EntryId);
        // -f は登録した形式より優先
        var explicitSingles = await command.ExecuteAsync("!pokech add -t 2 1 -f シングル", Registered(BattleFormat.Doubles));
        Assert.Contains("シングルのチーム2", explicitSingles.Message);
    }

    [Fact]
    public async Task Add_hints_when_the_other_format_has_the_same_number()
    {
        var (command, _) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 2 1 -f ダブル");
        var result = await command.ExecuteAsync("!pokech add -t 2 2");
        Assert.True(result.Success, result.Message);
        Assert.Contains("シングルのチーム2", result.Message);
        Assert.Contains("ダブルのチーム2はあります", result.Message);
        Assert.DoesNotContain("はあります", (await command.ExecuteAsync("!pokech add -t 2 2 -f ダブル")).Message);   // 形式を書いたときは添えない
    }

    // ---- 同じ番号が両形式にあるときの、各コマンド

    private static async Task<(PokeCommand Command, RosterRepository Repo)> BothAsync()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1");
        await command.ExecuteAsync("!pokech add -t 1 2 -f ダブル");
        return (command, repo);
    }

    [Fact]
    public async Task Use_and_unuse_are_per_format()
    {
        var (command, repo) = await BothAsync();
        var useSingles = await command.ExecuteAsync("!pokech use -t 1");
        Assert.Contains("シングルのチーム1", useSingles.Message);
        var useDoubles = await command.ExecuteAsync("!pokech use -t 1 --format double");
        Assert.True(useDoubles.Success, useDoubles.Message);
        Assert.Contains("ダブルのチーム1", useDoubles.Message);
        var roster = await RosterAsync(repo);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Doubles));

        var unuse = await command.ExecuteAsync("!pokech unuse -t -f ダブル");
        Assert.Contains("ダブル", unuse.Message);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));
        // 登録した形式が省略時の対象
        await command.ExecuteAsync("!pokech unuse -t", Registered(BattleFormat.Singles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Singles));
        Assert.Contains("使用中のチームはありません", (await command.ExecuteAsync("!pokech unuse -t")).Message);
    }

    [Fact]
    public async Task Use_of_a_number_that_exists_only_in_the_other_format_explains_how()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 5 1 -f ダブル");
        var result = await command.ExecuteAsync("!pokech use -t 5");
        Assert.False(result.Success);
        Assert.Contains("シングルのチーム5はありません", result.Message);
        Assert.Contains("ダブルのチーム5はあります", result.Message);
        Assert.Contains("-f ダブル", result.Message);
        Assert.Null((await RosterAsync(repo)).ActiveNumber(BattleFormat.Doubles));
    }

    [Fact]
    public async Task Rm_never_removes_the_other_format()
    {
        var (command, repo) = await BothAsync();
        var removeSingles = await command.ExecuteAsync("!pokech rm -t 1");
        Assert.Contains("シングルのチーム1 を削除しました", removeSingles.Message);
        var roster = await RosterAsync(repo);
        Assert.Null(roster.FindTeam(BattleFormat.Singles, 1));
        Assert.NotNull(roster.FindTeam(BattleFormat.Doubles, 1));
        // もう一度 rm -t 1 しても、ダブルは消えない（案内だけ）
        var again = await command.ExecuteAsync("!pokech rm -t 1");
        Assert.False(again.Success);
        Assert.Contains("ダブルのチーム1はあります", again.Message);
        Assert.NotNull(roster.FindTeam(BattleFormat.Doubles, 1));
        // 中のポケモンを外す: 形式つき
        Assert.Contains("ダブルのチーム1 から #2 を外しました", (await command.ExecuteAsync("!pokech rm -t 1 #2 -f ダブル")).Message);
        Assert.Null((await RosterAsync(repo)).FindTeam(BattleFormat.Doubles, 1));
    }

    [Fact]
    public async Task Ls_shows_both_formats_without_the_option_and_one_with_it()
    {
        var (command, _) = await BothAsync();
        await command.ExecuteAsync("!pokech use -t 1");
        await command.ExecuteAsync("!pokech add -t 2 2 -f ダブル");
        var both = (await command.ExecuteAsync("!pokech ls -t")).Message;
        Assert.Contains("シングル: チーム1(使用中)[マニューラ]", both);
        Assert.Contains("ダブル: チーム1[ガブリアス] チーム2[ガブリアス]", both);
        Assert.True(both.IndexOf("シングル:", StringComparison.Ordinal) < both.IndexOf("ダブル:", StringComparison.Ordinal));
        var doubles = (await command.ExecuteAsync("!pokech ls -t -f ダブル")).Message;
        Assert.DoesNotContain("シングル:", doubles);
        Assert.Contains("チーム2", doubles);
        var singles = (await command.ExecuteAsync("!pokech ls -t --format シングル")).Message;
        Assert.DoesNotContain("ダブル:", singles);
        // 登録した形式があっても、形式を省略した ls -t は両方
        Assert.Contains("ダブル:", (await command.ExecuteAsync("!pokech ls -t", Registered(BattleFormat.Singles))).Message);
        Assert.Contains("シングル:", (await command.ExecuteAsync("!pokech ls -t", Registered(BattleFormat.Doubles))).Message);
    }

    [Fact]
    public async Task Ls_marks_none_for_a_format_without_teams()
    {
        var (command, _) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1");
        Assert.Contains("ダブル: なし", (await command.ExecuteAsync("!pokech ls -t")).Message);
        Assert.Contains("チームはありません", (await (await NewAsync()).Command.ExecuteAsync("!pokech ls -t")).Message);
    }

    [Fact]
    public async Task Detail_is_per_format_with_a_hint()
    {
        var (command, _) = await BothAsync();
        var singles = (await command.ExecuteAsync("!pokech detail -t 1")).Message;
        Assert.StartsWith("シングルのチーム1", singles);
        Assert.Contains("マニューラ", singles);
        var doubles = (await command.ExecuteAsync("!pokech detail -t 1 -f ダブル")).Message;
        Assert.StartsWith("ダブルのチーム1", doubles);
        Assert.Contains("ガブリアス", doubles);
        Assert.StartsWith("ダブルのチーム1", (await command.ExecuteAsync("!pokech detail -t 1", Registered(BattleFormat.Doubles))).Message);
        await command.ExecuteAsync("!pokech add -t 7 1");
        var missing = await command.ExecuteAsync("!pokech detail -t 7 -f ダブル");
        Assert.False(missing.Success);
        Assert.Contains("シングルのチーム7はあります", missing.Message);
    }

    [Fact]
    public async Task Rm_m_and_detail_m_name_the_format_of_each_team()
    {
        var (command, repo) = await BothAsync();
        var detail = (await command.ExecuteAsync("!pokech detail -m 1")).Message;
        Assert.Contains("シングルのチーム1", detail);
        // 登録 2 を消すと、ダブルのチーム1 が 0 体になって消える
        var removed = (await command.ExecuteAsync("!pokech rm -m 2")).Message;
        Assert.Contains("ダブルのチーム1を削除しました", removed);
        Assert.NotNull((await RosterAsync(repo)).FindTeam(BattleFormat.Singles, 1));
    }

    // ---- set -t: 別の形式への移動

    [Fact]
    public async Task Set_moves_a_team_to_the_other_format()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 3 1");
        await command.ExecuteAsync("!pokech use -t 3");
        var moved = await command.ExecuteAsync("!pokech set -t 3 ダブル");
        Assert.True(moved.Success, moved.Message);
        Assert.Contains("シングルのチーム3 を ダブルのチーム3 に移しました", moved.Message);
        Assert.Contains("使用中は解除", moved.Message);
        var roster = await RosterAsync(repo);
        Assert.Null(roster.FindTeam(BattleFormat.Singles, 3));
        Assert.Equal(BattleFormat.Doubles, roster.FindTeam(BattleFormat.Doubles, 3)!.BattleFormat);
        Assert.Null(roster.ActiveNumber(BattleFormat.Singles));
        // 元に戻す（移す先と反対の形式が元）
        Assert.True((await command.ExecuteAsync("!pokech set -t 3 シングル")).Success);
        Assert.NotNull((await RosterAsync(repo)).FindTeam(BattleFormat.Singles, 3));
    }

    [Fact]
    public async Task Set_fails_on_a_collision_and_never_renumbers()
    {
        var (command, repo) = await BothAsync();
        var result = await command.ExecuteAsync("!pokech set -t 1 ダブル");
        Assert.False(result.Success);
        Assert.Contains("ダブルのチーム1が既にあります", result.Message);
        Assert.Contains("set -t 1 ダブル 2", result.Message);   // 空いている番号の例
        var roster = await RosterAsync(repo);
        Assert.Equal(2, roster.Teams.Count);
        Assert.Equal(new[] { 1, 1 }, roster.Teams.Select(t => t.Number));
        var withNumber = await command.ExecuteAsync("!pokech set -t 1 ダブル 2");
        Assert.True(withNumber.Success, withNumber.Message);
        Assert.Contains("ダブルのチーム2", withNumber.Message);
        Assert.NotNull((await RosterAsync(repo)).FindTeam(BattleFormat.Doubles, 2));
        Assert.Null((await RosterAsync(repo)).FindTeam(BattleFormat.Singles, 1));
    }

    [Fact]
    public async Task Set_with_the_option_renumbers_inside_one_format()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1 -f ダブル");
        Assert.True((await command.ExecuteAsync("!pokech set -t 1 -f ダブル ダブル 4")).Success);
        Assert.NotNull((await RosterAsync(repo)).FindTeam(BattleFormat.Doubles, 4));
        var same = await command.ExecuteAsync("!pokech set -t 4 -f ダブル ダブル");
        Assert.Contains("すでにダブル", same.Message);
    }

    [Theory]
    [InlineData("!pokech set -t")]
    [InlineData("!pokech set -t 1")]
    [InlineData("!pokech set -t 1 トリプル")]
    [InlineData("!pokech set -t x ダブル")]
    [InlineData("!pokech set -t 1 ダブル 0")]
    [InlineData("!pokech set -t 1 ダブル 2 余分")]
    public async Task Set_explains_the_usage_for_bad_input(string input)
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 1 1");
        var result = await command.ExecuteAsync(input);
        Assert.False(result.Success);
        Assert.Contains("set -t", result.Message);
        Assert.NotNull((await RosterAsync(repo)).FindTeam(BattleFormat.Singles, 1));
    }

    [Fact]
    public async Task Set_for_a_missing_team_fails_with_a_hint()
    {
        var (command, _) = await NewAsync();
        var result = await command.ExecuteAsync("!pokech set -t 9 ダブル");
        Assert.False(result.Success);
        Assert.Contains("シングルのチーム9", result.Message);
    }

    [Fact]
    public async Task Set_for_a_number_already_in_the_target_format_says_so_instead_of_pointing_back()
    {
        var (command, _) = await NewAsync();
        await command.ExecuteAsync("!pokech add -t 5 1 -f ダブル");
        var result = await command.ExecuteAsync("!pokech set -t 5 ダブル");
        Assert.False(result.Success);
        Assert.Contains("ダブルのチーム5はすでにダブルです", result.Message);
        Assert.DoesNotContain("-f ダブル を付けて", result.Message);
    }

    [Theory]
    [InlineData("!pokech dmg ガブリアス じしん リザードン -f シングル ダブル")]
    [InlineData("!pokech dmg ガブリアス じしん リザードン ダブル -f シングル")]
    [InlineData("!pokech dmg ガブリアス じしん リザードン --format double シングル")]
    [InlineData("!pokech dmg ガブリアス じしん リザードン シングル ダブル")]
    [InlineData("!pokech faster ガブリアス ガブリアス -f ダブル シングル")]
    public async Task Conflicting_formats_in_a_calculation_are_an_error(string input)
    {
        var (command, _) = await TwoBuildsAsync();
        var result = await command.ExecuteAsync(input);
        Assert.False(result.Success);
        Assert.Contains("形式が 2 つ書かれています", result.Message);
        // 同じ形式を重ねて書くのは問題ない
        Assert.True((await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン -f ダブル ダブル")).Success);
    }

    [Fact]
    public async Task Team_numbers_above_the_cloud_limit_are_refused_in_commands()
    {
        var (command, repo) = await NewAsync();
        foreach (var input in new[] { "!pokech add -t 2147483647 1", "!pokech use -t 2147483647", "!pokech rm -t 2147483647", "!pokech detail -t 2147483647", "!pokech set -t 1 ダブル 2147483647" })
            Assert.False((await command.ExecuteAsync(input)).Success, input);
        Assert.Empty((await RosterAsync(repo)).Teams);
        Assert.True((await command.ExecuteAsync("!pokech add -t 2147483646 1")).Success);
        Assert.NotNull((await RosterAsync(repo)).FindTeam(BattleFormat.Singles, 2147483646));
        Assert.False((await command.ExecuteAsync("!pokech set -t 2147483646 ダブル 2147483647")).Success);
    }

    [Fact]
    public async Task Team_changes_need_streamer_or_moderator()
    {
        var (command, repo) = await BothAsync();
        var denied = new PokeCommandOptions { AllowMutations = false };
        foreach (var input in new[] { "!pokech set -t 1 ダブル 5", "!pokech add -t 2 1 -f ダブル", "!pokech rm -t 1 -f ダブル", "!pokech use -t 1 -f ダブル", "!pokech unuse -t -f ダブル" })
            Assert.False((await command.ExecuteAsync(input, denied)).Success, input);
        Assert.Equal(2, (await RosterAsync(repo)).Teams.Count);
        // 読むだけは誰でも
        Assert.True((await command.ExecuteAsync("!pokech ls -t -f ダブル", denied)).Success);
        Assert.True((await command.ExecuteAsync("!pokech detail -t 1 -f ダブル", denied)).Success);
    }

    // ---- 形式の登録（format set / clear）

    private sealed class FormatStore
    {
        public BattleFormat? Saved { get; private set; }
        public int Calls { get; private set; }
        public Task Save(BattleFormat? format, CancellationToken ct) { Saved = format; Calls++; return Task.CompletedTask; }
    }

    [Theory]
    [InlineData("!pokech format set ダブル", BattleFormat.Doubles)]
    [InlineData("!pokech format set double", BattleFormat.Doubles)]
    [InlineData("!pokech format set シングル", BattleFormat.Singles)]
    [InlineData("!pokech format set single", BattleFormat.Singles)]
    [InlineData("!pokech format set -f ダブル", BattleFormat.Doubles)]
    [InlineData("!pokech format set --format single", BattleFormat.Singles)]
    [InlineData("!pokech format ダブル", BattleFormat.Doubles)]   // set の省略
    [InlineData("!pokech format set　ダブル", BattleFormat.Doubles)]
    public async Task Format_set_saves_the_registration(string input, BattleFormat expected)
    {
        var (command, _) = await NewAsync();
        var store = new FormatStore();
        var result = await command.ExecuteAsync(input, new PokeCommandOptions { SaveFormatAsync = store.Save });
        Assert.True(result.Success, result.Message);
        Assert.Equal(expected, store.Saved);
        Assert.Equal(1, store.Calls);
        Assert.Contains(TeamFormats.Label(expected), result.Message);
    }

    [Theory]
    [InlineData("!pokech format clear")]
    [InlineData("!pokech format unset")]
    [InlineData("!pokech format reset")]
    public async Task Format_clear_removes_the_registration(string input)
    {
        var (command, _) = await NewAsync();
        var store = new FormatStore();
        var result = await command.ExecuteAsync(input, new PokeCommandOptions { Format = BattleFormat.Doubles, FormatIsRegistered = true, SaveFormatAsync = store.Save });
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, store.Calls);
        Assert.Null(store.Saved);
        Assert.Contains("解除", result.Message);
        Assert.Contains("シングル", result.Message);
    }

    [Theory]
    [InlineData("!pokech format set")]
    [InlineData("!pokech format set トリプル")]
    [InlineData("!pokech format set ダブル シングル")]
    [InlineData("!pokech format set ダブル -f シングル")]
    [InlineData("!pokech format clear ダブル")]
    [InlineData("!pokech format clear -f ダブル")]
    [InlineData("!pokech format blah")]
    public async Task Format_with_bad_input_saves_nothing(string input)
    {
        var (command, _) = await NewAsync();
        var store = new FormatStore();
        var result = await command.ExecuteAsync(input, new PokeCommandOptions { SaveFormatAsync = store.Save });
        Assert.False(result.Success);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Format_shows_the_current_registration()
    {
        var (command, _) = await NewAsync();
        var none = (await command.ExecuteAsync("!pokech format")).Message;
        Assert.Contains("登録していません", none);
        Assert.Contains("シングル", none);
        var registered = (await command.ExecuteAsync("!pokech format", Registered(BattleFormat.Doubles))).Message;
        Assert.Contains("登録している形式はダブル", registered);
        Assert.Equal(registered, (await command.ExecuteAsync("!pokech format show", Registered(BattleFormat.Doubles))).Message);
    }

    [Fact]
    public async Task Format_changes_need_streamer_or_moderator_but_showing_does_not()
    {
        var (command, _) = await NewAsync();
        var store = new FormatStore();
        var denied = new PokeCommandOptions { AllowMutations = false, SaveFormatAsync = store.Save };
        Assert.False((await command.ExecuteAsync("!pokech format set ダブル", denied)).Success);
        Assert.False((await command.ExecuteAsync("!pokech format clear", denied)).Success);
        Assert.Equal(0, store.Calls);
        Assert.True((await command.ExecuteAsync("!pokech format", denied)).Success);
    }

    [Fact]
    public async Task Format_set_without_a_place_to_save_says_so()
    {
        var (command, _) = await NewAsync();
        var result = await command.ExecuteAsync("!pokech format set ダブル");
        Assert.False(result.Success);
        Assert.Contains("保存できません", result.Message);
    }

    [Fact]
    public async Task Registered_format_applies_to_later_commands_in_the_same_flow()
    {
        // 登録 → (BOT が Format を更新して渡す) → 形式を省略したコマンドが登録した形式で動く
        var (command, repo) = await NewAsync();
        var store = new FormatStore();
        await command.ExecuteAsync("!pokech format set ダブル", new PokeCommandOptions { SaveFormatAsync = store.Save });
        var options = new PokeCommandOptions { Format = store.Saved ?? BattleFormat.Singles, FormatIsRegistered = store.Saved is not null };
        await command.ExecuteAsync("!pokech add -t 1 1", options);
        await command.ExecuteAsync("!pokech use -t 1", options);
        var roster = await RosterAsync(repo);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Doubles));
        Assert.Null(roster.ActiveNumber(BattleFormat.Singles));
        // 解除すると既定のシングルに戻る
        await command.ExecuteAsync("!pokech format clear", new PokeCommandOptions { SaveFormatAsync = store.Save });
        Assert.Null(store.Saved);
        var cleared = new PokeCommandOptions { Format = store.Saved ?? BattleFormat.Singles };
        Assert.Contains("シングルのチーム2", (await command.ExecuteAsync("!pokech add -t 2 1", cleared)).Message);
    }

    // ---- 計算は、その形式の使用中チームを使う

    private static async Task<(PokeCommand Command, RosterRepository Repo)> TwoBuildsAsync()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new MemoryRosterStore());
        var garchomp = data.Pokemon.First(p => p.Name == "ガブリアス");
        // 同じガブリアスを、攻撃の振り方を変えて 2 体登録（シングルの使用中が #1、ダブルの使用中が #2）
        await repo.AddPokemonAsync(Ds, garchomp, new StatSet { Attack = 32 }, data.Natures.First(n => n.Name == "いじっぱり"), null);
        await repo.AddPokemonAsync(Ds, garchomp, new StatSet { Attack = 0 }, data.Natures.First(n => n.Name == "ひかえめ"), null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, 1, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, 2, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1);
        return (new PokeCommand(TestData.Catalog(), repo), repo);
    }

    [Fact]
    public async Task Dmg_uses_the_active_team_of_the_calculation_format()
    {
        var (command, _) = await TwoBuildsAsync();
        var bySingles = (await command.ExecuteAsync("!pokech dmg #1 じしん リザードン")).Message;
        var byDoubles = (await command.ExecuteAsync("!pokech dmg #2 じしん リザードン ダブル")).Message;
        Assert.NotEqual(bySingles, byDoubles);

        // 名前だけ: 使用中のチームにいる 1 体が選ばれる。選ばれるチームは計算の形式で変わる
        Assert.Equal(bySingles, (await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン")).Message);
        Assert.Equal(bySingles, (await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン -f シングル")).Message);
        Assert.Equal(byDoubles, (await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン ダブル")).Message);
        Assert.Equal(byDoubles, (await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン -f ダブル")).Message);
        Assert.Equal(byDoubles, (await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン --format double")).Message);
        // 登録した形式が省略時
        Assert.Equal(byDoubles, (await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン", Registered(BattleFormat.Doubles))).Message);
        // コマンドの中の指定は、登録した形式より優先
        Assert.Equal(bySingles, (await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン -f シングル", Registered(BattleFormat.Doubles))).Message);
    }

    [Fact]
    public async Task Calculation_without_an_active_team_in_that_format_does_not_fall_back_to_the_other()
    {
        var (command, repo) = await TwoBuildsAsync();
        await repo.ClearActiveTeamAsync(Ds, BattleFormat.Doubles);
        // ダブルの使用中が無い: 同じポケモンが 2 体いるので、どちらを使うか登録 ID の指定を案内する（シングルの #1 を黙って使わない）
        var doubles = await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン -f ダブル");
        Assert.False(doubles.Success);
        Assert.Contains("2 体", doubles.Message);
        Assert.True((await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン")).Success);
    }

    [Fact]
    public async Task Faster_uses_the_active_team_of_the_calculation_format()
    {
        var (command, _) = await TwoBuildsAsync();
        var singles = (await command.ExecuteAsync("!pokech faster ガブリアス ガブリアス")).Message;
        var doubles = (await command.ExecuteAsync("!pokech faster ガブリアス ガブリアス -f ダブル")).Message;
        Assert.NotEqual(singles, doubles);   // ひかえめ(#2)とようき…性格が違うので実数値が違う
        Assert.Equal(doubles, (await command.ExecuteAsync("!pokech faster ガブリアス ガブリアス", Registered(BattleFormat.Doubles))).Message);
    }

    [Fact]
    public async Task Option_errors_in_calculation_commands_are_reported()
    {
        var (command, _) = await TwoBuildsAsync();
        var result = await command.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン -f トリプル");
        Assert.False(result.Success);
        Assert.Contains("トリプル", result.Message);
    }

    // ---- ヘルプ

    [Fact]
    public async Task Help_explains_the_option_and_the_format_command()
    {
        var usage = PokeCommand.Usage("pokech");
        Assert.Contains("-f", usage);
        Assert.Contains("format set", usage);
        Assert.Contains("format clear", usage);
        var (command, _) = await NewAsync();
        var help = (await command.ExecuteAsync("!pokech help")).Message;
        Assert.Contains("-f 形式", help);
        Assert.Contains("format set", help);
        Assert.Contains("set -t 番号 形式 [新番号]", help);
        Assert.Contains("set -t 番号 シングル|ダブル [新番号]", usage);
    }
}
