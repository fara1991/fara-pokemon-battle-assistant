using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// 昔の世代（第 3〜7 世代）のダメージ計算。
/// 補正を掛ける順番・丸め方は、公開されているダメージ計算ツール（@smogon/calc の gen3 / gen4 / gen56 / gen789）に合わせる。
/// 第 3〜4 世代は LegacyDamageCalculator.Gen34.cs。
/// 第 8 世代以降は <see cref="DamageCalculator"/> の計算（今までの計算）をそのまま使う。
/// 対応している特性・持ち物・技の範囲は第 9 世代の計算と同じ（威力が変動する技の多くは未対応）。
/// </summary>
internal sealed partial class LegacyDamageCalculator
{
    private const int ItemIdAirBalloon = 584;
    private const int MoveIdEarthquake = 89;
    private const int MoveIdBulldoze = 523;
    private const int MoveIdFacade = 263;
    private const int MoveIdKnockOff = 282;
    private const int MoveIdSolarBeam = 76;
    private const int MoveIdSolarBlade = 669;
    private const int MoveIdBrickBreak = 280;
    private const int MoveIdPsychicFangs = 706;

    private readonly TypeChart _chart;
    private readonly int _gen;

    public LegacyDamageCalculator(TypeChart chart, int generation)
    {
        if (generation is < 3 or > 7) throw new ArgumentOutOfRangeException(nameof(generation), "第 3〜7 世代だけに対応しています");
        _chart = chart;
        _gen = generation;
    }

    /// <summary>かたやぶり系で無視される防御側の特性（この計算で効果を扱うもの）。</summary>
    private static readonly HashSet<string> IgnorableAbilities = new()
    {
        "battle-armor", "shell-armor", "bulletproof", "dry-skin", "filter", "flash-fire", "fur-coat", "grass-pelt",
        "heatproof", "levitate", "lightning-rod", "marvel-scale", "motor-drive", "multiscale", "sap-sipper",
        "solid-rock", "soundproof", "storm-drain", "thick-fat", "unaware", "volt-absorb", "water-absorb",
        "wonder-guard", "fluffy", "water-bubble",
    };

    /// <summary>第 5〜7 世代の計算の途中経過。段階ごとの関数が、順に値を埋めていく。</summary>
    private sealed class LegacyState
    {
        public LegacyState(DamageRequest request, int generation)
        {
            Request = request;
            Attacker = request.Attacker;
            Defender = request.Defender;
            Move = request.Move;
            DefenderHp = Defender.MaxHp;
            // Z 技（第 7 世代）: 威力は Z 技の表、接触・パンチなどの技の性質や追加効果は無くなる。持ち物は Z クリスタル
            IsZ = generation == 7 && request.IsZMove;
            AttackerItem = IsZ ? null : Attacker.Item;
            DefenderItem = Defender.Item;
            Weather = request.Weather;
            MoveType = Move.Type;
            Flags = IsZ ? new HashSet<string>() : Move.Flags;
            IsPhysical = Move.Category == MoveCategory.Physical;
        }

        public DamageRequest Request { get; }
        public PokemonBuild Attacker { get; }
        public PokemonBuild Defender { get; }
        public Move Move { get; }
        public int DefenderHp { get; }
        public bool IsZ { get; }
        public IReadOnlySet<string> Flags { get; }
        public bool IsPhysical { get; }
        public List<string> Modifiers { get; } = new();

        public Item? AttackerItem, DefenderItem;
        public string AtkAbility = "", DefAbility = "", AtkName = "", DefName = "";
        public Weather Weather;
        public Terrain Terrain;
        public bool IsCritical, HasAte, Reflect, LightScreen, AttackerGrounded, DefenderGrounded, ApplyBurn;
        public string MoveType;
        public double Effectiveness = 1.0;
        public int BasePower;
        public int Stab = 4096;
    }

    public DamageResult Calculate(DamageRequest request)
    {
        if (_gen <= 4) return CalculateGen34(request);
        var st = new LegacyState(request, _gen);
        if (!st.Move.IsDamaging)
            return Empty(st.DefenderHp, 1.0, st.Move.Type, "変化技");
        if (st.IsZ) st.Modifiers.Add($"Z技(威力{ZMove.Power(st.Move, st.Attacker.Pokemon)}・持ち物はZクリスタル)");

        ResolveAbilitiesAndField(st);
        ResolveMoveType(st);
        ResolveScreens(st);
        var blocked = ResolveEffectiveness(st) ?? CheckImmunity(st);
        if (blocked is not null) return blocked;

        st.AttackerGrounded = IsGrounded(st.Attacker, st.AtkAbility, st.AttackerItem);
        st.DefenderGrounded = IsGrounded(st.Defender, st.DefAbility, st.DefenderItem);
        ResolveBasePower(st);
        var attack = ComputeAttack(st);
        var defense = ComputeDefense(st);
        var baseDamage = ComputeBaseDamage(st, attack, defense);
        ResolveStabAndBurn(st);
        return BuildResult(st, baseDamage, attack, defense);
    }

