using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// 第 3〜4 世代のダメージ計算（@smogon/calc の gen3 / gen4 と同じ順番・切り捨て）。
/// 4096 を 1 倍とする補正ではなく、補正ごとに切り捨てる。第 3 世代は物理・特殊が技のタイプで決まり（データ側で反映済み）、
/// タイプ一致がタイプ相性の前・乱数の前に掛かる。
/// </summary>
internal sealed partial class LegacyDamageCalculator
{
    private const int MoveIdFireFang = 424;
    private const int MoveIdExplosion = 153;
    private const int MoveIdSelfDestruct = 120;

    /// <summary>2 タイプのポケモンで、どちらのタイプの相性を先に掛けるか（この順で前のタイプが先）。</summary>
    private static readonly string[] TypePrecedence =
    {
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison", "Ground", "Flying",
        "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark", "Steel",
    };

    /// <summary>第 4 世代のかたやぶりで無視される防御側の特性（この計算で効果を扱うもの）。</summary>
    private static readonly HashSet<string> IgnorableAbilitiesGen4 = new()
    {
        "battle-armor", "shell-armor", "dry-skin", "filter", "flash-fire", "heatproof", "levitate", "lightning-rod",
        "marvel-scale", "motor-drive", "solid-rock", "soundproof", "storm-drain", "thick-fat", "unaware",
        "volt-absorb", "water-absorb", "wonder-guard",
    };

    /// <summary>第 3 世代の物理・特殊（技のタイプで決まる）。</summary>
    private static MoveCategory Gen3Category(string type) =>
        type is "Normal" or "Fighting" or "Flying" or "Poison" or "Ground" or "Rock" or "Bug" or "Ghost" or "Steel"
            ? MoveCategory.Physical
            : MoveCategory.Special;

    /// <summary>第 3・4 世代の計算の途中経過。段階ごとの関数が、順に値を埋めていく。</summary>
    private sealed class Gen34State
    {
        public Gen34State(DamageRequest request)
        {
            Request = request;
            Attacker = request.Attacker;
            Defender = request.Defender;
            Move = request.Move;
            DefenderHp = Defender.MaxHp;
            MoveType = Move.Type;
            Category = Move.Category;
        }

        public DamageRequest Request { get; }
        public PokemonBuild Attacker { get; }
        public PokemonBuild Defender { get; }
        public Move Move { get; }
        public int DefenderHp { get; }
        public List<string> Modifiers { get; } = new();

        public string AtkAbility = "", DefAbility = "", AtkName = "", DefName = "", StatName = "";
        public IReadOnlySet<string> Flags = new HashSet<string>();
        public Item? AttackerItem, DefenderItem;
        public Weather Weather;
        public string MoveType;
        public MoveCategory Category;
        public bool WeatherBallDoubled, Reflect, LightScreen, IsCritical, IsPhysical, Pinch;
        public double Eff1 = 1.0, Eff2 = 1.0, Effectiveness = 1.0;
        public int BasePower, Attack, Defense, BaseDamage;
    }

    private DamageResult CalculateGen34(DamageRequest request)
    {
        var st = new Gen34State(request);
        if (!st.Move.IsDamaging)
            return Empty(st.DefenderHp, 1.0, st.Move.Type, "変化技");

        ResolveAbilitiesMoveTypeScreens(st);
        var blocked = ResolveEffectivenessAndImmunity(st);
        if (blocked is not null) return blocked;
        ResolveCategoryAndPinch(st);
        ResolveBasePowerGen34(st);
        ComputeAttackGen34(st);
        ComputeDefenseGen34(st);
        ComputeBaseDamageGen34(st);
        return BuildRollsGen34(st);
    }

