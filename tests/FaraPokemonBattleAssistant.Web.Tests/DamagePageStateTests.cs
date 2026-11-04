using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Web.Components.Pages;
using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// ダメージ計算機の入力の状態（ダブルバトルは、自分側・相手側それぞれ 2 体を持ち、選んだ 1 体だけを入力欄に出す）と、
/// 素早さ比較の状態（4 体）、ダブルの結果の対戦画面の内容。
/// </summary>
public class DamagePageStateTests
{
    private static PokemonBuild B(int id) => new(new Pokemon { Id = id, Name = $"P{id}" }, EvSystem.Classic);
    private static Move M(int id) => new() { Id = id, Name = $"M{id}" };

    [Fact]
    public void Attacker_properties_point_at_the_selected_slot()
    {
        var s = new DamageCalculator.State();
        var first = B(1);
        var second = B(2);
        s.Attacker = first;                 // 1 体目（既定）
        s.AttackerIndex = 1;
        Assert.Null(s.Attacker);            // 2 体目はまだ空
        s.Attacker = second;
        Assert.Same(first, s.Attackers[0]);
        Assert.Same(second, s.Attackers[1]);
        s.AttackerIndex = 0;
        Assert.Same(first, s.Attacker);
    }

    [Fact]
    public void Defender_slot_is_independent_of_the_attacker_slot()
    {
        var s = new DamageCalculator.State();
        s.Attackers[0] = B(1);
        s.Attackers[1] = B(2);
        s.Defenders[0] = B(10);
        s.Defenders[1] = B(20);
        s.AttackerIndex = 1;
        s.DefenderIndex = 0;
        Assert.Equal(2, s.Attacker!.Pokemon.Id);
        Assert.Equal(10, s.Defender!.Pokemon.Id);
        s.DefenderIndex = 1;
        Assert.Equal(2, s.Attacker!.Pokemon.Id);
        Assert.Equal(20, s.Defender!.Pokemon.Id);
    }

    [Fact]
    public void Move_and_zmove_belong_to_each_attacker()
    {
        var s = new DamageCalculator.State();
        s.Move = M(1);
        s.IsZMove = true;
        s.AttackerIndex = 1;
        Assert.Null(s.Move);
        Assert.False(s.IsZMove);
        s.Move = M(2);
        Assert.Equal(new[] { 1, 2 }, s.Moves.Select(m => m!.Id));
        Assert.Equal(new[] { true, false }, s.ZMoves);
        s.AttackerIndex = 0;
        Assert.Equal(1, s.Move!.Id);
        Assert.True(s.IsZMove);
    }

    [Fact]
    public void Roster_choices_are_kept_per_slot()
    {
        var s = new DamageCalculator.State();
        s.UseRosterAttacker = true;
        s.AttackerRosterKey = "r1";
        s.UseRosterDefender = true;
        s.DefenderRosterKey = "r9";
        s.AttackerIndex = 1;
        s.DefenderIndex = 1;
        Assert.False(s.UseRosterAttacker);
        Assert.Null(s.AttackerRosterKey);
        Assert.False(s.UseRosterDefender);
        Assert.Null(s.DefenderRosterKey);
        s.AttackerRosterKey = "r2";
        Assert.Equal(new[] { "r1", "r2" }, s.AttackerRosterKeys);
        Assert.Equal("r9", s.DefenderRosterKeys[0]);
    }

    [Fact]
    public void ResetSelections_clears_both_slots_and_returns_to_the_first()
    {
        var s = new DamageCalculator.State();
        s.Attackers[0] = B(1);
        s.Attackers[1] = B(2);
        s.Defenders[1] = B(3);
        s.Moves[1] = M(5);
        s.ZMoves[1] = true;
        s.UseRosterAttackers[1] = true;
        s.AttackerRosterKeys[1] = "r1";
        s.AttackerIndex = 1;
        s.DefenderIndex = 1;
        s.ResetSelections();
        Assert.All(s.Attackers, x => Assert.Null(x));
        Assert.All(s.Defenders, x => Assert.Null(x));
        Assert.All(s.Moves, x => Assert.Null(x));
        Assert.All(s.AttackerRosterKeys, x => Assert.Null(x));
        // 「登録チームから選ぶ」と Z 技のスイッチは、ゲームを切り替えても（これまでどおり）残る
        Assert.True(s.ZMoves[1]);
        Assert.True(s.UseRosterAttackers[1]);
        Assert.Equal(0, s.AttackerIndex);
        Assert.Equal(0, s.DefenderIndex);
    }

    [Fact]
    public void Fresh_state_has_two_slots_per_side()
    {
        var s = new DamageCalculator.State();
        Assert.Equal(2, s.Attackers.Length);
        Assert.Equal(2, s.Defenders.Length);
        Assert.Equal(2, s.Moves.Length);
        Assert.Equal(0, s.AttackerIndex);
        Assert.Equal(0, s.DefenderIndex);
    }

    [Fact]
    public void Speed_state_holds_four_pokemon_and_starts_on_the_first_of_each_side()
    {
        var s = new Speed.State();
        Assert.Equal(4, s.Builds.Length);
        Assert.Equal(4, s.Tailwind.Length);
        Assert.Equal(4, s.UseRoster.Length);
        Assert.Equal(4, s.RosterKey.Length);
        Assert.Equal(4, s.RosterMega.Length);
        Assert.Equal(0, s.MineIndex);
        Assert.Equal(0, s.FoeIndex);
    }

    private static DamageScene Scene(string attacker, string defender, int min, int max) =>
        new(attacker, 100, 100, defender, 100, 100, min, max, false, "わざ", "ノーマル", $"{min}-{max}", "確定2発", true, "", null, null, false);

    [Fact]
    public void DamageScene_keeps_the_slot_numbers_so_two_of_the_same_species_stay_separate()
    {
        var first = Scene("A", "X", 10, 20) with { AttackerSlot = 0, DefenderSlot = 0 };
        var second = Scene("A", "X", 10, 20) with { AttackerSlot = 1, DefenderSlot = 1 };
        Assert.NotEqual(first, second);   // 同じ名前・同じダメージでも、体が違えば別
        Assert.Equal(new[] { 0, 1 }, new[] { first, second }.Select(x => x.DefenderSlot));
    }

    [Fact]
    public void DamagePairScene_equals_by_content_so_the_stage_is_not_redrawn()
    {
        var a = new DamagePairScene(new[] { Scene("A", "X", 10, 20), Scene("B", "Y", 30, 40) });
        var same = new DamagePairScene(new[] { Scene("A", "X", 10, 20), Scene("B", "Y", 30, 40) });
        var other = new DamagePairScene(new[] { Scene("A", "X", 10, 21), Scene("B", "Y", 30, 40) });
        Assert.Equal(a, same);
        Assert.NotEqual(a, other);
        Assert.NotEqual(a, new DamagePairScene(new[] { Scene("A", "X", 10, 20) }));
    }

    [Fact]
    public void SpeedOrderScene_equals_by_content()
    {
        var lines = new[] { new SpeedOrderLine("A", 100, true, 1, false), new SpeedOrderLine("B", 90, false, 2, false) };
        var a = new SpeedOrderScene(lines, "A → B", false);
        var same = new SpeedOrderScene(new[] { new SpeedOrderLine("A", 100, true, 1, false), new SpeedOrderLine("B", 90, false, 2, false) }, "A → B", false);
        Assert.Equal(a, same);
        Assert.NotEqual(a, new SpeedOrderScene(lines, "A → B", true));
        Assert.NotEqual(a, new SpeedOrderScene(lines.Take(1).ToArray(), "A", false));
    }
}
