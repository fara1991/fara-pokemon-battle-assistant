using FaraPokemonBattleAssistant.Core.Battle;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>ダブルバトルの素早さ比較（4 体の動く順番）。</summary>
public class SpeedOrderTests
{
    private static SpeedEntry E(string label, int speed, string team = "") => new(label, speed, team);

    [Fact]
    public void Rank_orders_fastest_first()
    {
        var ranks = SpeedOrder.Rank(new[] { E("A", 100), E("B", 150), E("C", 80), E("D", 120) }, trickRoom: false);
        Assert.Equal(new[] { "B", "D", "A", "C" }, ranks.SelectMany(r => r.Entries).Select(e => e.Label));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ranks.Select(r => r.Rank));
        Assert.All(ranks, r => Assert.False(r.IsTie));
    }

    [Fact]
    public void Rank_trick_room_orders_slowest_first()
    {
        var ranks = SpeedOrder.Rank(new[] { E("A", 100), E("B", 150), E("C", 80), E("D", 120) }, trickRoom: true);
        Assert.Equal(new[] { "C", "A", "D", "B" }, ranks.SelectMany(r => r.Entries).Select(e => e.Label));
    }

    [Fact]
    public void Rank_groups_equal_speeds_and_skips_the_following_rank()
    {
        var ranks = SpeedOrder.Rank(new[] { E("A", 100), E("B", 100), E("C", 80), E("D", 150) }, trickRoom: false);
        Assert.Equal(3, ranks.Count);
        Assert.Equal(new[] { 1, 2, 4 }, ranks.Select(r => r.Rank));   // 同速の次は 4 位
        Assert.True(ranks[1].IsTie);
        Assert.Equal(new[] { "A", "B" }, ranks[1].Entries.Select(e => e.Label));   // 同速は、渡した順のまま
    }

    [Fact]
    public void Rank_handles_fewer_than_four_and_none()
    {
        Assert.Empty(SpeedOrder.Rank(Array.Empty<SpeedEntry>(), false));
        var one = SpeedOrder.Rank(new[] { E("A", 90) }, true);
        Assert.Single(one);
        Assert.Equal(1, one[0].Rank);
    }

    [Fact]
    public void Rank_all_tied_is_a_single_group()
    {
        var ranks = SpeedOrder.Rank(new[] { E("A", 100), E("B", 100), E("C", 100), E("D", 100) }, false);
        Assert.Single(ranks);
        Assert.Equal(4, ranks[0].Entries.Count);
    }

    [Fact]
    public void Describe_joins_ranks_with_arrows_and_ties_with_equals()
    {
        var ranks = SpeedOrder.Rank(new[] { E("A", 100), E("B", 100), E("C", 80), E("D", 150) }, false);
        Assert.Equal("D → A = B → C", SpeedOrder.Describe(ranks));
    }
}