    /// <summary>特性による天候の無効化、第 4 世代のかたやぶり・ノーマルスキン、技タイプ（第 3 世代のウェザーボール）、壁を決める。</summary>
    private void ResolveAbilitiesMoveTypeScreens(Gen34State st)
    {
        st.AtkAbility = st.Attacker.Ability?.Identifier ?? "";
        st.DefAbility = st.Defender.Ability?.Identifier ?? "";
        st.AtkName = st.Attacker.Ability?.Name ?? "";
        st.DefName = st.Defender.Ability?.Name ?? "";
        st.AttackerItem = st.Attacker.Item;
        st.DefenderItem = st.Defender.Item;
        st.Weather = st.Request.Weather;

        if (st.Weather != Weather.None && (st.AtkAbility is "air-lock" or "cloud-nine" || st.DefAbility is "air-lock" or "cloud-nine"))
        {
            st.Modifiers.Add($"{(st.AtkAbility is "air-lock" or "cloud-nine" ? st.AtkName : st.DefName)}(天候の効果なし)");
            st.Weather = Weather.None;
        }

        if (_gen == 4 && st.AtkAbility == "mold-breaker" && IgnorableAbilitiesGen4.Contains(st.DefAbility))
        {
            st.Modifiers.Add($"{st.AtkName}(相手の特性を無視)");
            st.DefAbility = "";
        }

        st.MoveType = st.Move.Type;
        if (_gen == 4 && st.AtkAbility == "normalize")
        {
            st.MoveType = "Normal";
            st.Modifiers.Add($"{st.AtkName}(ノーマルタイプ化)");
        }
        // 第 3 世代のウェザーボール: 天候があるとタイプが変わり（物理・特殊も変わったタイプで決まる）、威力ではなくダメージが 2 倍。
        // 2 倍は急所と同じところ（基礎ダメージに +2 した後、タイプ一致・相性・乱数の前）で掛かる
        st.WeatherBallDoubled = false;
        st.Category = st.Move.Category;
        if (_gen == 3 && st.Move.Id == MoveEffects.MoveIdWeatherBall && MoveEffects.WeatherBallType(st.Weather) is { } weatherBallType)
        {
            st.MoveType = weatherBallType;
            st.Category = Gen3Category(weatherBallType);
            st.WeatherBallDoubled = true;
        }

        st.Reflect = st.Request.Reflect || st.Request.Screen;
        st.LightScreen = st.Request.LightScreen || st.Request.Screen;
        if (st.Move.Id == MoveIdBrickBreak) st.Reflect = st.LightScreen = false;
    }

    /// <summary>相性（2 タイプは決まった順に、1 つずつ切り捨てながら掛ける）と、特性による無効化。無効ならその結果を返す。</summary>
    private DamageResult? ResolveEffectivenessAndImmunity(Gen34State st)
    {
        // --- 相性（2 タイプは決まった順に、1 つずつ切り捨てながら掛ける）
        var types = st.Defender.Pokemon.Types.ToList();
        if (types.Count == 2 && types[0] != types[1] && Array.IndexOf(TypePrecedence, types[0]) > Array.IndexOf(TypePrecedence, types[1]))
            (types[0], types[1]) = (types[1], types[0]);
        double Against(string t)
        {
            var m = _chart.Against(st.MoveType, t);
            if (m == 0 && t == "Ghost" && _gen == 4 && st.AtkAbility == "scrappy" && st.MoveType is "Normal" or "Fighting") m = 1;
            return m;
        }
        st.Eff1 = Against(types.Count > 0 ? types[0] : "");
        st.Eff2 = types.Count > 1 ? Against(types[1]) : 1.0;
        st.Effectiveness = st.Eff1 * st.Eff2;
        if (st.Effectiveness == 0)
            return Empty(st.DefenderHp, 0, st.MoveType, "効果なし");

        st.Flags = st.Move.Flags;
        string? immunity = _gen == 3
            ? st.DefAbility switch
            {
                "flash-fire" when st.MoveType == "Fire" => "ほのお技無効",
                "levitate" when st.MoveType == "Ground" => "じめん技無効",
                "volt-absorb" when st.MoveType == "Electric" => "でんき技無効",
                "water-absorb" when st.MoveType == "Water" => "みず技無効",
                "wonder-guard" when st.Effectiveness <= 1 => "効果抜群以外無効",
                "soundproof" when st.Flags.Contains("Sound") => "音技無効",
                _ => null,
            }
            : st.DefAbility switch
            {
                "wonder-guard" when st.Effectiveness <= 1 && st.Move.Id != MoveIdFireFang => "効果抜群以外無効",
                "flash-fire" when st.MoveType == "Fire" => "ほのお技無効",
                "dry-skin" or "water-absorb" when st.MoveType == "Water" => "みず技無効",
                "motor-drive" or "volt-absorb" when st.MoveType == "Electric" => "でんき技無効",
                "levitate" when st.MoveType == "Ground" => "じめん技無効",
                "soundproof" when st.Flags.Contains("Sound") => "音技無効",
                _ => null,
            };
        if (immunity is not null)
            return Empty(st.DefenderHp, 0, st.MoveType, $"{st.DefName}({immunity})");
        return null;
    }

