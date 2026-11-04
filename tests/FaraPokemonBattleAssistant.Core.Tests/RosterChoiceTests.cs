using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>計算機・素早さ比較の「登録チームから選ぶ」（そのバトル形式のチームごとのグループ。使用中が先頭。チームに入っていない登録は含めない）。</summary>
public class RosterChoiceTests
{
    [Fact]
    public async Task Choices_are_grouped_by_team_active_first_with_team_items_and_exclude_unassigned()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var repo = new RosterRepository(new MemoryRosterStore());
        var nature = data.Natures.First();
        var garchomp = await repo.AddPokemonAsync("Gen9", data.FindPokemon(445)!, new StatSet { Attack = 252, Speed = 252 }, nature, null);
        var charizard = await repo.AddPokemonAsync("Gen9", data.FindPokemon(6)!, new StatSet { SpAttack = 252 }, nature, null);
        var dragonite = await repo.AddPokemonAsync("Gen9", data.FindPokemon(149)!, new StatSet { HP = 252 }, nature, null);
        var scarf = data.Items.First(i => i.Id == 264);
        await repo.AddToTeamAsync("Gen9", BattleFormat.Singles, 2, garchomp.Id, scarf);
        await repo.AddToTeamAsync("Gen9", BattleFormat.Singles, 1, charizard.Id, null);
        await repo.UseTeamAsync("Gen9", BattleFormat.Singles, 2);

