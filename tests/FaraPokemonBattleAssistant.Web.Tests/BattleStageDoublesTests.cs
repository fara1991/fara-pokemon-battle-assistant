using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.JSInterop;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>背景のバトルフィールドに立たせるポケモン（BattleStage）。ダブルバトルは、それぞれの側 2 体の計 4 体。</summary>
public class BattleStageDoublesTests
{
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("JS は使えません");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("JS は使えません");
    }

    private static Pokemon P(int id, bool provisional = false, string icon = "x.png") => new() { Id = id, Name = $"P{id}", Icon = icon, IsProvisional = provisional };

    private static List<Pokemon> Many(int count) => Enumerable.Range(1, count).Select(i => P(i)).ToList();

    private static BattleStage NewStage() => new(new NoJs());

    private static int[] Ids(BattleStage s) => new[] { s.Mine?.Id ?? 0, s.Foe?.Id ?? 0, s.Mine2?.Id ?? 0, s.Foe2?.Id ?? 0 };

    [Fact]
    public void Singles_picks_two_and_no_second_pair_is_shown()
    {
        var stage = NewStage();
        stage.EnsureDefaults("set", Many(10), doubles: false);
        Assert.False(stage.Doubles);
        Assert.NotNull(stage.Mine);
        Assert.NotNull(stage.Foe);
        Assert.NotEqual(stage.Mine!.Id, stage.Foe!.Id);
    }

    [Fact]
    public void Doubles_picks_four_distinct_pokemon()
    {
        for (var trial = 0; trial < 50; trial++)
        {
            var stage = NewStage();
            stage.EnsureDefaults("set", Many(8), doubles: true);
            Assert.True(stage.Doubles);
            var ids = Ids(stage);
            Assert.All(ids, id => Assert.NotEqual(0, id));
            Assert.Equal(4, ids.Distinct().Count());
        }
    }

    [Fact]
    public void Switching_to_doubles_keeps_the_pair_and_adds_two_others()
    {
        var stage = NewStage();
        var pokemon = Many(8);
        stage.EnsureDefaults("set", pokemon, doubles: false);
        var (mine, foe) = (stage.Mine!.Id, stage.Foe!.Id);
        var fired = 0;
        stage.Changed += () => fired++;
        stage.EnsureDefaults("set", pokemon, doubles: true);
        Assert.Equal(mine, stage.Mine!.Id);
        Assert.Equal(foe, stage.Foe!.Id);
        Assert.Equal(4, Ids(stage).Distinct().Count());
        Assert.True(fired > 0);
    }

    [Fact]
    public void Switching_back_to_singles_hides_the_second_pair_but_keeps_it_for_later()
    {
        var stage = NewStage();
        var pokemon = Many(8);
        stage.EnsureDefaults("set", pokemon, doubles: true);
        var ids = Ids(stage);
        stage.EnsureDefaults("set", pokemon, doubles: false);
        Assert.False(stage.Doubles);
        stage.EnsureDefaults("set", pokemon, doubles: true);
        Assert.Equal(ids, Ids(stage));   // もう一度ダブルにしても、同じ 4 体
    }

    [Fact]
    public void Same_state_does_not_raise_Changed()
    {
        var stage = NewStage();
        var pokemon = Many(8);
        stage.EnsureDefaults("set", pokemon, doubles: true);
        var fired = 0;
        stage.Changed += () => fired++;
        stage.EnsureDefaults("set", pokemon, doubles: true);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Too_few_candidates_for_doubles_still_shows_the_first_pair()
    {
        var stage = NewStage();
        stage.EnsureDefaults("set", Many(3), doubles: true);
        Assert.NotNull(stage.Mine);
        Assert.NotNull(stage.Foe);
        Assert.NotEqual(stage.Mine!.Id, stage.Foe!.Id);
        Assert.Null(stage.Foe2);   // 4 体そろわないときは、足りない側は出さない（落ちない）
    }

    [Fact]
    public void Provisional_and_iconless_pokemon_are_never_picked()
    {
        var pokemon = new List<Pokemon> { P(1), P(2), P(3), P(4), P(5, provisional: true), P(6, icon: "") };
        for (var trial = 0; trial < 50; trial++)
        {
            var stage = NewStage();
            stage.EnsureDefaults("set", pokemon, doubles: true);
            Assert.DoesNotContain(Ids(stage), id => id is 5 or 6);
        }
    }

    [Fact]
    public void Changing_the_data_set_picks_again()
    {
        var stage = NewStage();
        stage.EnsureDefaults("a", Many(8), doubles: true);
        var other = Enumerable.Range(101, 8).Select(i => P(i)).ToList();
        stage.EnsureDefaults("b", other, doubles: true);
        Assert.Equal("b", stage.DataSetKey);
        Assert.All(Ids(stage), id => Assert.InRange(id, 101, 108));
        Assert.Null(stage.PickedMine2);
    }

    [Fact]
    public void Show_records_the_second_pokemon_and_moves_the_stage()
    {
        var stage = NewStage();
        var pokemon = Many(8);
        stage.EnsureDefaults("set", pokemon, doubles: true);
        stage.Show("set", pokemon[0], pokemon[1], null, null, pokemon[2], pokemon[3]);
        Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(stage));
        Assert.Equal(3, stage.PickedMine2!.Id);
        Assert.Equal(4, stage.PickedFoe2!.Id);
    }

    [Fact]
    public void Show_from_a_singles_page_keeps_the_second_pair()
    {
        var stage = NewStage();
        var pokemon = Many(8);
        stage.EnsureDefaults("set", pokemon, doubles: true);
        stage.Show("set", pokemon[0], pokemon[1], null, null, pokemon[2], pokemon[3]);
        stage.Show("set", pokemon[4], pokemon[5]);   // 2 体目を渡さないページ（ダメージ計算機）
        Assert.Equal(new[] { 5, 6, 3, 4 }, Ids(stage));
    }

    [Fact]
    public void Show_for_another_data_set_is_ignored()
    {
        var stage = NewStage();
        var pokemon = Many(8);
        stage.EnsureDefaults("set", pokemon, doubles: true);
        var before = Ids(stage);
        stage.Show("other", pokemon[6], pokemon[7], null, null, pokemon[5], pokemon[4]);
        Assert.Equal(before, Ids(stage));
    }
}