    /// <summary>急所、物理・特殊、ピンチ時の特性（しんりょく・もうか・げきりゅう・むしのしらせ）を決める。</summary>
    private static void ResolveCategoryAndPinch(Gen34State st)
    {
        st.IsCritical = st.Request.IsCritical && st.DefAbility is not ("battle-armor" or "shell-armor");
        if (st.Request.IsCritical && !st.IsCritical) st.Modifiers.Add($"{st.DefName}(急所に当たらない)");
        st.IsPhysical = st.Category == MoveCategory.Physical;
        st.StatName = st.IsPhysical ? "A" : "C";
        st.Pinch = st.Attacker.CurrentHp * 3 <= st.Attacker.MaxHp && (st.AtkAbility == "overgrow" && st.MoveType == "Grass" || st.AtkAbility == "blaze" && st.MoveType == "Fire"
            || st.AtkAbility == "torrent" && st.MoveType == "Water" || st.AtkAbility == "swarm" && st.MoveType == "Bug");
    }

    /// <summary>威力（からげんき、第 4 世代の特性・持ち物、ピンチ）。</summary>
    private void ResolveBasePowerGen34(Gen34State st)
    {
        // --- 威力
        st.BasePower = st.Move.Power;
        if (st.Move.Id == MoveIdFacade && st.Attacker.Status is StatusCondition.Burn or StatusCondition.Paralysis or StatusCondition.Poison or StatusCondition.BadlyPoisoned)
        {
            st.BasePower *= 2;
            st.Modifiers.Add("からげんき(状態異常で2倍)");
        }
        if (_gen == 4)
        {
            if (st.AtkAbility == "technician" && st.BasePower <= 60) { st.BasePower = st.BasePower * 3 / 2; st.Modifiers.Add($"{st.AtkName}(1.5倍)"); }
            if (IsMuscleBand(st.AttackerItem, st.IsPhysical)) { st.BasePower = (int)Math.Floor(st.BasePower * 1.1); st.Modifiers.Add($"{st.AttackerItem!.Name}(1.1倍)"); }
            else if (IsTypeBoost(st.AttackerItem, st.MoveType)) { st.BasePower = (int)Math.Floor(st.BasePower * 1.2); st.Modifiers.Add($"{st.AttackerItem!.Name}(1.2倍)"); }
            if (st.AtkAbility == "iron-fist" && st.Flags.Contains("Punch") || st.AtkAbility == "reckless" && MoveEffects.IsRecoil(st.Move)) { st.BasePower = (int)Math.Floor(st.BasePower * 1.2); st.Modifiers.Add($"{st.AtkName}(1.2倍)"); }
            else if (st.Pinch) { st.BasePower = st.BasePower * 3 / 2; st.Modifiers.Add($"{st.AtkName}(HP1/3以下 1.5倍)"); }
            if (st.DefAbility == "heatproof" && st.MoveType == "Fire" || st.DefAbility == "thick-fat" && st.MoveType is "Fire" or "Ice")
            {
                st.BasePower /= 2;
                st.Modifiers.Add($"{st.DefName}(0.5倍)");
            }
            else if (st.DefAbility == "dry-skin" && st.MoveType == "Fire") { st.BasePower = (int)Math.Floor(st.BasePower * 1.25); st.Modifiers.Add($"{st.DefName}(1.25倍)"); }
        }
        else if (st.Pinch)
        {
            st.BasePower = st.BasePower * 3 / 2;
            st.Modifiers.Add($"{st.AtkName}(HP1/3以下 1.5倍)");
        }
    }

