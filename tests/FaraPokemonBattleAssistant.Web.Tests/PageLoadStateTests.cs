using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 読み込み中の検索の ID の保留（<see cref="HeldQuery"/>）と、次の成功でやり直す印（<see cref="DeferredFlag"/>）。
/// </summary>
public class PageLoadStateTests
{
    [Fact]
    public void Held_query_applies_only_when_its_own_game_loads_successfully()
    {
        var held = new HeldQuery();
        held.Hold("Gen9");

        Assert.False(held.Complete("Champions", succeeded: true));   // 別のゲームが読めたら、捨てる
        Assert.False(held.IsHeld);
    }

    [Fact]
    public void Held_query_is_dropped_when_its_game_fails_to_load()
    {
        var held = new HeldQuery();
        held.Hold("Gen9");

        Assert.False(held.Complete("Gen9", succeeded: false));
        // 失敗のあとに、同じゲームが読めても、捨てた ID は入らない
        Assert.False(held.Complete("Gen9", succeeded: true));
    }

    [Fact]
    public void Held_query_applies_once_on_the_matching_success()
    {
        var held = new HeldQuery();
        held.Hold("Gen9");

        Assert.True(held.Complete("Gen9", succeeded: true));
        Assert.False(held.Complete("Gen9", succeeded: true));   // 二度は適用しない
    }

    [Fact]
    public void Newer_hold_replaces_older_one()
    {
        var held = new HeldQuery();
        held.Hold("Champions");
        held.Hold("Gen9");

        Assert.Equal("Gen9", held.Key);
        Assert.False(held.Complete("Champions", succeeded: true));
    }

    [Fact]
    public void Deferred_flag_survives_until_a_take_and_is_cleared_by_it()
    {
        var flag = new DeferredFlag();
        Assert.False(flag.Take());

        flag.Set();
        Assert.True(flag.IsSet);
        Assert.True(flag.Take());    // 成功した読み込みで、やり直す
        Assert.False(flag.Take());   // 一度で下ろす
    }
}