    /// <summary>天候の無効化（エアロック系）、フィールド、かたやぶり系による相手の特性の無視、急所の無効を決める。</summary>
    private void ResolveAbilitiesAndField(LegacyState st)
    {
        var modifiers = st.Modifiers;
        st.AtkAbility = st.Attacker.Ability?.Identifier ?? "";
        st.DefAbility = st.Defender.Ability?.Identifier ?? "";
        st.AtkName = st.Attacker.Ability?.Name ?? "";
        st.DefName = st.Defender.Ability?.Name ?? "";

        // ノーてんき・エアロック: 天候の効果が無くなる
        if (st.Weather != Weather.None && (st.AtkAbility is "air-lock" or "cloud-nine" || st.DefAbility is "air-lock" or "cloud-nine"))
        {
            modifiers.Add($"{(st.AtkAbility is "air-lock" or "cloud-nine" ? st.AtkName : st.DefName)}(天候の効果なし)");
            st.Weather = Weather.None;
        }
        // フィールドは第 6 世代から（サイコフィールドは第 7 世代から）
        var request = st.Request;
        st.Terrain = _gen >= 6 && !(request.Terrain == Terrain.Psychic && _gen < 7) ? request.Terrain : Terrain.None;

        if (st.AtkAbility is "mold-breaker" or "teravolt" or "turboblaze" && IgnorableAbilities.Contains(st.DefAbility))
        {
            modifiers.Add($"{st.AtkName}(相手の特性を無視)");
            st.DefAbility = "";
        }

        st.IsCritical = request.IsCritical && st.DefAbility is not ("battle-armor" or "shell-armor");
        if (request.IsCritical && !st.IsCritical) modifiers.Add($"{st.DefName}(急所に当たらない)");
    }

    /// <summary>技タイプ（スキン系・ノーマルスキン）。</summary>
    private void ResolveMoveType(LegacyState st)
    {
        var move = st.Move;
        // ウェザーボールはスキン系特性でタイプが変わらない
        if (st.IsZ || move.Id == MoveEffects.MoveIdWeatherBall) return;
        var skin = SkinType(st.AtkAbility);
        if (skin is not null && st.MoveType == "Normal")
        {
            st.MoveType = skin;
            st.HasAte = true;
            st.Modifiers.Add($"{st.AtkName}({TypeNames.ToJapanese(skin)}タイプ化 {(_gen == 6 ? "1.3" : "1.2")}倍)");
        }
        else if (st.AtkAbility == "normalize")
        {
            st.MoveType = "Normal";
            st.HasAte = _gen >= 7; // 第 7 世代からノーマルスキンも 1.2 倍
            st.Modifiers.Add(_gen >= 7 ? $"{st.AtkName}(ノーマルタイプ化 1.2倍)" : $"{st.AtkName}(ノーマルタイプ化)");
        }
    }

    /// <summary>壁（かわらわり・サイコファングは壁を壊す。すりぬけは無視）。</summary>
    private static void ResolveScreens(LegacyState st)
    {
        st.Reflect = st.Request.Reflect || st.Request.Screen;
        st.LightScreen = st.Request.LightScreen || st.Request.Screen;
        if (!st.IsZ && st.Move.Id is MoveIdBrickBreak or MoveIdPsychicFangs) st.Reflect = st.LightScreen = false;
        if (st.AtkAbility == "infiltrator") st.Reflect = st.LightScreen = false;
    }

    /// <summary>相性。効果が無ければその結果を返す。</summary>
    private DamageResult? ResolveEffectiveness(LegacyState st)
    {
        var effectiveness = 1.0;
        foreach (var t in st.Defender.Pokemon.Types)
        {
            var m = _chart.Against(st.MoveType, t);
            if (m == 0 && t == "Ghost" && st.AtkAbility == "scrappy" && st.MoveType is "Normal" or "Fighting") m = 1;
            effectiveness *= m;
        }
        if (effectiveness == 0)
            return Empty(st.DefenderHp, 0, st.MoveType, "効果なし");
        st.Effectiveness = effectiveness;
        return null;
    }

    /// <summary>特性・持ち物・フィールドによる無効化。無効ならその結果を返す。</summary>
    private static DamageResult? CheckImmunity(LegacyState st)
    {
        var moveType = st.MoveType;
        var flags = st.Flags;
        var immunity = st.DefAbility switch
        {
            "wonder-guard" when st.Effectiveness <= 1 => "効果抜群以外無効",
            "sap-sipper" when moveType == "Grass" => "くさ技無効",
            "flash-fire" when moveType == "Fire" => "ほのお技無効",
            "dry-skin" or "storm-drain" or "water-absorb" when moveType == "Water" => "みず技無効",
            "lightning-rod" or "motor-drive" or "volt-absorb" when moveType == "Electric" => "でんき技無効",
            "levitate" when moveType == "Ground" => "じめん技無効",
            "bulletproof" when flags.Contains("Bullet") => "弾技無効",
            "soundproof" when flags.Contains("Sound") => "音技無効",
            _ => null,
        };
        if (immunity is not null)
            return Empty(st.DefenderHp, 0, moveType, $"{st.DefName}({immunity})");
        if (moveType == "Ground" && st.DefenderItem?.Id == ItemIdAirBalloon)
            return Empty(st.DefenderHp, 0, moveType, $"{st.DefenderItem.Name}(じめん技無効)");
        if (st.Terrain == Terrain.Psychic && st.Move.Priority > 0 && !st.IsZ && IsGrounded(st.Defender, st.DefAbility, st.DefenderItem))
            return Empty(st.DefenderHp, 0, moveType, "サイコフィールド(先制技無効)");
        return null;
    }

    /// <summary>威力（技ごとの倍率と威力の補正）。</summary>
    private void ResolveBasePower(LegacyState st)
    {
        var basePower = st.IsZ ? ZMove.Power(st.Move, st.Attacker.Pokemon) : st.Move.Power;
        var bpMods = _gen >= 7
            ? BasePowerModsGen7(st.Request, st.Attacker, st.Defender, st.Move, st.MoveType, st.IsZ, st.Flags, st.HasAte, st.AtkAbility, st.DefAbility,
                st.AttackerItem, st.DefenderItem, basePower, st.Terrain, st.Weather, st.AttackerGrounded, st.DefenderGrounded, st.Modifiers)
            : BasePowerModsGen56(st.Request, st.Attacker, st.Defender, st.Move, st.MoveType, st.Flags, st.HasAte, st.AtkAbility, st.DefAbility,
                st.AttackerItem, st.DefenderItem, basePower, st.Terrain, st.Weather, st.AttackerGrounded, st.DefenderGrounded, st.Modifiers);
        st.BasePower = Math.Max(1, DamageCalculator.PokeRound(basePower * (double)Chain(bpMods, 41, 2097152) / 4096));
    }