    /// <summary>攻撃側の実数値。</summary>
    private void ComputeAttackGen34(Gen34State st)
    {
        // --- 攻撃側の実数値
        var attackStatKind = st.IsPhysical ? Stat.Attack : Stat.SpAttack;
        st.Attack = StatCalculator.Calculate(st.Attacker, attackStatKind);
        var attackBoost = st.Attacker.Boosts[attackStatKind];
        var applyAttackBoost = attackBoost > 0 || !st.IsCritical && attackBoost < 0;
        if (_gen == 4)
        {
            if (st.DefAbility == "unaware") { if (attackBoost != 0) st.Modifiers.Add($"{st.DefName}(ランク無視)"); }
            else if (applyAttackBoost) { st.Attack = StatCalculator.ApplyBoost(st.Attack, attackBoost); st.Modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}"); }
            if (st.IsPhysical && st.AtkAbility is "huge-power" or "pure-power") { st.Attack *= 2; st.Modifiers.Add($"{st.AtkName}({st.StatName}2倍)"); }
            else if (!st.IsPhysical && st.AtkAbility == "solar-power" && st.Weather == Weather.Sun) { st.Attack = st.Attack * 3 / 2; st.Modifiers.Add($"{st.AtkName}({st.StatName}1.5倍)"); }
            else if (st.IsPhysical && (st.AtkAbility == "hustle" || st.AtkAbility == "guts" && st.Attacker.Status != StatusCondition.None)) { st.Attack = st.Attack * 3 / 2; st.Modifiers.Add($"{st.AtkName}({st.StatName}1.5倍)"); }
            if (IsChoiceBand(st.AttackerItem) && st.IsPhysical || IsChoiceSpecs(st.AttackerItem) && !st.IsPhysical) { st.Attack = st.Attack * 3 / 2; st.Modifiers.Add($"{st.AttackerItem!.Name}({st.StatName}1.5倍)"); }
            else if (st.AttackerItem is { Category: ItemCategory.LightBall } && st.Attacker.Pokemon.SpeciesId == 25
                || st.AttackerItem is { Category: ItemCategory.ThickClub } && st.IsPhysical && st.Attacker.Pokemon.SpeciesId is 104 or 105)
            {
                st.Attack *= 2;
                st.Modifiers.Add($"{st.AttackerItem.Name}({st.StatName}2倍)");
            }
        }
        else
        {
            if (st.IsPhysical && st.AtkAbility is "huge-power" or "pure-power") { st.Attack *= 2; st.Modifiers.Add($"{st.AtkName}({st.StatName}2倍)"); }
            // 第 3 世代のタイプ強化の持ち物は攻撃・特攻の 1.1 倍
            if (IsTypeBoost(st.AttackerItem, st.MoveType)) { st.Attack = (int)Math.Floor(st.Attack * 1.1); st.Modifiers.Add($"{st.AttackerItem!.Name}({st.StatName}1.1倍)"); }
            else if (IsChoiceBand(st.AttackerItem) && st.IsPhysical) { st.Attack = st.Attack * 3 / 2; st.Modifiers.Add($"{st.AttackerItem!.Name}({st.StatName}1.5倍)"); }
            else if (st.AttackerItem is { Category: ItemCategory.LightBall } && !st.IsPhysical && st.Attacker.Pokemon.SpeciesId == 25
                || st.AttackerItem is { Category: ItemCategory.ThickClub } && st.IsPhysical && st.Attacker.Pokemon.SpeciesId is 104 or 105)
            {
                st.Attack *= 2;
                st.Modifiers.Add($"{st.AttackerItem.Name}({st.StatName}2倍)");
            }
            if (st.DefAbility == "thick-fat" && st.MoveType is "Fire" or "Ice") { st.Attack /= 2; st.Modifiers.Add($"{st.DefName}({st.StatName}0.5倍)"); }
            if (st.IsPhysical && (st.AtkAbility == "hustle" || st.AtkAbility == "guts" && st.Attacker.Status != StatusCondition.None)) { st.Attack = st.Attack * 3 / 2; st.Modifiers.Add($"{st.AtkName}({st.StatName}1.5倍)"); }
            if (applyAttackBoost) { st.Attack = StatCalculator.ApplyBoost(st.Attack, attackBoost); st.Modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}"); }
        }
    }

