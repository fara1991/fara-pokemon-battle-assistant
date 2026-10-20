using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>コードレビューで見つかった、チャットコマンドの読み取りの不具合の回帰テスト。</summary>
public class ReviewFixCommandTests
{
    private const string Ds = "Gen9";

    private static (PokeCommand Command, RosterRepository Repo) New()
    {
        var repo = new RosterRepository(new MemoryRosterStore());
        return (new PokeCommand(TestData.Catalog(), repo), repo);
    }

    [Fact]
    public async Task A_pokemon_nickname_is_read_as_the_next_pokemon_not_as_a_move_of_the_previous_one()
    {
        var (command, repo) = New();
        // 「カミ」はハバタクカミの略称。リザードンの技「かみつく」の一部として読まれてはいけない
        var added = await command.ExecuteAsync("!pokesv add -r リザードン カミ");
        Assert.True(added.Success, added.Message);
        var rivals = (await repo.GetAsync()).For(Ds).Rivals;
        Assert.Equal(2, rivals.Count);
        Assert.Contains(rivals, r => r.PokemonName == "ハバタクカミ");
        Assert.Empty(rivals.Single(r => r.PokemonName == "リザードン").MoveIds);
    }

    [Fact]
    public async Task Moves_written_for_a_registered_pokemon_are_saved_and_items_or_picked_are_refused()
    {
        var (command, repo) = New();
        var added = await command.ExecuteAsync("!pokesv add -m ガブリアス A32 S32 ようき じしん");
        Assert.True(added.Success, added.Message);
        var entry = (await repo.GetAsync()).For(Ds).Pokemon.Single();
        Assert.Contains(89, entry.MoveIds);   // じしん

        var withItem = await command.ExecuteAsync("!pokesv set -m ガブリアス こだわりスカーフ");
        Assert.False(withItem.Success);
        Assert.Contains("持ち物", withItem.Message);
        var picked = await command.ExecuteAsync("!pokesv set -m ガブリアス 選出");
        Assert.False(picked.Success);
        Assert.Contains("選出", picked.Message);
        Assert.Contains(89, (await repo.GetAsync()).For(Ds).Pokemon.Single().MoveIds);

        // 技を書くと、その技に置き換わる
        var changed = await command.ExecuteAsync("!pokesv set -m ガブリアス げきりん");
        Assert.True(changed.Success, changed.Message);
        var moves = (await repo.GetAsync()).For(Ds).Pokemon.Single().MoveIds;
        Assert.DoesNotContain(89, moves);
        Assert.Single(moves);
    }

    [Fact]
    public async Task Scan_does_not_read_a_rank_token_as_the_observed_damage()
    {
        var (command, _) = New();
        Assert.True((await command.ExecuteAsync("!pokesv add -m ガブリアス A32 S32 ようき")).Success);
        var result = await command.ExecuteAsync("!pokesv scan ガブリアス じしん ハバタクカミ +1 93");
        Assert.Contains("93", result.Message);
        Assert.DoesNotContain("ダメージ: 1 ", result.Message);
    }
}
