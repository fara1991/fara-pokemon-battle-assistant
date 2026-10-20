using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Roster;

/// <summary>
/// 計算機・素早さ比較の「登録から選ぶ」で、選んだ登録から画面に入れる構成を作る処理。
/// 2 つの画面で同じ規則（メガシンカの前後・お面・固定の持ち物と特性）になるよう、ここにまとめる。
/// </summary>
public static class PickedBuild
{
    /// <summary>選んだ登録（<paramref name="key"/>）。無ければ null。</summary>
    public static RosterChoice? FindChoice(IReadOnlyList<RosterChoiceGroup> groups, string? key) =>
        key is null ? null : groups.SelectMany(g => g.Choices).FirstOrDefault(c => c.Key == key);

    /// <summary>選んだ登録のポケモンの、メガシンカ後の姿（メガシンカしないポケモンなら空）。</summary>
    public static IReadOnlyList<Pokemon> MegaFormsOf(PokemonDataSet data, IReadOnlyList<RosterChoiceGroup> groups, string? key) =>
        FindChoice(groups, key) is { } choice && data.FindPokemon(choice.Entry.PokemonId) is { } pokemon
            ? RosterRepository.MegaFormsOf(data, pokemon) : Array.Empty<Pokemon>();

    /// <summary>
    /// 登録から選んだ構成を作る。<paramref name="mega"/> があればメガシンカ後の姿。
    /// <paramref name="current"/> を渡すと、メガシンカの前後の切り替えとして、いまの入力（ランク補正・状態異常・HP など）を引き継ぐ。
    /// 登録データ自体は変えない。データに無いポケモンなら null。
    /// </summary>
    public static PokemonBuild? FromChoice(RosterChoice choice, Pokemon? mega, PokemonBuild? current, PokemonDataSet data)
    {
        var build = RosterRepository.ToBuild(choice.Entry, data, choice.Item);
        if (build is null) return null;
        if (current is not null)
            build = mega is null ? RosterRepository.ToBaseBuild(current, choice.Entry, data, choice.Item) : RosterRepository.ToMegaBuild(current, mega, data);
        else if (mega is not null)
            build = RosterRepository.ToMegaBuild(build, mega, data);
        ApplyFixedChoices(build, data);
        return build;
    }

    /// <summary>メガシンカ後・お面のすがたは持ち物が固定、特性が 1 つなら固定。通常のオーガポンは、ほかのすがたのお面ではなくみどりのめん。</summary>
    public static void ApplyFixedChoices(PokemonBuild build, PokemonDataSet data)
    {
        if (build.Pokemon.FixedItemId is { } fixedItemId)
            build.Item = data.FindItem(fixedItemId) ?? build.Item;
        else
            build.Item = MoveEffects.NormalizeOgerponItem(build.Pokemon, build.Item, data);
        var abilities = data.AbilitiesOf(build.Pokemon);
        if (abilities.Count == 1) build.Ability = abilities[0];
        else if (build.Ability is not null && !abilities.Contains(build.Ability)) build.Ability = abilities.FirstOrDefault();
    }
}
