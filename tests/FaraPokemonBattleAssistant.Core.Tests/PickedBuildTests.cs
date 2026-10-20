using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>計算機・素早さ比較の「登録から選ぶ」で作る構成（2 つの画面で同じ規則になるよう、Core にまとめたもの）。</summary>
public class PickedBuildTests
{
    private static RosterChoice Choice(int pokemonId, string name, Item? item = null) =>
        new("r1", new RegisteredPokemon { Id = 1, PokemonId = pokemonId, PokemonName = name, Nature = "ようき", EVs = new StatSet { Attack = 32 } }, item);

    [Fact]
    public async Task Choices_are_found_by_key_and_mega_forms_listed()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var charizard = data.Pokemon.First(p => p.Name == "リザードン");
        var choice = Choice(charizard.Id, charizard.Name);
        var groups = new[] { new RosterChoiceGroup("チーム1", new[] { choice }) };
        Assert.Same(choice, PickedBuild.FindChoice(groups, "r1"));
        Assert.Null(PickedBuild.FindChoice(groups, "r9"));
        Assert.Null(PickedBuild.FindChoice(groups, null));
        Assert.Equal(new[] { "メガリザードンX", "メガリザードンY" }, PickedBuild.MegaFormsOf(data, groups, "r1").Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(PickedBuild.MegaFormsOf(data, groups, "r9"));
    }

    [Fact]
    public async Task Mega_switch_carries_the_current_input_and_switching_back_restores_the_registered_item()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var charizard = data.Pokemon.First(p => p.Name == "リザードン");
        var megaX = data.Pokemon.First(p => p.Name == "メガリザードンX");
        var scarf = data.Items.First(i => i.Id == 264);
        var choice = Choice(charizard.Id, charizard.Name, scarf);

        var normal = PickedBuild.FromChoice(choice, null, null, data)!;
        Assert.Equal(scarf, normal.Item);
        normal.Boosts = new StatSet { Attack = 2 };
        normal.Status = StatusCondition.Burn;

        var mega = PickedBuild.FromChoice(choice, megaX, normal, data)!;
        Assert.Equal(megaX, mega.Pokemon);
        Assert.Equal(megaX.MegaStoneId, mega.Item?.Id);        // メガシンカ後はメガストーン
        Assert.Equal(2, mega.Boosts.Attack);                   // いまの入力は引き継ぐ
        Assert.Equal(StatusCondition.Burn, mega.Status);

        var back = PickedBuild.FromChoice(choice, null, mega, data)!;
        Assert.Equal(charizard, back.Pokemon);
        Assert.Equal(scarf, back.Item);                        // 戻すと、登録（チーム）の持ち物
        Assert.Equal(2, back.Boosts.Attack);
        Assert.Equal(StatusCondition.Burn, back.Status);
        Assert.Equal("ようき", choice.Entry.Nature);            // 登録データは変わらない
        Assert.Equal(32, choice.Entry.EVs.Attack);
        Assert.Equal(charizard.Id, choice.Entry.PokemonId);

        // 引き継ぎ無しでメガシンカの姿を選ぶ（別のページから戻ったとき）
        var fresh = PickedBuild.FromChoice(choice, megaX, null, data)!;
        Assert.Equal(megaX, fresh.Pokemon);
        Assert.Equal(megaX.MegaStoneId, fresh.Item?.Id);
        Assert.Equal(0, fresh.Boosts.Attack);
    }

    [Fact]
    public async Task Base_ogerpon_gets_the_teal_mask_and_mask_forms_are_fixed()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var hearthflameMask = data.Items.First(i => i.Id == 2107);
        // 通常のオーガポンに、ほかのすがたのお面が登録されていても、みどりのめんにする
        var baseBuild = PickedBuild.FromChoice(Choice(1017, "オーガポン", hearthflameMask), null, null, data)!;
        Assert.Equal(2102, baseBuild.Item?.Id);
        // お面のすがたは、そのお面で固定
        var wellspring = PickedBuild.FromChoice(Choice(10273, "オーガポン(いどのめん)", hearthflameMask), null, null, data)!;
        Assert.Equal(2106, wellspring.Item?.Id);
        // 特性が 1 つのポケモンは、その特性で固定
        var single = data.Pokemon.First(p => data.AbilitiesOf(p).Count == 1);
        var other = data.Abilities.First(a => !data.AbilitiesOf(single).Contains(a));
        var entry = Choice(single.Id, single.Name).Entry;
        entry.AbilityId = other.Id;   // 特性の一覧にない特性が登録されていても、固定の特性にする
        var build = PickedBuild.FromChoice(new RosterChoice("r1", entry, null), null, null, data)!;
        Assert.Equal(data.AbilitiesOf(single)[0], build.Ability);
    }

    [Fact]
    public async Task Unknown_pokemon_gives_null()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        Assert.Null(PickedBuild.FromChoice(Choice(99999999, "いない"), null, null, data));
    }
}