    /// <summary>攻撃側の実数値（イカサマは相手の攻撃とランク）。</summary>
    private int ComputeAttack(LegacyState st)
    {
        var attacker = st.Attacker;
        var move = st.Move;
        var modifiers = st.Modifiers;
        var attackStatKind = move.AttackStatUsed;
        var attackSource = !st.IsZ && move.Id == MoveEffects.MoveIdFoulPlay ? st.Defender : attacker;
        if (attackSource != attacker) modifiers.Add($"{move.Name}(相手のAで計算)");
        var attack = StatCalculator.Calculate(attackSource, attackStatKind);
        var attackBoost = attackSource.Boosts[attackStatKind];
        if (attackBoost != 0 && !(st.IsCritical && attackBoost < 0))
        {
            if (st.DefAbility == "unaware") modifiers.Add($"{st.DefName}(ランク無視)");
            else
            {
                attack = StatCalculator.ApplyBoost(attack, attackBoost);
                modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}");
            }
        }
        if (st.AtkAbility == "hustle" && st.IsPhysical)
        {
            attack = DamageCalculator.PokeRound(attack * 3 / 2.0);
            modifiers.Add($"{st.AtkName}(A1.5倍)");
        }
        var atMods = _gen >= 7
            ? AttackModsGen7(attacker, move, st.MoveType, st.IsPhysical, st.IsZ, st.AtkAbility, st.DefAbility, st.AttackerItem, st.Weather, modifiers)
            : AttackModsGen56(attacker, move, st.MoveType, st.IsPhysical, st.AtkAbility, st.DefAbility, st.AttackerItem, st.Weather, modifiers);
        return Math.Max(1, DamageCalculator.PokeRound(attack * (double)Chain(atMods, 410, 131072) / 4096));
    }

    /// <summary>防御側の実数値。サイコショックなどは相手の防御で計算（Z 技にすると特防）。</summary>
    private int ComputeDefense(LegacyState st)
    {
        var defender = st.Defender;
        var move = st.Move;
        var modifiers = st.Modifiers;
        var hitsPhysical = HitsPhysical(st);
        var defenseStatKind = hitsPhysical ? Stat.Defense : Stat.SpDefense;
        if (move.UsesPhysicalDefense && !st.IsZ) modifiers.Add($"{move.Name}(相手のBで計算)");
        var defense = StatCalculator.Calculate(defender, defenseStatKind);
        var defenseBoost = defender.Boosts[defenseStatKind];
        if (defenseBoost != 0 && !st.IsZ && MoveEffects.IgnoresDefenseBoosts(move))
        {
            modifiers.Add($"{move.Name}(相手のランク変化を無視)");
            defenseBoost = 0;
        }
        if (defenseBoost != 0 && !(st.IsCritical && defenseBoost > 0))
        {
            if (st.AtkAbility == "unaware") modifiers.Add($"{st.AtkName}(ランク無視)");
            else
            {
                defense = StatCalculator.ApplyBoost(defense, defenseBoost);
                modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}");
            }
        }
        if (st.Weather == Weather.Sand && defender.Pokemon.HasType("Rock") && !hitsPhysical)
        {
            defense = DamageCalculator.PokeRound(defense * 3 / 2.0);
            modifiers.Add("砂嵐(いわタイプのD1.5倍)");
        }
        var dfMods = _gen >= 7
            ? DefenseModsGen7(defender, hitsPhysical, st.DefAbility, st.DefenderItem, st.Terrain, modifiers)
            : DefenseModsGen56(defender, hitsPhysical, st.DefAbility, st.DefenderItem, st.Terrain, modifiers);
        return Math.Max(1, DamageCalculator.PokeRound(defense * (double)Chain(dfMods, 410, 131072) / 4096));
    }

    private static bool HitsPhysical(LegacyState st) => st.IsPhysical || st.Move.UsesPhysicalDefense && !st.IsZ;

    /// <summary>基礎ダメージ（複数対象、おやこあいの 2 回目、天候、急所まで）。</summary>
    private int ComputeBaseDamage(LegacyState st, int attack, int defense)
    {
        var request = st.Request;
        var move = st.Move;
        var weather = st.Weather;
        var moveType = st.MoveType;
        var modifiers = st.Modifiers;
        var baseDamage = (st.Attacker.Level * 2 / 5 + 2) * st.BasePower * attack / defense / 50 + 2;
        var isSpread = request.Format == BattleFormat.Doubles && move.Target != MoveTarget.Single && !st.IsZ;
        if (isSpread)
        {
            baseDamage = DamageCalculator.PokeRound(baseDamage * 3072 / 4096.0);
            modifiers.Add("複数対象(0.75倍)");
        }
        if (request.ParentalChild)
        {
            // おやこあいの 2 回目（第 6 世代 1/2、第 7 世代 1/4）
            baseDamage = DamageCalculator.PokeRound(baseDamage * (_gen >= 7 ? 1024 : 2048) / 4096.0);   // 表示は DamageCalculator のまとめの行で出す
        }
        if (weather == Weather.Sun && moveType == "Fire" || weather == Weather.Rain && moveType == "Water")
        {
            baseDamage = DamageCalculator.PokeRound(baseDamage * 6144 / 4096.0);
            modifiers.Add($"{FieldNames.Japanese(weather)}(1.5倍)");
        }
        else if (weather == Weather.Sun && moveType == "Water" || weather == Weather.Rain && moveType == "Fire")
        {
            baseDamage = DamageCalculator.PokeRound(baseDamage * 2048 / 4096.0);
            modifiers.Add($"{FieldNames.Japanese(weather)}(0.5倍)");
        }
        if (st.IsCritical)
        {
            var crit = _gen >= 6 ? 1.5 : 2.0;
            baseDamage = (int)Math.Floor(baseDamage * crit);
            modifiers.Add($"急所({crit:0.#}倍)");
        }
        return baseDamage;
    }

    /// <summary>タイプ一致、やけど（こんじょうは除く。からげんきは第 6 世代から除く）、相性の表示。</summary>
    private void ResolveStabAndBurn(LegacyState st)
    {
        var attacker = st.Attacker;
        var stab = 4096;
        if (attacker.Pokemon.HasType(st.MoveType)) stab += 2048;
        if (st.AtkAbility == "adaptability" && attacker.Pokemon.HasType(st.MoveType)) stab += 2048;
        st.Stab = stab;
        if (stab != 4096) st.Modifiers.Add($"タイプ一致({stab / 4096.0:0.##}倍)");

        st.ApplyBurn = attacker.Status == StatusCondition.Burn && st.IsPhysical && st.AtkAbility != "guts"
            && !(st.Move.Id == MoveIdFacade && !st.IsZ && _gen >= 6);
        if (st.ApplyBurn) st.Modifiers.Add("やけど(物理0.5倍)");

        if (st.Effectiveness != 1.0) st.Modifiers.Add($"タイプ相性{st.Effectiveness:0.##}倍");
    }

    /// <summary>最終補正を掛けて 16 通りのダメージと結果を作る（2 発目以降はマルチスケイル・半減きのみが無くなる）。</summary>
    private DamageResult BuildResult(LegacyState st, int baseDamage, int attack, int defense)
    {
        var finalMods = FinalMods(st.Request, st.Attacker, st.Defender, st.Move, st.MoveType, st.IsPhysical, st.IsZ, st.Flags, st.Effectiveness, st.IsCritical,
            st.Reflect, st.LightScreen, st.AtkAbility, st.DefAbility, st.AttackerItem, st.DefenderItem, st.Modifiers, firstHit: true);
        var laterMods = FinalMods(st.Request, st.Attacker, st.Defender, st.Move, st.MoveType, st.IsPhysical, st.IsZ, st.Flags, st.Effectiveness, st.IsCritical,
            st.Reflect, st.LightScreen, st.AtkAbility, st.DefAbility, st.AttackerItem, st.DefenderItem, new List<string>(), firstHit: false);
        var finalMod = Chain(finalMods, 41, 131072);
        var laterMod = Chain(laterMods, 41, 131072);

        int[] Rolls(int fm)
        {
            var result = new int[16];
            for (var i = 0; i < 16; i++)
                result[i] = FinalDamage(baseDamage, i, st.Effectiveness, st.ApplyBurn, st.Stab, fm);
            return result;
        }
        var rolls = Rolls(finalMod);
        var laterRolls = laterMod == finalMod ? rolls : Rolls(laterMod);
        var koOptions = KnockOutOptions.For(st.Request, st.Defender, st.DefenderHp, st.DefenderGrounded, laterRolls, st.Modifiers, _gen);

        return new DamageResult
        {
            Rolls = rolls,
            LaterRolls = laterRolls,
            DefenderHP = st.DefenderHp,
            TypeEffectiveness = st.Effectiveness,
            IsStab = st.Stab != 4096,
            AttackStat = attack,
            DefenseStat = defense,
            BasePower = st.BasePower,
            MoveType = st.MoveType,
            KnockOut = KnockOutCalculator.Calculate(rolls, st.DefenderHp, koOptions),
            Modifiers = st.Modifiers,
        };
    }

    /// <summary>乱数・タイプ一致・相性・やけど・最終補正（@smogon/calc の getFinalDamage と同じ）。</summary>
    internal static int FinalDamage(int baseDamage, int roll, double effectiveness, bool burn, int stab, int finalMod)
    {
        double damage = baseDamage * (85 + roll) / 100;
        if (stab != 4096) damage = damage * stab / 4096;
        var d = (int)Math.Floor(DamageCalculator.PokeRound(damage) * effectiveness);
        if (burn) d /= 2;
        return DamageCalculator.PokeRound(Math.Max(1, d * (double)finalMod / 4096));
    }

    /// <summary>4096 を 1 倍とした補正値を順に掛け合わせる（@smogon/calc の chainMods）。</summary>
    internal static int Chain(IEnumerable<int> mods, int lower, int upper)
    {
        long m = 4096;
        foreach (var mod in mods)
        {
            if (mod != 4096) m = (m * mod + 2048) >> 12;
        }
        return (int)Math.Max(Math.Min(m, upper), lower);
    }

    private string? SkinType(string ability) => ability switch
    {
        "pixilate" when _gen >= 6 => "Fairy",
        "aerilate" when _gen >= 6 => "Flying",
        "refrigerate" when _gen >= 6 => "Ice",
        "galvanize" when _gen >= 7 => "Electric",
        _ => null,
    };

    private static bool IsGrounded(PokemonBuild build, string ability, Item? item) =>
        !build.Pokemon.HasType("Flying") && ability != "levitate" && item?.Id != ItemIdAirBalloon;

    private static bool IsChoiceBand(Item? item) => item is { Category: ItemCategory.Choice, AttackMultiplier: > 1 };
    private static bool IsChoiceSpecs(Item? item) => item is { Category: ItemCategory.Choice, SpAttackMultiplier: > 1 };
    private static bool IsAssaultVest(Item? item) => item is { Category: ItemCategory.AssaultVest };
    private static bool IsMuscleBand(Item? item, bool physical) =>
        item is { Category: ItemCategory.Other, TypeBoost: "Physical" or "Special" } && item.TypeBoost == (physical ? "Physical" : "Special");
    private static bool IsTypeBoost(Item? item, string moveType) => item is { Category: ItemCategory.TypeBoost } && item.TypeBoost == moveType;

    private List<int> BasePowerModsGen56(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType,
        IReadOnlySet<string> flags, bool hasAte, string atkAbility, string defAbility, Item? attackerItem, Item? defenderItem, int basePower,
        Terrain terrain, Weather weather, bool attackerGrounded, bool defenderGrounded, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";

        if (atkAbility == "technician" && basePower <= 60) Add(6144, $"{atkName}(1.5倍)");
        else if (atkAbility == "sand-force" && weather == Weather.Sand && moveType is "Rock" or "Ground" or "Steel") Add(5325, $"{atkName}(1.3倍)");
        else if (atkAbility == "iron-fist" && flags.Contains("Punch") || atkAbility == "reckless" && MoveEffects.IsRecoil(move)) Add(4915, $"{atkName}(1.2倍)");

        if (defAbility == "heatproof" && moveType == "Fire") Add(2048, $"{defName}(0.5倍)");
        else if (defAbility == "dry-skin" && moveType == "Fire") Add(5120, $"{defName}(1.25倍)");

        if (atkAbility == "sheer-force" && MoveEffects.SheerForceBoosts(move, _gen)) Add(5325, $"{atkName}(1.3倍)");

        if (IsTypeBoost(attackerItem, moveType)) Add(4915, $"{attackerItem!.Name}(1.2倍)");
        else if (IsMuscleBand(attackerItem, move.Category == MoveCategory.Physical)) Add(4505, $"{attackerItem!.Name}(1.1倍)");

        if (move.Id == MoveIdFacade && attacker.Status is StatusCondition.Burn or StatusCondition.Paralysis or StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "からげんき(状態異常で2倍)");
        else if (move.Id == MoveEffects.MoveIdBrine && defender.CurrentHp * 2 <= defender.MaxHp)
            Add(8192, "しおみず(相手のHPが半分以下で2倍)");
        else if (move.Id == MoveEffects.MoveIdVenoshock && defender.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "ベノムショック(相手がどくで2倍)");
        else if (_gen > 5 && move.Id == MoveIdKnockOff && MoveEffects.KnockOffBoosted(defenderItem, defender)) Add(6144, "はたきおとす(持ち物があるので1.5倍)");
        else if (move.Id == MoveIdSolarBeam && weather is Weather.Rain or Weather.Sand or Weather.Snow) Add(2048, $"{move.Name}({FieldNames.Japanese(weather, _gen)}で0.5倍)");

        if (hasAte) Add(5325, $"{atkName}(1.3倍)");
        else if (atkAbility == "mega-launcher" && flags.Contains("Pulse") || atkAbility == "strong-jaw" && flags.Contains("Bite"))
            Add(6144, $"{atkName}(1.5倍)");
        else if (atkAbility == "tough-claws" && flags.Contains("Contact")) Add(5325, $"{atkName}(1.3倍)");

        if (attackerGrounded && (terrain == Terrain.Electric && moveType == "Electric" || terrain == Terrain.Grassy && moveType == "Grass"))
            Add(6144, $"{FieldNames.Japanese(terrain)}(1.5倍)");
        if (defenderGrounded && (terrain == Terrain.Misty && moveType == "Dragon" || terrain == Terrain.Grassy && move.Id is MoveIdEarthquake or MoveIdBulldoze))
            Add(2048, $"{FieldNames.Japanese(terrain)}(0.5倍)");
        return mods;
    }

    private List<int> BasePowerModsGen7(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType,
        bool isZ, IReadOnlySet<string> flags, bool hasAte, string atkAbility, string defAbility, Item? attackerItem, Item? defenderItem,
        int basePower, Terrain terrain, Weather weather, bool attackerGrounded, bool defenderGrounded, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var moveId = isZ ? 0 : move.Id;

        if (moveId == MoveIdFacade && attacker.Status is StatusCondition.Burn or StatusCondition.Paralysis or StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "からげんき(状態異常で2倍)");
        else if (moveId == MoveEffects.MoveIdBrine && defender.CurrentHp * 2 <= defender.MaxHp)
            Add(8192, "しおみず(相手のHPが半分以下で2倍)");
        else if (moveId == MoveEffects.MoveIdVenoshock && defender.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "ベノムショック(相手がどくで2倍)");
        else if (moveId == MoveIdKnockOff && MoveEffects.KnockOffBoosted(defenderItem, defender)) Add(6144, "はたきおとす(持ち物があるので1.5倍)");
        else if (moveId is MoveIdSolarBeam or MoveIdSolarBlade && weather is Weather.Rain or Weather.Sand or Weather.Snow)
            Add(2048, $"{move.Name}({FieldNames.Japanese(weather, _gen)}で0.5倍)");

        if (attackerGrounded && (terrain == Terrain.Electric && moveType == "Electric" || terrain == Terrain.Grassy && moveType == "Grass" || terrain == Terrain.Psychic && moveType == "Psychic"))
            Add(6144, $"{FieldNames.Japanese(terrain)}(1.5倍)");
        if (defenderGrounded && (terrain == Terrain.Misty && moveType == "Dragon" || terrain == Terrain.Grassy && moveId is MoveIdEarthquake or MoveIdBulldoze))
            Add(2048, $"{FieldNames.Japanese(terrain)}(0.5倍)");

        if (atkAbility == "technician" && basePower <= 60 || atkAbility == "mega-launcher" && flags.Contains("Pulse") || atkAbility == "strong-jaw" && flags.Contains("Bite"))
            Add(6144, $"{atkName}(1.5倍)");

        if (atkAbility == "sheer-force" && !isZ && MoveEffects.SheerForceBoosts(move, _gen) || atkAbility == "sand-force" && weather == Weather.Sand && moveType is "Rock" or "Ground" or "Steel"
            || atkAbility == "tough-claws" && flags.Contains("Contact"))
            Add(5325, $"{atkName}(1.3倍)");

        if (hasAte) Add(4915, $"{atkName}(1.2倍)");
        // Z 技は技が置き換わるので、すてみの対象にならない
        if (atkAbility == "iron-fist" && flags.Contains("Punch") || atkAbility == "reckless" && !isZ && MoveEffects.IsRecoil(move)) Add(4915, $"{atkName}(1.2倍)");

        if (defAbility == "heatproof" && moveType == "Fire") Add(2048, $"{defName}(0.5倍)");
        else if (defAbility == "dry-skin" && moveType == "Fire") Add(5120, $"{defName}(1.25倍)");

        if (IsTypeBoost(attackerItem, moveType)) Add(4915, $"{attackerItem!.Name}(1.2倍)");
        else if (IsMuscleBand(attackerItem, move.Category == MoveCategory.Physical)) Add(4505, $"{attackerItem!.Name}(1.1倍)");
        return mods;
    }

    private List<int> AttackModsGen56(PokemonBuild attacker, Move move, string moveType, bool isPhysical, string atkAbility, string defAbility,
        Item? attackerItem, Weather weather, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var statName = isPhysical ? "A" : "C";

        if (defAbility == "thick-fat" && moveType is "Fire" or "Ice") Add(2048, $"あついしぼう({statName}0.5倍)");

        if (atkAbility == "guts" && attacker.Status != StatusCondition.None && isPhysical || Pinch(attacker, atkAbility, moveType))
            Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "toxic-boost" && isPhysical && attacker.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned
                 || atkAbility == "flare-boost" && !isPhysical && attacker.Status == StatusCondition.Burn)
            Add(6144, $"{atkName}({StatusNames.Japanese(attacker.Status)}で{statName}1.5倍)");
        else if (atkAbility == "solar-power" && weather == Weather.Sun && !isPhysical) Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "defeatist" && attacker.CurrentHp * 2 <= attacker.MaxHp) Add(2048, $"{atkName}({statName}0.5倍)");
        else if (atkAbility is "huge-power" or "pure-power" && isPhysical) Add(8192, $"{atkName}({statName}2倍)");

        if (attackerItem is { Category: ItemCategory.ThickClub } && isPhysical && attacker.Pokemon.SpeciesId is 104 or 105
            || attackerItem is { Category: ItemCategory.LightBall } && attacker.Pokemon.SpeciesId == 25)
            Add(8192, $"{attackerItem.Name}({statName}2倍)");
        else if (IsChoiceBand(attackerItem) && isPhysical || IsChoiceSpecs(attackerItem) && !isPhysical)
            Add(6144, $"{attackerItem!.Name}({statName}1.5倍)");
        return mods;
    }

    private List<int> AttackModsGen7(PokemonBuild attacker, Move move, string moveType, bool isPhysical, bool isZ, string atkAbility, string defAbility,
        Item? attackerItem, Weather weather, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var statName = isPhysical ? "A" : "C";

        if (atkAbility == "defeatist" && attacker.CurrentHp * 2 <= attacker.MaxHp) Add(2048, $"{atkName}({statName}0.5倍)");
        else if (atkAbility == "solar-power" && weather == Weather.Sun && !isPhysical) Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "guts" && attacker.Status != StatusCondition.None && isPhysical || Pinch(attacker, atkAbility, moveType))
            Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "toxic-boost" && isPhysical && attacker.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned
                 || atkAbility == "flare-boost" && !isPhysical && attacker.Status == StatusCondition.Burn)
            Add(6144, $"{atkName}({StatusNames.Japanese(attacker.Status)}で{statName}1.5倍)");
        else if (atkAbility == "steelworker" && moveType == "Steel") Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "water-bubble" && moveType == "Water" || atkAbility is "huge-power" or "pure-power" && isPhysical)
            Add(8192, $"{atkName}({statName}2倍)");

        if (defAbility == "thick-fat" && moveType is "Fire" or "Ice" || defAbility == "water-bubble" && moveType == "Fire")
            Add(2048, $"{(defAbility == "thick-fat" ? "あついしぼう" : "すいほう")}({statName}0.5倍)");

        if (attackerItem is { Category: ItemCategory.ThickClub } && isPhysical && attacker.Pokemon.SpeciesId is 104 or 105
            || attackerItem is { Category: ItemCategory.LightBall } && attacker.Pokemon.SpeciesId == 25 && !isZ)
            Add(8192, $"{attackerItem.Name}({statName}2倍)");
        else if (!isZ && (IsChoiceBand(attackerItem) && isPhysical || IsChoiceSpecs(attackerItem) && !isPhysical))
            Add(6144, $"{attackerItem!.Name}({statName}1.5倍)");
        return mods;
    }

    private static bool Pinch(PokemonBuild attacker, string ability, string moveType) =>
        attacker.CurrentHp * 3 <= attacker.MaxHp && (ability == "overgrow" && moveType == "Grass" || ability == "blaze" && moveType == "Fire"
            || ability == "torrent" && moveType == "Water" || ability == "swarm" && moveType == "Bug");

    private static List<int> DefenseModsGen56(PokemonBuild defender, bool hitsPhysical, string defAbility, Item? defenderItem, Terrain terrain, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var defName = defender.Ability?.Name ?? "";
        var statName = hitsPhysical ? "B" : "D";

        if (defAbility == "marvel-scale" && defender.Status != StatusCondition.None && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        if (defAbility == "grass-pelt" && terrain == Terrain.Grassy && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        if (defenderItem is { Category: ItemCategory.Eviolite } && defender.Pokemon.NotFullyEvolved || IsAssaultVest(defenderItem) && !hitsPhysical)
            Add(6144, $"{defenderItem!.Name}({statName}1.5倍)");
        if (defAbility == "fur-coat" && hitsPhysical) Add(8192, $"{defName}({statName}2倍)");
        return mods;
    }

    private static List<int> DefenseModsGen7(PokemonBuild defender, bool hitsPhysical, string defAbility, Item? defenderItem, Terrain terrain, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var defName = defender.Ability?.Name ?? "";
        var statName = hitsPhysical ? "B" : "D";

        if (defAbility == "marvel-scale" && defender.Status != StatusCondition.None && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        else if (defAbility == "grass-pelt" && terrain == Terrain.Grassy && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        else if (defAbility == "fur-coat" && hitsPhysical) Add(8192, $"{defName}({statName}2倍)");
        if (defenderItem is { Category: ItemCategory.Eviolite } && defender.Pokemon.NotFullyEvolved || IsAssaultVest(defenderItem) && !hitsPhysical)
            Add(6144, $"{defenderItem!.Name}({statName}1.5倍)");
        return mods;
    }

    private List<int> FinalMods(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType, bool isPhysical,
        bool isZ, IReadOnlySet<string> flags, double effectiveness, bool isCritical, bool reflect, bool lightScreen,
        string atkAbility, string defAbility, Item? attackerItem, Item? defenderItem, List<string> modifiers, bool firstHit)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var doubles = request.Format == BattleFormat.Doubles;

        // 壁は技の分類で決まる（サイコショックは特殊技なのでひかりのかべ）
        if (!isCritical && (reflect && isPhysical || lightScreen && !isPhysical))
        {
            var wall = isPhysical ? "リフレクター" : "ひかりのかべ";
            if (doubles) Add(_gen > 5 ? 2732 : 2703, $"{wall}(ダブル 0.67倍)");
            else Add(2048, $"{wall}(0.5倍)");
        }

        var fullHp = defender.IsFullHp;
        if (_gen >= 7)
        {
            if (atkAbility == "neuroforce" && effectiveness > 1) Add(5120, $"{atkName}(1.25倍)");
            else if (atkAbility == "sniper" && isCritical) Add(6144, $"{atkName}(急所1.5倍)");
            else if (atkAbility == "tinted-lens" && effectiveness < 1) Add(8192, $"{atkName}(2倍)");
            if (defAbility is "multiscale" or "shadow-shield" && fullHp && firstHit) Add(2048, $"{defName}(HP満タン 0.5倍)");
            if (defAbility == "fluffy" && flags.Contains("Contact")) Add(2048, $"{defName}(接触技0.5倍)");
            if (defAbility is "solid-rock" or "filter" or "prism-armor" && effectiveness > 1) Add(3072, $"{defName}(0.75倍)");
            if (defAbility == "fluffy" && moveType == "Fire") Add(8192, $"{defName}(ほのお技2倍)");
            if (attackerItem is { Category: ItemCategory.ExpertBelt } && effectiveness > 1 && !isZ) Add(4915, $"{attackerItem.Name}(1.2倍)");
            else if (attackerItem is { Category: ItemCategory.LifeOrb }) Add(5324, $"{attackerItem.Name}(1.3倍)");
        }
        else
        {
            if (defAbility == "multiscale" && fullHp && firstHit) Add(2048, $"{defName}(HP満タン 0.5倍)");
            if (atkAbility == "tinted-lens" && effectiveness < 1) Add(8192, $"{atkName}(2倍)");
            if (atkAbility == "sniper" && isCritical) Add(6144, $"{atkName}(急所1.5倍)");
            if (defAbility is "solid-rock" or "filter" && effectiveness > 1) Add(3072, $"{defName}(0.75倍)");
            if (attackerItem is { Category: ItemCategory.ExpertBelt } && effectiveness > 1) Add(4915, $"{attackerItem.Name}(1.2倍)");
            else if (attackerItem is { Category: ItemCategory.LifeOrb }) Add(5324, $"{attackerItem.Name}(1.3倍)");
        }

        if (firstHit && atkAbility != "unnerve" && defenderItem is { Category: ItemCategory.ResistBerry } berry && berry.TypeBoost == moveType && (effectiveness > 1 || moveType == "Normal"))
            Add(2048, $"{berry.Name}(1発目のみ 0.5倍)");
        return mods;
    }

    private static DamageResult Empty(int defenderHp, double effectiveness, string moveType, string reason) => new()
    {
        Rolls = new int[16],
        DefenderHP = defenderHp,
        TypeEffectiveness = effectiveness,
        IsStab = false,
        AttackStat = 0,
        DefenseStat = 0,
        BasePower = 0,
        MoveType = moveType,
        KnockOut = new KnockOut(0, 0),
        Modifiers = new[] { reason },
    };
}

/// <summary>Z 技（第 7 世代）の威力。元の技の威力から表で決まり、一部の技は個別の値。</summary>
public static class ZMove
{
    /// <summary>表と違う Z 技の威力（技 ID → 威力）。連続技・威力が変わる技など。</summary>
    private static readonly Dictionary<int, int> Exceptions = new()
    {
        [42] = 140,   // ミサイルばり
        [72] = 120,   // メガドレイン
        [131] = 120,  // とげキャノン
        [167] = 120,  // トリプルキック
        [198] = 140,  // ボーンラッシュ
        [311] = 160,  // ウェザーボール
        [331] = 140,  // タネマシンガン
        [333] = 140,  // つららばり
        [350] = 140,  // ロックブラスト
        [458] = 140,  // ダブルアタック
        [500] = 160,  // アシストパワー
        [506] = 160,  // たたりめ
        [541] = 140,  // スイープビンタ
        [544] = 180,  // ギアソーサー
        [557] = 220,  // Vジェネレート
        [560] = 170,  // フライングプレス
        [614] = 180,  // サウザンアロー
        [616] = 185,  // グランドフォース
        [681] = 160,  // パワートリップ
        [687] = 140,  // コアパニッシャー
        [718] = 185,  // マルチアタック
        [742] = 180,  // ダブルパンツァー
    };

    /// <summary>専用 Z 技（技 ID → 条件に合うポケモン・技名・威力）。専用の Z クリスタルを持つ前提で、そのポケモンがその技を使うときだけ。</summary>
    private sealed record Exclusive(Func<Pokemon, bool> Holder, string Name, int Power);

    private static Func<Pokemon, bool> Species(params int[] ids) => p => ids.Contains(p.SpeciesId);
    private static Func<Pokemon, bool> Forms(params int[] ids) => p => ids.Contains(p.Id);

    private static readonly Dictionary<int, Exclusive> ExclusiveMoves = new()
    {
        [344] = new(Species(25), "ひっさつのピカチュート", 210),                       // ボルテッカー（ピカチュウ）
        [85] = new(Forms(10100), "ライトニングサーフライド", 175),                     // 10まんボルト（アローラのライチュウ）
        [662] = new(Species(724), "シャドーアローズストライク", 180),                   // かげぬい（ジュナイパー）
        [663] = new(Species(727), "ハイパーダーククラッシャー", 180),                   // DDラリアット（ガオガエン）
        [664] = new(Species(730), "わだつみのシンフォニア", 195),                       // うたかたのアリア（アシレーヌ）
        [712] = new(Species(802), "しちせいだっこんたい", 195),                         // シャドースチール（マーシャドー）
        [416] = new(Species(143), "ほんきをだす　こうげき", 210),                       // ギガインパクト（カビゴン）
        [94] = new(Species(151), "オリジンズスーパーノヴァ", 185),                     // サイコキネシス（ミュウ）
        [722] = new(Forms(10157), "てんこがすめつぼうのひかり", 200),                   // フォトンゲイザー（ウルトラネクロズマ）
        [713] = new(p => p.SpeciesId == 791 || p.Id == 10155, "サンシャインスマッシャー", 200),   // メテオドライブ（ソルガレオ・たそがれのたてがみ）
        [714] = new(p => p.SpeciesId == 792 || p.Id == 10156, "ムーンライトブラスター", 200),     // シャドーレイ（ルナアーラ・あかつきのつばさ）
        [583] = new(Species(778), "ぽかぼかフレンドタイム", 190),                       // じゃれつく（ミミッキュ）
        [444] = new(Species(745), "ラジアルエッジストーム", 190),                       // ストーンエッジ（ルガルガン）
        [691] = new(Species(784), "ブレイジングソウルビート", 185),                     // スケイルノイズ（ジャラランガ）
    };

    private static readonly Dictionary<string, string> GenericNames = new()
    {
        ["Normal"] = "ウルトラダッシュアタック", ["Fighting"] = "ぜんりょくむそうげきれつけん", ["Flying"] = "ファイナルダイブクラッシュ",
        ["Poison"] = "アシッドポイズンデリート", ["Ground"] = "ライジングランドオーバー", ["Rock"] = "ワールズエンドフォール",
        ["Bug"] = "ぜったいほしょくかいてんざん", ["Ghost"] = "むげんあんやへのいざない", ["Steel"] = "ちょうぜつらせんれんげき",
        ["Fire"] = "ダイナミックフルフレイム", ["Water"] = "スーパーアクアトルネード", ["Grass"] = "ブルームシャインエクストラ",
        ["Electric"] = "スパーキングギガボルト", ["Psychic"] = "マキシマムサイブレイカー", ["Ice"] = "レイジングジオフリーズ",
        ["Dragon"] = "アルティメットドラゴンバーン", ["Dark"] = "ブラックホールイクリプス", ["Fairy"] = "ラブリースターインパクト",
    };

    /// <summary>専用 Z 技があるポケモンと技の組み合わせか。</summary>
    public static bool HasExclusive(Move move, Pokemon attacker) => ExclusiveMoves.TryGetValue(move.Id, out var e) && e.Holder(attacker);

    /// <summary><paramref name="attacker"/> が <paramref name="move"/> を Z 技にしたときの技名（専用 Z 技があればそれ）。</summary>
    public static string Name(Move move, Pokemon attacker, string moveType) =>
        ExclusiveMoves.TryGetValue(move.Id, out var e) && e.Holder(attacker) ? e.Name
        : GenericNames.TryGetValue(moveType, out var n) ? n : "Z技";

    /// <summary><paramref name="attacker"/> が <paramref name="move"/> を Z 技にしたときの威力。</summary>
    public static int Power(Move move, Pokemon attacker) =>
        ExclusiveMoves.TryGetValue(move.Id, out var e) && e.Holder(attacker) ? e.Power : Power(move);

    public static int Power(Move move)
    {
        if (Exceptions.TryGetValue(move.Id, out var power)) return power;
        return move.Power switch
        {
            >= 140 => 200,
            >= 130 => 195,
            >= 120 => 190,
            >= 110 => 185,
            >= 100 => 180,
            >= 90 => 175,
            >= 80 => 160,
            >= 70 => 140,
            >= 60 => 120,
            _ => 100,
        };
    }
}
