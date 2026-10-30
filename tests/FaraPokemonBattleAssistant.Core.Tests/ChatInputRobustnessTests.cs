using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>チャットコマンドに、足りない・範囲外・おかしな入力が来ても、例外にならず、理由を返すことのテスト。</summary>
public class ChatInputRobustnessTests
{
    private static async Task<PokeCommand> NewAsync()
    {
        var repo = new RosterRepository(new MemoryRosterStore());
        var command = new PokeCommand(TestData.Catalog(), repo);
        Assert.True((await command.ExecuteAsync("!pokesv add -m ガブリアス A32 S32 ようき じしん")).Success);
        return command;
    }

    [Theory]
    [InlineData("!pokesv dmg")]
    [InlineData("!pokesv dmg ガブリアス")]
    [InlineData("!pokesv dmg ガブリアス じしん")]
    [InlineData("!pokesv scan")]
    [InlineData("!pokesv scan ガブリアス じしん ハバタクカミ")]
    [InlineData("!pokesv scan ガブリアス じしん ハバタクカミ 0")]
    [InlineData("!pokesv scan ガブリアス じしん ハバタクカミ -5")]
    [InlineData("!pokesv scan ガブリアス じしん ハバタクカミ 150%")]
    [InlineData("!pokesv scan ガブリアス じしん ハバタクカミ abc")]
    [InlineData("!pokesv scan ガブリアス じしん ハバタクカミ 1e400")]
    [InlineData("!pokesv faster")]
    [InlineData("!pokesv faster ガブリアス")]
    [InlineData("!pokesv add -r")]
    [InlineData("!pokesv add -r ガブリアス H999")]
    [InlineData("!pokesv add -r ガブリアス 1 2 3 4 5 -1")]
    [InlineData("!pokesv add -t 1 99")]
    [InlineData("!pokesv add -t abc 1")]
    [InlineData("!pokesv use -t 99")]
    [InlineData("!pokesv rm -m 99")]
    [InlineData("!pokesv set -m 99 H32")]
    [InlineData("!pokesv detail -m abc")]
    public async Task Bad_input_does_not_throw_and_explains_itself(string text)
    {
        var command = await NewAsync();
        var result = await command.ExecuteAsync(text);   // 例外にならない
        Assert.False(result.Success, result.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("Lv0", "Lv1)")]       // 範囲外のレベルは、範囲内に丸めて、丸めた値を表示する
    [InlineData("Lv999", "Lv100)")]
    [InlineData("A9999", "A")]       // 範囲外の努力値・ランクも例外にならない
    [InlineData("+99", "")]
    public async Task Out_of_range_values_are_clamped_without_throwing(string token, string shown)
    {
        var command = await NewAsync();
        var result = await command.ExecuteAsync($"!pokesv dmg ガブリアス じしん ハバタクカミ {token}");
        Assert.True(result.Success, result.Message);
        Assert.Contains(shown, result.Message);
    }

    [Fact]
    public async Task The_seventh_pokemon_cannot_be_added_to_a_team()
    {
        var repo = new RosterRepository(new MemoryRosterStore());
        var command = new PokeCommand(TestData.Catalog(), repo);
        for (var i = 0; i < 7; i++)
            Assert.True((await command.ExecuteAsync("!pokesv add -m ガブリアス A32 S32 ようき")).Success);
        for (var id = 1; id <= 6; id++)
            Assert.True((await command.ExecuteAsync($"!pokesv add -t 1 {id}")).Success);
        var seventh = await command.ExecuteAsync("!pokesv add -t 1 7");
        Assert.False(seventh.Success, seventh.Message);
    }

    [Fact]
    public async Task A_long_reply_is_cut_to_the_maximum_length()
    {
        var repo = new RosterRepository(new MemoryRosterStore());
        var command = new PokeCommand(TestData.Catalog(), repo);
        for (var i = 0; i < 6; i++) await command.ExecuteAsync("!pokesv add -m ガブリアス A32 S32 ようき");
        var list = await command.ExecuteAsync("!pokesv ls -m", new PokeCommandOptions { MaxLength = 40 });
        Assert.True(list.Message.Length <= 40, list.Message);
    }
}