    /// <summary>防御側の実数値。</summary>
    private void ComputeDefenseGen34(Gen34State st)
    {
        // --- 防御側の実数値
        var defenseStatKind = st.IsPhysical ? Stat.Defense : Stat.SpDefense;
        var defStatName = st.IsPhysical ? "B" : "D";
        st.Defense = StatCalculator.Calculate(st.Defender, defenseStatKind);
        var defenseBoost = st.Defender.Boosts[defenseStatKind];
        var applyDefenseBoost = defenseBoost < 0 || !st.IsCritical && defenseBoost > 0;
        var selfDestruct = st.Move.Id is MoveIdExplosion or MoveIdSelfDestruct;
        if (_gen == 4)
        {
            if (st.AtkAbility == "unaware") { if (defenseBoost != 0) st.Modifiers.Add($"{st.AtkName}(ランク無視)"); }
            else if (applyDefenseBoost) { st.Defense = StatCalculator.ApplyBoost(st.Defense, defenseBoost); st.Modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}"); }
            if (st.DefAbility == "marvel-scale" && st.Defender.Status != StatusCondition.None && st.IsPhysical) { st.Defense = st.Defense * 3 / 2; st.Modifiers.Add($"{st.DefName}({defStatName}1.5倍)"); }
            if (st.Weather == Weather.Sand && st.Defender.Pokemon.HasType("Rock") && !st.IsPhysical) { st.Defense = st.Defense * 3 / 2; st.Modifiers.Add("砂嵐(いわタイプのD1.5倍)"); }
            if (selfDestruct) { st.Defense /= 2; st.Modifiers.Add($"{st.Move.Name}(相手のB半分)"); }
            st.Defense = Math.Max(1, st.Defense);
        }
        else
        {
            if (st.DefAbility == "marvel-scale" && st.Defender.Status != StatusCondition.None && st.IsPhysical) { st.Defense = st.Defense * 3 / 2; st.Modifiers.Add($"{st.DefName}({defStatName}1.5倍)"); }
            if (selfDestruct) { st.Defense /= 2; st.Modifiers.Add($"{st.Move.Name}(相手のB半分)"); }
            if (applyDefenseBoost) { st.Defense = StatCalculator.ApplyBoost(st.Defense, defenseBoost); st.Modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}"); }
        }
    }

    /// <summary>基礎ダメージ（やけど・壁・複数対象・天候・急所・ウェザーボールの 2 倍まで）。</summary>
    private void ComputeBaseDamageGen34(Gen34State st)
    {
        // --- 基礎ダメージと補正
        var levelTerm = st.Attacker.Level * 2 / 5 + 2;
        var burn = st.Attacker.Status == StatusCondition.Burn && st.IsPhysical && st.AtkAbility != "guts";
        var doubles = st.Request.Format == BattleFormat.Doubles;
        if (_gen == 4)
        {
            st.BaseDamage = levelTerm * st.BasePower * st.Attack / 50 / st.Defense;
            if (burn) { st.BaseDamage /= 2; st.Modifiers.Add("やけど(物理0.5倍)"); }
        }
        else
        {
            st.BaseDamage = levelTerm * st.Attack * st.BasePower / st.Defense / 50;
            if (burn) { st.BaseDamage /= 2; st.Modifiers.Add("やけど(物理0.5倍)"); }
        }
        if (!st.IsCritical && (st.IsPhysical && st.Reflect || !st.IsPhysical && st.LightScreen))
        {
            var wall = st.IsPhysical ? "リフレクター" : "ひかりのかべ";
            st.BaseDamage = (int)Math.Floor(st.BaseDamage * (doubles ? 2.0 / 3 : 0.5));
            st.Modifiers.Add(doubles ? $"{wall}(ダブル 0.67倍)" : $"{wall}(0.5倍)");
        }
        if (doubles && (_gen == 4 ? st.Move.Target != MoveTarget.Single : st.Move.Target == MoveTarget.AllFoes))
        {
            st.BaseDamage = _gen == 4 ? st.BaseDamage * 3 / 4 : st.BaseDamage / 2;
            st.Modifiers.Add(_gen == 4 ? "複数対象(0.75倍)" : "複数対象(0.5倍)");
        }
        if (st.Weather == Weather.Sun && st.MoveType == "Fire" || st.Weather == Weather.Rain && st.MoveType == "Water")
        {
            st.BaseDamage = st.BaseDamage * 3 / 2;
            st.Modifiers.Add($"{FieldNames.Japanese(st.Weather, _gen)}(1.5倍)");
        }
        else if (st.Weather == Weather.Sun && st.MoveType == "Water" || st.Weather == Weather.Rain && st.MoveType == "Fire"
            || st.Move.Id == MoveIdSolarBeam && st.Weather is Weather.Rain or Weather.Sand or Weather.Snow)
        {
            st.BaseDamage /= 2;
            st.Modifiers.Add($"{FieldNames.Japanese(st.Weather, _gen)}(0.5倍)");
        }
        st.BaseDamage = (_gen == 3 && st.IsPhysical ? Math.Max(1, st.BaseDamage) : st.BaseDamage) + 2;
        if (st.IsCritical)
        {
            var crit = _gen == 4 && st.AtkAbility == "sniper" ? 3 : 2;
            st.BaseDamage *= crit;
            st.Modifiers.Add($"急所({crit}倍)");
        }
        if (st.WeatherBallDoubled)
        {
            st.BaseDamage *= 2;
            st.Modifiers.Add($"{st.Move.Name}({FieldNames.Japanese(st.Weather, _gen)}で{TypeNames.ToJapanese(st.MoveType)}タイプ・ダメージ2倍)");
        }
    }