        var groups = await repo.ChoiceGroupsAsync("Gen9", data, BattleFormat.Singles);
        Assert.Equal(new[] { "チーム2（使用中）", "チーム1" }, groups.Select(g => g.Label));
        var team2 = groups[0].Choices.Single();
        Assert.Equal(garchomp.Id, team2.Entry.Id);
        Assert.Equal(scarf, team2.Item);   // チームで持たせた持ち物
        Assert.Contains("こだわりスカーフ", team2.Label);
        // どのチームにも入っていない育成ポケモン（dragonite）は出さない
        Assert.DoesNotContain(groups.SelectMany(g => g.Choices), c => c.Entry.Id == dragonite.Id);
        // 別のゲームの登録は混ざらない
        Assert.Empty(await repo.ChoiceGroupsAsync("Gen8", data, BattleFormat.Singles));
    }

    [Fact]
    public async Task Changing_a_picked_build_does_not_change_the_registration()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var repo = new RosterRepository(new MemoryRosterStore());
        var entry = await repo.AddPokemonAsync("Gen9", data.FindPokemon(445)!, new StatSet { Attack = 252, Speed = 252 }, data.Natures.First(), null);
        await repo.AddToTeamAsync("Gen9", BattleFormat.Singles, 1, entry.Id, null);
        var choice = (await repo.ChoiceGroupsAsync("Gen9", data, BattleFormat.Singles)).Single().Choices.Single();
        var build = RosterRepository.ToBuild(choice.Entry, data, choice.Item)!;
        Assert.Equal(252, build.EVs.Attack);
        build.EVs = new StatSet { HP = 252 };
        build.Nature = data.Natures.Last();
        build.Item = data.Items.First();
        var stored = (await repo.GetAsync()).For("Gen9").Pokemon.Single();
        Assert.Equal(252, stored.Attack);
        Assert.Equal(0, stored.HP);
        Assert.Equal(entry.Nature, stored.Nature);
        Assert.Null(stored.ItemId);
    }

    [Fact]
    public async Task Registered_pokemon_can_be_switched_to_its_mega_forms()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var charizard = data.Pokemon.First(p => p.Name == "リザードン");
        var forms = RosterRepository.MegaFormsOf(data, charizard);
        Assert.Equal(new[] { "メガリザードンX", "メガリザードンY" }, forms.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(RosterRepository.MegaFormsOf(data, forms[0]));   // メガシンカ後の姿からは、さらに切り替えない

        var nature = data.Natures.First();
        var build = new PokemonBuild(charizard, data.Info.EvSystem) { EVs = new StatSet { SpAttack = 32 }, Nature = nature, Item = data.Items.First() };
        var mega = RosterRepository.ToMegaBuild(build, forms[0], data);
        Assert.Equal(forms[0], mega.Pokemon);
        Assert.Equal(32, mega.EVs.SpAttack);
        Assert.NotSame(build.EVs, mega.EVs);
        Assert.Equal(nature, mega.Nature);
        Assert.Equal(forms[0].MegaStoneId, mega.Item?.Id);
        Assert.Equal(charizard, build.Pokemon);   // 元の構成は変わらない

        // 切り替えても、ランク補正などは引き継ぐ。メガシンカ前に戻すと、持ち物・特性は登録のもの
        mega.Boosts = new StatSet { Attack = 2 };
        mega.Status = StatusCondition.Burn;
        var entry = new RegisteredPokemon { PokemonId = charizard.Id, PokemonName = charizard.Name, Nature = nature.Name };
        var back = RosterRepository.ToBaseBuild(mega, entry, data, null);
        Assert.Equal(charizard, back.Pokemon);
        Assert.Equal(2, back.Boosts.Attack);
        Assert.Equal(StatusCondition.Burn, back.Status);
        Assert.Null(back.Item);
    }

    [Fact]
    public async Task Items_and_abilities_have_descriptions()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        Assert.NotEmpty(data.Items.First(i => i.Name == "こだわりスカーフ").Description);
        Assert.NotEmpty(data.Abilities.First(a => a.Name == "いかく").Description);
    }

    [Fact]
    public async Task Type_boost_items_are_1_1x_before_gen4_and_describe_it()
    {
        var gen3 = (await TestData.Catalog().GetDataSetAsync("Gen3")).Items.First(i => i.Name == "ぎんのこな");
        Assert.Equal(1.1, gen3.TypeBoostMultiplier);
        Assert.Equal("むしタイプの技の威力が1.1倍になる。", gen3.Description);
        var gen9 = (await TestData.Catalog().GetDataSetAsync("Gen9")).Items.First(i => i.Name == "ぎんのこな");
        Assert.Equal(1.2, gen9.TypeBoostMultiplier);
        Assert.Equal("むしタイプの技の威力が1.2倍になる。", gen9.Description);
    }

    [Theory]
    [InlineData(10273, 2106, "Water")]
    [InlineData(10274, 2107, "Fire")]
    [InlineData(10275, 2108, "Rock")]
    public async Task Ogerpon_mask_boosts_moves_only_for_its_own_form_and_changes_ivy_cudgel_type(int formId, int maskId, string type)
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var calc = new FaraPokemonBattleAssistant.Core.Battle.DamageCalculator(data.TypeChart);
        var form = data.Pokemon.First(p => p.Id == formId);
        var ownMask = data.Items.First(i => i.Id == maskId);
        var otherMask = data.Items.First(i => i.Id != maskId && i.Id is 2106 or 2107 or 2108);
        var ivy = data.Moves.First(m => m.Id == 904);
        var target = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem) { EVs = new StatSet { HP = 32 } };
        PokemonBuild Make(Pokemon pokemon, Item? item) => new(pokemon, data.Info.EvSystem) { EVs = new StatSet { Attack = 32 }, Nature = data.Natures.First(), Item = item };

        var plain = calc.Calculate(new DamageRequest { Attacker = Make(form, null), Move = ivy, Defender = target });
        var masked = calc.Calculate(new DamageRequest { Attacker = Make(form, ownMask), Move = ivy, Defender = target });
        var wrongMask = calc.Calculate(new DamageRequest { Attacker = Make(form, otherMask), Move = ivy, Defender = target });
        Assert.Equal(type, masked.MoveType);                 // ツタこんぼうは、お面のタイプ
        Assert.Contains(masked.Modifiers, m => m.Contains("1.2倍") && m.Contains(ownMask.Name));
        Assert.True(masked.MaxDamage > plain.MaxDamage);     // 自分のお面なら威力が上がる
        Assert.Equal(plain.MaxDamage, wrongMask.MaxDamage);  // 別のすがたのお面では上がらない
        // 通常のオーガポン（みどりのめん）は Grass のまま、お面の補正もつかない
        var teal = calc.Calculate(new DamageRequest { Attacker = Make(data.Pokemon.First(p => p.Id == 1017), ownMask), Move = ivy, Defender = target });
        Assert.Equal("Grass", teal.MoveType);
    }

    [Theory]
    [InlineData(1017, Stat.Speed, "Grass")]
    [InlineData(10273, Stat.SpDefense, "Water")]
    [InlineData(10274, Stat.Attack, "Fire")]
    [InlineData(10275, Stat.Defense, "Rock")]
    public async Task Terastallized_ogerpon_gets_embody_aspect_and_a_fixed_tera_type(int formId, Stat boosted, string type)
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var form = data.Pokemon.First(p => p.Id == formId);
        var build = new PokemonBuild(form, data.Info.EvSystem) { Ability = data.AbilitiesOf(form).First(), TeraType = "Dragon" };
        var tera = FaraPokemonBattleAssistant.Core.Battle.MoveEffects.AdjustOgerponTerastal(build, out var note);
        Assert.Equal(type, tera.TeraType);                      // テラスタイプはお面のタイプに固定
        Assert.Equal("embody-aspect", tera.Ability!.Identifier);  // 特性は おもかげやどし（かたやぶりなどは消える）
        Assert.Equal(1, tera.Boosts[boosted]);
        Assert.NotNull(note);
        Assert.Equal("Dragon", build.TeraType);                 // 元の構成は変わらない
        Assert.Equal(0, build.Boosts[boosted]);

        build.TeraType = null;   // テラスタルしていなければ、そのまま
        Assert.Same(build, FaraPokemonBattleAssistant.Core.Battle.MoveEffects.AdjustOgerponTerastal(build, out _));
        var other = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem) { TeraType = "Fire" };
        Assert.Same(other, FaraPokemonBattleAssistant.Core.Battle.MoveEffects.AdjustOgerponTerastal(other, out _));
    }

    [Fact]
    public async Task Teal_mask_exists_and_boosts_base_ogerpon()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var teal = data.Items.First(i => i.Id == 2102);
        Assert.Equal("みどりのめん", teal.Name);
        var calc = new FaraPokemonBattleAssistant.Core.Battle.DamageCalculator(data.TypeChart);
        var ogerpon = data.Pokemon.First(p => p.Id == 1017);
        var ivy = data.Moves.First(m => m.Id == 904);
        var target = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem) { EVs = new StatSet { HP = 32 } };
        PokemonBuild Make(Item? item) => new(ogerpon, data.Info.EvSystem) { EVs = new StatSet { Attack = 32 }, Nature = data.Natures.First(), Item = item };
        var plain = calc.Calculate(new DamageRequest { Attacker = Make(null), Move = ivy, Defender = target });
        var masked = calc.Calculate(new DamageRequest { Attacker = Make(teal), Move = ivy, Defender = target });
        Assert.True(masked.MaxDamage > plain.MaxDamage);
    }

    [Fact]
    public async Task Terastallized_wellspring_ogerpon_loses_water_absorb_in_the_calculation()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var calc = new FaraPokemonBattleAssistant.Core.Battle.DamageCalculator(data.TypeChart);
        var wellspring = data.Pokemon.First(p => p.Id == 10273);
        var surf = data.Moves.First(m => m.Name == "なみのり");
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem) { EVs = new StatSet { SpAttack = 32 } };
        PokemonBuild Defender(string? tera) => new(wellspring, data.Info.EvSystem)
        {
            Ability = data.AbilitiesOf(wellspring).First(a => a.Identifier == "water-absorb"),
            TeraType = tera,
        };
        var normal = calc.Calculate(new DamageRequest { Attacker = attacker, Move = surf, Defender = Defender(null) });
        var tera = calc.Calculate(new DamageRequest { Attacker = attacker, Move = surf, Defender = Defender("Water") });
        Assert.Equal(0, normal.MaxDamage);       // ちょすいで無効
        Assert.True(tera.MaxDamage > 0);         // テラスタルで おもかげやどし になり、ちょすいは消える
        Assert.Contains(tera.Modifiers, m => m.Contains("おもかげやどし"));
    }

    [Fact]
    public async Task Ogerpon_mask_forms_hold_their_mask_but_base_ogerpon_is_free()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        Assert.Equal(2106, data.Pokemon.First(p => p.Id == 10273).FixedItemId);
        Assert.Equal(2107, data.Pokemon.First(p => p.Id == 10274).FixedItemId);
        Assert.Equal(2108, data.Pokemon.First(p => p.Id == 10275).FixedItemId);
        Assert.Null(data.Pokemon.First(p => p.Id == 1017).FixedItemId);   // 通常のオーガポンは持ち物自由
        var champions = await TestData.Catalog().GetDataSetAsync("Champions");
        var megaX = champions.Pokemon.First(p => p.Name == "メガリザードンX");
        Assert.Equal(megaX.MegaStoneId, megaX.FixedItemId);   // メガストーンの固定は今まで通り
        // 通常のオーガポンは、使用率の持ち物がほかのすがたのお面でも、みどりのめんになる
        var base1017 = data.Pokemon.First(p => p.Id == 1017);
        Assert.Equal(2102, MoveEffects.NormalizeOgerponItem(base1017, data.FindItem(2107), data)?.Id);
        Assert.Equal(2107, MoveEffects.NormalizeOgerponItem(data.Pokemon.First(p => p.Id == 10274), data.FindItem(2107), data)?.Id);
        // 登録の構成も、お面のすがたならそのお面を持つ
        var entry = new RegisteredPokemon { PokemonId = 10274, PokemonName = "オーガポン(かまどのめん)", Nature = data.Natures.First().Name };
        Assert.Equal(2107, RosterRepository.ToBuild(entry, data)!.Item?.Id);
    }

    [Fact]
    public async Task Terastal_base_power_floor_does_not_apply_to_each_hit_of_multi_hit_moves()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var calc = new FaraPokemonBattleAssistant.Core.Battle.DamageCalculator(data.TypeChart);
        var attackerPokemon = data.Pokemon.First(p => p.Name == "ウーラオス(れんげきのかた)");
        var surging = data.Moves.First(m => m.Id == 818);   // すいりゅうれんだ（威力 25 × 3 回）
        var target = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem) { EVs = new StatSet { HP = 32 } };
        PokemonBuild Make(string? tera) => new(attackerPokemon, data.Info.EvSystem) { EVs = new StatSet { Attack = 32 }, Nature = data.Natures.First(), TeraType = tera };
        var plain = calc.Calculate(new DamageRequest { Attacker = Make(null), Move = surging, Defender = target });
        var tera = calc.Calculate(new DamageRequest { Attacker = Make("Water"), Move = surging, Defender = target });
        // テラスタルの一致補正（1.5 倍→2 倍）だけで、威力 60 保証（約 2.4 倍）は掛からない
        Assert.DoesNotContain(tera.Modifiers, m => m.Contains("威力60保証"));
        Assert.True(tera.MaxDamage < plain.MaxDamage * 2.0, $"{tera.MaxDamage} vs {plain.MaxDamage}");
    }

    [Theory]
    [InlineData("Gen6", 1.5)]
    [InlineData("Gen7", 1.25)]
    public async Task Parental_bond_works_in_gen6_and_gen7(string key, double expectedRatio)
    {
        var data = await TestData.Catalog().GetDataSetAsync(key);
        var calc = new FaraPokemonBattleAssistant.Core.Battle.DamageCalculator(data.TypeChart, key == "Gen6" ? 6 : 7);
        var mega = data.Pokemon.First(p => p.Name == "メガガルーラ");
        var parental = data.AbilitiesOf(mega).First(a => a.Identifier == "parental-bond");
        var move = data.Moves.First(m => m.Name == "すてみタックル");
        var target = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem) { EVs = new StatSet { HP = 32 } };
        PokemonBuild Make(Ability? ability) => new(mega, data.Info.EvSystem) { EVs = new StatSet { Attack = 32 }, Nature = data.Natures.First(), Ability = ability };
        var without = calc.Calculate(new DamageRequest { Attacker = Make(null), Move = move, Defender = target });
        var with = calc.Calculate(new DamageRequest { Attacker = Make(parental), Move = move, Defender = target });
        Assert.Contains(with.Modifiers, m => m.Contains("おやこあい"));
        Assert.InRange(with.MaxDamage / (double)without.MaxDamage, expectedRatio - 0.05, expectedRatio + 0.05);
    }

    [Fact]
    public void Max_move_power_has_exceptions_for_multi_hit_moves()
    {
        var bulletSeed = new Move { Id = 331, Name = "タネマシンガン", Type = "Grass", Power = 25, Category = MoveCategory.Physical };
        Assert.Equal(130, FaraPokemonBattleAssistant.Core.Battle.DamageCalculator.MaxMovePower(bulletSeed));   // 表なら 90
        var tackle = new Move { Id = 33, Name = "たいあたり", Type = "Normal", Power = 40, Category = MoveCategory.Physical };
        Assert.Equal(90, FaraPokemonBattleAssistant.Core.Battle.DamageCalculator.MaxMovePower(tackle));
    }
}