    /// <summary>タイプ一致・相性・乱数（第 4 世代は持ち物・特性の補正も）を掛けて、結果を作る。</summary>
    private DamageResult BuildRollsGen34(Gen34State st)
    {
        var isStab = st.Attacker.Pokemon.HasType(st.MoveType);
        if (_gen == 4)
        {
            if (st.AttackerItem is { Category: ItemCategory.LifeOrb }) { st.BaseDamage = (int)Math.Floor(st.BaseDamage * 1.3); st.Modifiers.Add($"{st.AttackerItem.Name}(1.3倍)"); }
            var stab = isStab ? (st.AtkAbility == "adaptability" ? 2.0 : 1.5) : 1.0;
            if (isStab) st.Modifiers.Add($"タイプ一致({stab:0.#}倍)");
            if (st.Effectiveness != 1.0) st.Modifiers.Add($"タイプ相性{st.Effectiveness:0.##}倍");
            var filter = st.DefAbility is "filter" or "solid-rock" && st.Effectiveness > 1;
            var belt = st.AttackerItem is { Category: ItemCategory.ExpertBelt } && st.Effectiveness > 1;
            var tinted = st.AtkAbility == "tinted-lens" && st.Effectiveness < 1;
            var berryApplies = st.DefenderItem is { Category: ItemCategory.ResistBerry } berry && berry.TypeBoost == st.MoveType && (st.Effectiveness > 1 || st.MoveType == "Normal");
            if (filter) st.Modifiers.Add($"{st.DefName}(0.75倍)");
            if (belt) st.Modifiers.Add($"{st.AttackerItem!.Name}(1.2倍)");
            if (tinted) st.Modifiers.Add($"{st.AtkName}(2倍)");
            if (berryApplies) st.Modifiers.Add($"{st.DefenderItem!.Name}(1発目のみ 0.5倍)");

            int[] Rolls(bool withBerry)
            {
                var rolls = new int[16];
                for (var i = 0; i < 16; i++)
                {
                    var d = st.BaseDamage * (85 + i) / 100;
                    d = (int)Math.Floor(d * stab);
                    d = (int)Math.Floor(d * st.Eff1);
                    d = (int)Math.Floor(d * st.Eff2);
                    if (filter) d = (int)Math.Floor(d * 0.75);
                    if (belt) d = (int)Math.Floor(d * 1.2);
                    if (tinted) d *= 2;
                    if (withBerry) d /= 2;
                    rolls[i] = Math.Max(1, d);
                }
                return rolls;
            }
            var first = Rolls(berryApplies);
            var later = berryApplies ? Rolls(false) : first;
            return Result(first, later, st.Effectiveness, isStab, st.Attack, st.Defense, st.BasePower, st.MoveType);
        }
        else
        {
            if (isStab) { st.BaseDamage = st.BaseDamage * 3 / 2; st.Modifiers.Add("タイプ一致(1.5倍)"); }
            st.BaseDamage = (int)Math.Floor(st.BaseDamage * st.Eff1);
            st.BaseDamage = (int)Math.Floor(st.BaseDamage * st.Eff2);
            if (st.Effectiveness != 1.0) st.Modifiers.Add($"タイプ相性{st.Effectiveness:0.##}倍");
            var rolls = new int[16];
            for (var i = 0; i < 16; i++) rolls[i] = Math.Max(1, st.BaseDamage * (85 + i) / 100);
            return Result(rolls, rolls, st.Effectiveness, isStab, st.Attack, st.Defense, st.BasePower, st.MoveType);
        }

        DamageResult Result(int[] rolls, int[] laterRolls, double eff, bool stab, int atk, int def, int bp, string type)
        {
            var koOptions = KnockOutOptions.For(st.Request, st.Defender, st.DefenderHp, !st.Defender.Pokemon.HasType("Flying") && st.DefAbility != "levitate",
                laterRolls, st.Modifiers, _gen);
            return new DamageResult
            {
                Rolls = rolls,
                LaterRolls = laterRolls,
                DefenderHP = st.DefenderHp,
                TypeEffectiveness = eff,
                IsStab = stab,
                AttackStat = atk,
                DefenseStat = def,
                BasePower = bp,
                MoveType = type,
                KnockOut = KnockOutCalculator.Calculate(rolls, st.DefenderHp, koOptions),
                Modifiers = st.Modifiers,
            };
        }
    }
}
