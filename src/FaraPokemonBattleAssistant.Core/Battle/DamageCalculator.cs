using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// ダメージ計算。乱数 16 通りをすべて返す。第 3〜7 世代は <see cref="LegacyDamageCalculator"/>、第 8 世代以降（剣盾・SV・チャンピオンズ）はこのクラスで計算する。
/// 対応: 実数値・ランク補正・急所・タイプ一致・タイプ相性・ダブルの複数対象・持ち物・テラスタル・ダイマックス・天候・フィールド・
/// 主要な特性（<see cref="AbilityEffects"/> 参照。わざわい系・テラスシェル・ノーてんき・シェルアーマー・きもったま・よわき・ふしぎなうろこなど）・
/// 壁・やけど・HP 依存の特性・回復きのみ / たべのこし / 半減きのみ・フィールドのシード・ジュエル・ねらいのまと。
/// 補正の分け方と掛ける順番は、公開されているダメージ計算ツール（@smogon/calc の gen789）に合わせ、tests の calc_vectors.json と照合している。
/// 技ごとの処理: ウェザーボール・だいちのはどう・たたりめ・しおみず・ベノムショック・イカサマ・ボディプレス・せいなるつるぎ系・
/// 必ず急所に当たる技・連続技（2〜5 回の技は 3 回、スキルリンクは 5 回。全部の回の合計）・かわらわり系（壁を壊す）・メテオビーム・
/// ライジングボルト・ワイドフォース・サイコブレイド・ミストバースト・はたきおとす（はたきおとせない持ち物は 1.5 倍にならない）など。
/// 未対応: 威力が状況で変わる技の一部（しっぺがえし・ジャイロボールなど）。
/// 世代（コンストラクターの generation）が 8（ソード・シールド）のときは、第 9 世代で増えた「雪」の効果（こおりタイプの防御上昇）を使わない。
/// </summary>
public sealed class DamageCalculator
{
    private const int MoveIdEarthquake = 89;
    private const int MoveIdMagnitude = 222;
    private const int MoveIdBulldoze = 523;
    private const int MoveIdExpandingForce = 797;
    private const int MoveIdFutureSight = 248;
    private const int MoveIdDoomDesire = 353;
    private const int MoveIdMistyExplosion = 802;
    private const int MoveIdRisingVoltage = 804;
    private const int MoveIdPsyblade = 875;
    private const int MoveIdHydroSteam = 876;
    private const int ItemIdElectricSeed = 898;
    private const int ItemIdPsychicSeed = 899;
    private const int ItemIdMistySeed = 900;
    private const int ItemIdGrassySeed = 901;
    private const int ItemIdAirBalloon = 584;
    private const int ItemIdRingTarget = 586;
    private const int ItemIdKeeBerry = 724;
    private const int ItemIdMarangaBerry = 725;
    private const int ItemIdLuminousMoss = 688;
    private const int ItemIdBoosterEnergy = 1696;
    private const int ItemIdPunchingGlove = 1700;
    private const int MoveIdFacade = 263;
    private const int MoveIdKnockOff = 282;
    private const int MoveIdSolarBeam = 76;
    private const int MoveIdSolarBlade = 669;

    private readonly TypeChart _typeChart;
    private readonly LegacyDamageCalculator? _legacy;
    private readonly int _generation;

    /// <summary>
    /// <paramref name="generation"/> はゲームの世代。第 3〜7 世代は <see cref="LegacyDamageCalculator"/>（その世代の補正の順番・丸め）で計算する。
    /// </summary>
    public DamageCalculator(TypeChart typeChart, int generation = 9)
    {
        _typeChart = typeChart;
        _generation = generation;
        if (generation is >= 3 and <= 7) _legacy = new LegacyDamageCalculator(typeChart, generation);
        else if (generation < 5) throw new NotSupportedException($"第{generation}世代のダメージ計算には対応していません");
    }

    /// <summary>表と違うダイマックス技の威力（技 ID → 威力）。連続技・威力が変わる技など（@smogon/calc の maxPower に合わせる）。</summary>
    private static readonly Dictionary<int, int> MaxPowerExceptions = new()
    {
        [4] = 100,     // れんぞくパンチ
        [24] = 80,     // にどげり
        [41] = 100,    // ダブルニードル
        [42] = 130,    // ミサイルばり
        [131] = 120,   // とげキャノン
        [154] = 100,   // みだれひっかき
        [155] = 130,   // ホネブーメラン
        [167] = 80,    // トリプルキック
        [198] = 130,   // ボーンラッシュ
        [311] = 130,   // ウェザーボール
        [331] = 130,   // タネマシンガン
        [333] = 130,   // つららばり
        [350] = 130,   // ロックブラスト
        [458] = 120,   // ダブルアタック
        [500] = 130,   // アシストパワー
        [530] = 130,   // ダブルチョップ
        [541] = 130,   // スイープビンタ
        [544] = 130,   // ギアソーサー
        [681] = 130,   // つけあがる
        [718] = 95,    // マルチアタック
        [742] = 140,   // ダブルパンツァー
        [751] = 130,   // ドラゴンアロー
        [799] = 130,   // スケイルショット
        [804] = 140,   // ライジングボルト
        [805] = 130,   // だいちのはどう
        [813] = 140,   // トリプルアクセル
        [814] = 130,   // ダブルウイング
        [818] = 130,   // すいりゅうれんだ
    };

    /// <summary>ダイマックス技の威力（かくとう・どくは低めの表。連続技など一部の技は個別の値）。</summary>
    public static int MaxMovePower(Move move)
    {
        var bp = move.Power;
        if (bp <= 0) return bp;
        if (MaxPowerExceptions.TryGetValue(move.Id, out var exception)) return exception;
        var low = move.Type is "Fighting" or "Poison";
        return bp switch
        {
            <= 40 => low ? 70 : 90,
            <= 50 => low ? 75 : 100,
            <= 60 => low ? 80 : 110,
            <= 70 => low ? 85 : 120,
            <= 100 => low ? 90 : 130,
            <= 140 => low ? 95 : 140,
            _ => low ? 100 : 150,
        };
    }

    public DamageResult Calculate(DamageRequest request)
    {
        // テラスタルしたオーガポンは、特性・能力ランク・テラスタイプがお面で決まる
        var attacker = MoveEffects.AdjustOgerponTerastal(request.Attacker, out var attackerNote);
        var defender = MoveEffects.AdjustOgerponTerastal(request.Defender, out var defenderNote);
        if (!ReferenceEquals(attacker, request.Attacker) || !ReferenceEquals(defender, request.Defender))
            request = request.With(attacker: attacker, defender: defender);
        var result = CalculateCore(request);
        if (attackerNote is not null) AddNote(result, attackerNote);
        if (defenderNote is not null) AddNote(result, defenderNote);
        // ダイマックス技・Z 技に変わった技の名前と威力（結果の表示用。威力は計算に使った値）
        result.Converted = ConvertedMoves.For(request, result.MoveType.Length > 0 ? result.MoveType : request.Move.Type, _generation);
        return result;
    }

    private DamageResult CalculateCore(DamageRequest request)
    {
        var move = request.Move;
        var maxOrZ = request.Attacker.IsDynamax || request.IsZMove && _generation == 7;
        // 必ず急所に当たる技（やまあらし・あんこくきょうだなど）
        var forcedCrit = !maxOrZ && !request.IsCritical && MoveEffects.AlwaysCrits(move, _generation);
        if (forcedCrit) request = request.With(isCritical: true);
        var hits = maxOrZ || !move.IsDamaging ? 1 : MoveEffects.HitCount(move, request.Attacker.Ability?.Identifier);
        // 技そのものが連続技か（回数を 1 に指定しても、連続技にはテラスタルの威力 60 保証が掛からない）
        var isMultiHitMove = hits > 1;
        if (hits > 1 && request.HitCountOverride is { } overrideHits && (MoveEffects.HitsTwoToFive(move) || MoveEffects.IsEscalating(move))) hits = Math.Clamp(overrideHits, 1, hits == 3 && MoveEffects.IsEscalating(move) ? 3 : 5);
        // おやこあい: 連続技でない技が 2 回当たる（2 回目は第 6 世代が 1/2、第 7 世代から 1/4。ダブルの全体技・Z 技・ダイマックス技は対象外）
        var parental = hits == 1 && request.HitCountOverride != 1 && !maxOrZ && move.IsDamaging && move.Power > 0 && _generation >= 6
            && request.Attacker.Ability?.Identifier == "parental-bond"
            && !(request.Format == BattleFormat.Doubles && move.Target != MoveTarget.Single);
        var first = CalculateHit(request, 1, multiHit: isMultiHitMove);
        if (forcedCrit && !first.Modifiers.Any(m => m.Contains("急所に当たらない"))) AddNote(first, $"{move.Name}(必ず急所)");
        if (parental && first.MaxDamage > 0) hits = 2;
        if (hits <= 1 || first.MaxDamage == 0) return first;

        // 連続技: 1 回ずつ計算して足す（2 回目以降はマルチスケイル・半減きのみが効かない。トリプルアクセルなどは回ごとに威力が上がる）。
        // 当たるたびに防御側の能力が変わる特性・持ち物（じきゅうりょく・くだけるよろい・みずがためと、タラプのみ・アッキのみ・ひかりごけ）も反映する
        var total = (int[])first.Rolls.Clone();
        var later = (int[])(first.LaterRolls ?? first.Rolls).Clone();
        var hitRolls = new List<int[]> { (int[])first.Rolls.Clone() };
        var defenderNow = request.Defender.Clone();
        var usedItem = false;
        var changes = new List<string>();
        for (var hit = 2; hit <= hits; hit++)
        {
            usedItem = ApplyHitReaction(request, first, defenderNow, usedItem, changes);
            var r = CalculateHit(request.With(defender: defenderNow), hit, parentalChild: parental, multiHit: !parental);
            var rolls = r.LaterRolls ?? r.Rolls;
            hitRolls.Add((int[])rolls.Clone());
            for (var i = 0; i < 16; i++)
            {
                total[i] += rolls[i];
                later[i] += rolls[i];
            }
        }
        var modifiers = first.Modifiers.ToList();
        modifiers.AddRange(changes.Distinct());
        modifiers.Insert(0, parental
            ? $"おやこあい(2回当たる。2回目は{(_generation >= 7 ? "1/4" : "1/2")}。合計)"
            : MoveEffects.HitsTwoToFive(move)
            ? $"{move.Name}({hits}回当たったとき)"
            : $"{move.Name}({hits}回の合計)");
        var defenderHp = first.DefenderHP;
        var defender = request.Defender;
        var grounded = !defender.DefensiveTypes.Contains("Flying") && defender.Ability?.Identifier is not ("levitate" or "eelevate");
        var koOptions = KnockOutOptions.For(request, defender, defenderHp, grounded, later, new List<string>(), _generation);
        return new DamageResult
        {
            Rolls = total,
            LaterRolls = later,
            Hits = hits,
            HitRolls = hitRolls,
            ParentalBond = parental,
            DefenderHP = defenderHp,
            TypeEffectiveness = first.TypeEffectiveness,
            IsStab = first.IsStab,
            AttackStat = first.AttackStat,
            DefenseStat = first.DefenseStat,
            BasePower = first.BasePower,
            MoveType = first.MoveType,
            KnockOut = KnockOutCalculator.Calculate(total, defenderHp, koOptions),
            Modifiers = modifiers,
        };
    }

    /// <summary>
    /// 連続技が 1 回当たったあとの防御側の能力の変化（<paramref name="defender"/> を書き換える）。
    /// 持ち物（タラプのみ・アッキのみ・ひかりごけ）は 1 回だけ。てんねんの攻撃側には関係しない。使った持ち物があれば true を返す。
    /// </summary>
    private bool ApplyHitReaction(DamageRequest request, DamageResult first, PokemonBuild defender, bool usedItem, List<string> changes)
    {
        if (request.Attacker.Ability?.Identifier == "unaware") return usedItem;
        var physical = request.Move.Category == MoveCategory.Physical;
        void Boost(Stat stat, int amount, string text)
        {
            defender.Boosts[stat] = Math.Clamp(defender.Boosts[stat] + amount, -6, 6);
            changes.Add(text);
        }
        var item = defender.Item;
        if (!usedItem && _generation >= 6 && item is not null)
        {
            var (stat, applies) = item.Id switch
            {
                ItemIdKeeBerry => (Stat.Defense, physical),
                ItemIdMarangaBerry => (Stat.SpDefense, !physical),
                ItemIdLuminousMoss => (Stat.SpDefense, first.MoveType == "Water"),
                _ => (Stat.HP, false),
            };
            if (applies)
            {
                Boost(stat, 1, $"{item.Name}(1回目で{StatNames.Letter(stat)}+1)");
                usedItem = true;
            }
        }
        switch (defender.Ability?.Identifier)
        {
            case "stamina" when _generation >= 7:
                Boost(Stat.Defense, 1, $"{defender.Ability.Name}(当たるたびにB+1)");
                break;
            case "water-compaction" when _generation >= 7 && first.MoveType == "Water":
                Boost(Stat.Defense, 2, $"{defender.Ability.Name}(当たるたびにB+2)");
                break;
            case "weak-armor" when _generation >= 5 && physical:
                Boost(Stat.Defense, -1, $"{defender.Ability.Name}(当たるたびにB-1)");
                break;
        }
        return usedItem;
    }

    private static void AddNote(DamageResult result, string note)
    {
        if (result.Modifiers is List<string> list) list.Insert(0, note);
    }

    /// <summary>連続技の <paramref name="hit"/> 回目（1 回目は 1）のダメージ。</summary>
    private DamageResult CalculateHit(DamageRequest request, int hit, bool parentalChild = false, bool multiHit = false)
    {
        var move = request.Move;
        var power = MoveEffects.PowerForHit(move, hit);
        if (power != move.Power) request = request.With(move: MoveEffects.With(move, power: power));
        if (_legacy is not null) return _legacy.Calculate(LegacyMove(parentalChild ? request.With(parentalChild: true) : request));
        return CalculateModern(request, parentalChild, multiHit);
    }

    /// <summary>
    /// 第 4〜7 世代の計算に渡す前に、威力・タイプが変わる技を置き換える（ウェザーボール・たたりめ・しおみず（第 4 世代））。
    /// 第 3 世代のウェザーボールは、威力ではなくダメージを 2 倍にするので置き換えない（LegacyDamageCalculator.Gen34.cs で扱う）。
    /// </summary>
    private DamageRequest LegacyMove(DamageRequest request)
    {
        var move = request.Move;
        var isZ = request.IsZMove && _generation == 7;
        var abilities = new[] { request.Attacker.Ability?.Identifier, request.Defender.Ability?.Identifier };
        var weather = abilities.Any(a => AbilityEffects.NegatesWeather(a ?? "")) ? Weather.None : request.Weather;
        // Z 技にしたウェザーボールは、天候のタイプの Z 技（威力 160 のまま）
        if (move.Id == MoveEffects.MoveIdWeatherBall && _generation >= 4 && MoveEffects.WeatherBallType(weather) is { } type)
            return request.With(move: MoveEffects.With(move, type: type, power: isZ ? move.Power : move.Power * 2));
        if (isZ) return request;
        if (MoveEffects.DoublesAgainstStatus(move, request.Defender.Status)
            || move.Id == MoveEffects.MoveIdBrine && _generation == 4 && request.Defender.CurrentHp * 2 <= request.Defender.MaxHp)
            return request.With(move: MoveEffects.With(move, power: move.Power * 2));
        return request;
    }

    /// <summary>第 8 世代以降の計算の途中経過。段階ごとの関数が、順に値を埋めていく。</summary>
    private sealed class ModernState
    {
        public ModernState(DamageRequest request)
        {
            Request = request;
            Attacker = request.Attacker;
            Defender = request.Defender;
            Move = request.Move;
            DefenderHp = Defender.MaxHp;
            // ダイマックス技は技が置き換わるので、技ごとの効果（威力・使う能力・壁を壊すなど）は無い
            MoveId = Attacker.IsDynamax ? 0 : Move.Id;
            MoveType = Move.Type;
            Target = Move.Target;
            Weather = request.Weather;
            Terrain = request.Terrain;
            AttackerItem = Attacker.Item;
            DefenderItem = Defender.Item;
        }

        public DamageRequest Request { get; }
        public PokemonBuild Attacker { get; }
        public PokemonBuild Defender { get; }
        public Move Move { get; }
        public int DefenderHp { get; }
        public int MoveId { get; }
        public List<string> Modifiers { get; } = new();

        public string AttackerAbility = "", DefenderAbility = "", AttackerAbilityName = "", DefenderAbilityName = "";
        public Weather Weather;
        public Terrain Terrain;
        public Item? AttackerItem, DefenderItem;
        public Stat? AttackerSeedBoost, DefenderSeedBoost;
        public bool IsCritical, IsPhysical, HitsPhysical, AttackerGrounded, DefenderGrounded;
        public Stat AttackStatKind, DefenseStatKind;
        public string MoveType;
        public bool HasAte;
        public bool Reflect, LightScreen;
        public double Effectiveness = 1.0;
        public MoveTarget Target;
        public int Power;
        public bool IsTeraStab;
        public int StabMod = 4096;
        public bool ApplyBurn;
    }

    /// <summary>
    /// 第 8 世代以降の計算。補正の分け方（威力・攻撃・防御・最後）と掛ける順番は、公開されているダメージ計算ツール（@smogon/calc の gen789）に合わせる
    /// （補正は 1 つ掛けるごとに丸めるので、順番が違うと 1 ずれることがある）。段階ごとに関数を分けているが、呼ぶ順番は計算の順番そのもの。
    /// </summary>
    private DamageResult CalculateModern(DamageRequest request, bool parentalChild = false, bool multiHit = false)
    {
        var st = new ModernState(request);
        if (st.Defender.IsDynamax) st.Modifiers.Add("防御側ダイマックス(HP2倍)");
        if (!st.Move.IsDamaging)
            return Empty(st.DefenderHp, 1.0, st.Move.Type, "変化技");

        ResolveAbilitiesAndField(st);
        ResolveSeedsAndStats(st);
        ResolveMoveType(st);
        ResolveScreens(st);
        var blocked = ResolveEffectiveness(st) ?? CheckImmunity(st);
        if (blocked is not null) return blocked;

        ResolvePower(st, multiHit);
        var attackStat = ComputeAttackStat(st);
        var defenseStat = ComputeDefenseStat(st);
        var baseDamage = ComputeBaseDamage(st, attackStat, defenseStat, parentalChild);
        ResolveStabAndBurn(st);
        return BuildModernResult(st, baseDamage, attackStat, defenseStat);
    }

    /// <summary>特性による天候・防御側の特性の無視、急所の無効を決める。</summary>
    private static void ResolveAbilitiesAndField(ModernState st)
    {
        var attacker = st.Attacker;
        var defender = st.Defender;
        var modifiers = st.Modifiers;
        st.AttackerAbility = attacker.Ability?.Identifier ?? "";
        st.DefenderAbility = defender.Ability?.Identifier ?? "";
        st.AttackerAbilityName = attacker.Ability?.Name ?? "";

        // ノーてんき・エアロック: どちらかが持っていれば天候の効果が無い
        if (st.Weather != Weather.None && (AbilityEffects.NegatesWeather(st.AttackerAbility) || AbilityEffects.NegatesWeather(st.DefenderAbility)))
        {
            modifiers.Add($"{(AbilityEffects.NegatesWeather(st.AttackerAbility) ? st.AttackerAbilityName : defender.Ability!.Name)}(天候の効果なし)");
            st.Weather = Weather.None;
        }

        // かたやぶり系は防御側の特性を無視する（シャドーシールド・プリズムアーマーなど無視できない特性もある）
        if (AbilityEffects.IgnoresTargetAbility(st.AttackerAbility) && AbilityEffects.IsIgnorable(st.DefenderAbility))
        {
            modifiers.Add($"{st.AttackerAbilityName}(相手の特性を無視)");
            st.DefenderAbility = "";
        }
        st.DefenderAbilityName = st.DefenderAbility.Length > 0 ? defender.Ability!.Name : "";
    }

    /// <summary>フィールドのシード、急所、使う能力、地面にいるかを決める。</summary>
    private static void ResolveSeedsAndStats(ModernState st)
    {
        var move = st.Move;
        var modifiers = st.Modifiers;
        // フィールドのシード: そのフィールドなら防御（エレキ・グラス）か特防（サイコ・ミスト）が 1 段階上がり、持ち物は無くなる
        if (SeedStat(st.DefenderItem, st.Terrain) is { } seedStat)
        {
            st.DefenderSeedBoost = seedStat;
            modifiers.Add($"{st.DefenderItem!.Name}({StatNames.Letter(seedStat)}+1)");
            st.DefenderItem = null;
        }
        st.AttackerSeedBoost = SeedStat(st.AttackerItem, st.Terrain);
        if (st.AttackerSeedBoost is not null) st.AttackerItem = null;

        // シェルアーマー・カブトアーマー: 急所に当たらない
        st.IsCritical = st.Request.IsCritical && st.DefenderAbility is not ("battle-armor" or "shell-armor");
        if (st.Request.IsCritical && !st.IsCritical) modifiers.Add($"{st.DefenderAbilityName}(急所に当たらない)");

        st.IsPhysical = move.Category == MoveCategory.Physical;
        st.AttackStatKind = move.AttackStatUsed;
        st.DefenseStatKind = move.DefenseStatUsed;
        st.HitsPhysical = st.DefenseStatKind == Stat.Defense;
        if (move.UsesPhysicalDefense) modifiers.Add($"{move.Name}(相手のBで計算)");

        st.AttackerGrounded = IsGrounded(st.Attacker, st.AttackerAbility, st.AttackerItem);
        st.DefenderGrounded = IsGrounded(st.Defender, st.DefenderAbility, st.DefenderItem);
    }

    /// <summary>技タイプ（ウェザーボール・だいちのはどう・レイジングブル、スキン系・ノーマルスキン）。</summary>
    private void ResolveMoveType(ModernState st)
    {
        var move = st.Move;
        var attacker = st.Attacker;
        var modifiers = st.Modifiers;
        var noTypeChange = move.Id is MoveEffects.MoveIdWeatherBall or MoveEffects.MoveIdTerrainPulse;
        if (move.Id == MoveEffects.MoveIdWeatherBall && MoveEffects.WeatherBallType(st.Weather) is { } weatherBallType)
        {
            st.MoveType = weatherBallType;
            modifiers.Add($"{move.Name}({FieldNames.Japanese(st.Weather, _generation)}で{TypeNames.ToJapanese(st.MoveType)}タイプ{(attacker.IsDynamax ? "" : "・威力2倍")})");
        }
        else if (move.Id == MoveEffects.MoveIdTerrainPulse && st.AttackerGrounded && MoveEffects.TerrainPulseType(st.Terrain) is { } terrainPulseType)
        {
            st.MoveType = terrainPulseType;
            modifiers.Add($"{move.Name}({FieldNames.Japanese(st.Terrain)}で{TypeNames.ToJapanese(st.MoveType)}タイプ{(attacker.IsDynamax ? "" : "・威力2倍")})");
        }
        else if (move.Id == MoveEffects.MoveIdRagingBull)
            st.MoveType = MoveEffects.RagingBullType(attacker.Pokemon, st.MoveType);
        else if (move.Id == MoveEffects.MoveIdIvyCudgel)
            st.MoveType = MoveEffects.IvyCudgelType(attacker.Pokemon, st.MoveType);
        var skinType = noTypeChange ? null : AbilityEffects.SkinType(st.AttackerAbility);
        if (skinType is not null && st.MoveType == "Normal")
        {
            st.MoveType = skinType;
            st.HasAte = true;
            modifiers.Add($"{st.AttackerAbilityName}({TypeNames.ToJapanese(skinType)}タイプ化)");
        }
        else if (st.AttackerAbility == "normalize" && !noTypeChange)
        {
            st.MoveType = "Normal";
            st.HasAte = true;
            modifiers.Add($"{st.AttackerAbilityName}(ノーマルタイプ化)");
        }
    }

    /// <summary>壁（かわらわり・サイコファング・レイジングブルは壁を壊す。すりぬけは無視。壁は技の分類で決まる）。</summary>
    private void ResolveScreens(ModernState st)
    {
        // （Screen はオーロラベール・チャットの「壁」。物理・特殊の両方に効く）
        st.Reflect = st.Request.Reflect || st.Request.Screen;
        st.LightScreen = st.Request.LightScreen || st.Request.Screen;
        if (st.AttackerAbility == "infiltrator") st.Reflect = st.LightScreen = false;
        if ((st.Reflect || st.LightScreen) && st.MoveId != 0 && MoveEffects.BreaksScreens(st.Move, _generation))
        {
            st.Reflect = st.LightScreen = false;
            st.Modifiers.Add($"{st.Move.Name}(壁を壊す)");
        }
    }

    /// <summary>相性（きもったま・しんがんはノーマル・かくとう技がゴーストに当たる）。効果が無ければその結果を返す。</summary>
    private DamageResult? ResolveEffectiveness(ModernState st)
    {
        var modifiers = st.Modifiers;
        var defenderTypes = st.Defender.DefensiveTypes;
        var ghostRevealed = st.AttackerAbility is "scrappy" or "minds-eye";
        // ねらいのまと: 効果が無いタイプにも等倍で当たる
        var ringTarget = st.DefenderItem?.Id == ItemIdRingTarget && st.DefenderAbility != "klutz";
        var effectiveness = 1.0;
        foreach (var t in defenderTypes)
        {
            var m = _typeChart.Against(st.MoveType, t);
            if (m == 0 && ghostRevealed && t == "Ghost" && st.MoveType is "Normal" or "Fighting") m = 1;
            if (m == 0 && ringTarget)
            {
                m = 1;
                modifiers.Add($"{st.DefenderItem!.Name}(効果が無いタイプにも当たる)");
            }
            effectiveness *= m;
        }
        if (effectiveness == 0)
            return Empty(st.DefenderHp, 0, st.MoveType, "効果なし");
        if (ghostRevealed && defenderTypes.Contains("Ghost") && st.MoveType is "Normal" or "Fighting")
            modifiers.Add($"{st.AttackerAbilityName}(ゴーストに当たる)");

        // テラスシェル: HP が満タンなら、どの技もいまひとつ（0.5 倍）
        if (st.DefenderAbility == "tera-shell" && st.Defender.IsFullHp)
        {
            effectiveness = 0.5;
            modifiers.Add($"{st.DefenderAbilityName}(HP満タンでいまひとつ)");
        }
        st.Effectiveness = effectiveness;
        return null;
    }

    /// <summary>特性・持ち物・フィールドによる無効化。無効ならその結果を返す。</summary>
    private static DamageResult? CheckImmunity(ModernState st)
    {
        var immunity = AbilityEffects.ImmunityReason(st.DefenderAbility, st.MoveType, st.Move, st.Effectiveness);
        if (immunity is not null)
            return Empty(st.DefenderHp, 0, st.MoveType, $"{st.DefenderAbilityName}({immunity})");
        if (st.MoveType == "Ground" && st.DefenderItem?.Id == ItemIdAirBalloon)
            return Empty(st.DefenderHp, 0, st.MoveType, $"{st.DefenderItem.Name}(じめん技無効)");
        if (st.Move.Priority > 0 && !st.Attacker.IsDynamax && st.Terrain == Terrain.Psychic && st.DefenderGrounded)
            return Empty(st.DefenderHp, 0, st.MoveType, "サイコフィールド(地面にいる相手に先制技は出ない)");
        return null;
    }

    /// <summary>威力（技ごとの倍率、威力の補正、テラスタルの 60 保証）。</summary>
    private void ResolvePower(ModernState st, bool multiHit)
    {
        var move = st.Move;
        var attacker = st.Attacker;
        var defender = st.Defender;
        var modifiers = st.Modifiers;
        var power = move.Power;
        if (attacker.IsDynamax)
        {
            power = MaxMovePower(move);
            modifiers.Add($"ダイマックス技(威力{power})");
        }
        else if (move.Id == MoveEffects.MoveIdWeatherBall && MoveEffects.WeatherBallType(st.Weather) is not null
            || move.Id == MoveEffects.MoveIdTerrainPulse && st.AttackerGrounded && st.Terrain != Terrain.None)
            power *= 2;
        else if (MoveEffects.DoublesAgainstStatus(move, defender.Status))
        {
            power *= 2;
            modifiers.Add($"{move.Name}(相手が{StatusNames.Japanese(defender.Status)}で威力2倍)");
        }
        else if (move.Id == MoveIdRisingVoltage && st.Terrain == Terrain.Electric && st.DefenderGrounded)
        {
            power *= 2;
            modifiers.Add("ライジングボルト(エレキフィールドで威力2倍)");
        }
        else if (move.Id == MoveIdPsyblade && st.Terrain == Terrain.Electric)
        {
            power = power * 3 / 2;
            modifiers.Add("サイコブレイド(エレキフィールドで威力1.5倍)");
        }
        var target = st.Target;
        var bpMods = BasePowerModifiers(st.Request, attacker, defender, move, st.MoveType, power, st.HasAte, st.AttackerAbility, st.DefenderAbility,
            st.AttackerItem, st.DefenderItem, st.Weather, st.Terrain, st.AttackerGrounded, st.DefenderGrounded, st.Effectiveness, modifiers, ref target);
        st.Target = target;
        power = Math.Max(1, PokeRound(power * (double)LegacyDamageCalculator.Chain(bpMods, 41, 2097152) / 4096));
        // テラスタル: テラスタイプと同じタイプの技（元のタイプにもある・先制技・連続技でない）は威力 60 未満でも 60
        st.IsTeraStab = attacker.IsTerastallized && attacker.TeraType == st.MoveType;
        if (st.IsTeraStab && power < 60 && move.Priority <= 0 && !multiHit && _generation >= 9)
        {
            power = 60;
            modifiers.Add("テラスタル(威力60保証)");
        }
        st.Power = power;
    }

    /// <summary>攻撃側の実数値（イカサマは相手の攻撃とランク、ボディプレスは自分の防御とランク。メテオビームは溜めるときに特攻 +1）。</summary>
    private int ComputeAttackStat(ModernState st)
    {
        var attacker = st.Attacker;
        var move = st.Move;
        var modifiers = st.Modifiers;
        var attackSource = st.MoveId == MoveEffects.MoveIdFoulPlay ? st.Defender : attacker;
        if (st.MoveId == MoveEffects.MoveIdFoulPlay) modifiers.Add($"{move.Name}(相手のAで計算)");
        if (st.MoveId == MoveEffects.MoveIdBodyPress)
        {
            st.AttackStatKind = Stat.Defense;
            modifiers.Add($"{move.Name}(自分のBで計算)");
        }
        var attackStat = StatCalculator.Calculate(attackSource, st.AttackStatKind);
        var attackBoost = attackSource.Boosts[st.AttackStatKind];
        if (st.AttackerSeedBoost == st.AttackStatKind && attackSource == attacker) attackBoost = Math.Min(6, attackBoost + 1);
        if (MoveEffects.RaisesSpAttackFirst(move) && st.MoveId != 0)
        {
            attackBoost = Math.Min(6, attackBoost + 1);
            modifiers.Add($"{move.Name}(溜めるときにC+1)");
        }
        if (attackBoost != 0 && !(st.IsCritical && attackBoost < 0))
        {
            if (st.DefenderAbility == "unaware") modifiers.Add($"{st.DefenderAbilityName}(ランク無視)");
            else
            {
                attackStat = StatCalculator.ApplyBoost(attackStat, attackBoost);
                modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}");
            }
        }
        if (st.AttackerAbility == "hustle" && st.IsPhysical)
        {
            attackStat = PokeRound(attackStat * 3 / 2.0);
            modifiers.Add($"{st.AttackerAbilityName}(A1.5倍)");
        }
        var atMods = AttackModifiers(attacker, st.Defender, move, st.MoveType, st.IsPhysical, st.AttackerAbility, st.DefenderAbility, st.AttackerItem,
            st.Weather, st.Terrain, modifiers);
        return Math.Max(1, PokeRound(attackStat * (double)LegacyDamageCalculator.Chain(atMods, 410, 131072) / 4096));
    }

    /// <summary>防御側の実数値。</summary>
    private int ComputeDefenseStat(ModernState st)
    {
        var defender = st.Defender;
        var move = st.Move;
        var modifiers = st.Modifiers;
        var defenderTypes = defender.DefensiveTypes;
        var defenseStat = StatCalculator.Calculate(defender, st.DefenseStatKind);
        var defenseBoost = Math.Clamp(defender.Boosts[st.DefenseStatKind] + (st.DefenderSeedBoost == st.DefenseStatKind ? 1 : 0), -6, 6);
        if (defenseBoost != 0 && st.MoveId != 0 && MoveEffects.IgnoresDefenseBoosts(move))
        {
            modifiers.Add($"{move.Name}(相手のランク変化を無視)");
            defenseBoost = 0;
        }
        if (defenseBoost != 0 && !(st.IsCritical && defenseBoost > 0))
        {
            if (st.AttackerAbility == "unaware") modifiers.Add($"{st.AttackerAbilityName}(ランク無視)");
            else
            {
                defenseStat = StatCalculator.ApplyBoost(defenseStat, defenseBoost);
                modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}");
            }
        }
        if (st.Weather == Weather.Sand && !st.HitsPhysical && defenderTypes.Contains("Rock"))
        {
            defenseStat = PokeRound(defenseStat * 3 / 2.0);
            modifiers.Add("砂嵐(いわタイプのD1.5倍)");
        }
        // 剣盾の「あられ」にはこおりタイプの防御上昇が無い（第 9 世代の「雪」だけ）
        if (st.Weather == Weather.Snow && _generation >= 9 && st.HitsPhysical && defenderTypes.Contains("Ice"))
        {
            defenseStat = PokeRound(defenseStat * 3 / 2.0);
            modifiers.Add("雪(こおりタイプのB1.5倍)");
        }
        var dfMods = DefenseModifiers(st.Request, st.Attacker, defender, st.HitsPhysical, st.AttackerAbility, st.DefenderAbility, st.DefenderItem, st.Weather, st.Terrain, modifiers);
        return Math.Max(1, PokeRound(defenseStat * (double)LegacyDamageCalculator.Chain(dfMods, 410, 131072) / 4096));
    }

    /// <summary>基礎ダメージ（複数対象、おやこあいの 2 回目、天候、急所まで）。</summary>
    private int ComputeBaseDamage(ModernState st, int attackStat, int defenseStat, bool parentalChild)
    {
        var modifiers = st.Modifiers;
        var weather = st.Weather;
        var levelTerm = st.Attacker.Level * 2 / 5 + 2;
        var baseDamage = (int)((long)levelTerm * st.Power * attackStat / defenseStat / 50 + 2);
        if (st.Request.Format == BattleFormat.Doubles && st.Target != MoveTarget.Single)
        {
            baseDamage = PokeRound(baseDamage * 3072 / 4096.0);
            modifiers.Add("複数対象(0.75倍)");
        }

        // おやこあいの 2 回目は、ダメージが 1/4
        if (parentalChild) baseDamage = PokeRound(baseDamage * 1024 / 4096.0);

        // --- 天候
        if (weather == Weather.Sun && st.Move.Id == MoveIdHydroSteam)
        {
            baseDamage = PokeRound(baseDamage * 6144 / 4096.0);
            modifiers.Add("ハイドロスチーム(晴れで1.5倍)");
        }
        else if (weather == Weather.Sun && st.MoveType == "Fire" || weather == Weather.Rain && st.MoveType == "Water")
        {
            baseDamage = PokeRound(baseDamage * 6144 / 4096.0);
            modifiers.Add($"{FieldNames.Japanese(weather)}(1.5倍)");
        }
        else if (weather == Weather.Sun && st.MoveType == "Water" || weather == Weather.Rain && st.MoveType == "Fire")
        {
            baseDamage = PokeRound(baseDamage * 2048 / 4096.0);
            modifiers.Add($"{FieldNames.Japanese(weather)}(0.5倍)");
        }

        // --- 急所（スナイパーの 1.5 倍は最後の補正で掛ける）
        if (st.IsCritical)
        {
            baseDamage = (int)Math.Floor(baseDamage * 1.5);
            modifiers.Add("急所(1.5倍)");
        }
        return baseDamage;
    }

    /// <summary>タイプ一致（テラスタル中: 元のタイプの技も 1.5 倍。てきおうりょくは今のタイプ（テラスタイプ）と同じ技だけ）と、相性・やけどの表示。</summary>
    private static void ResolveStabAndBurn(ModernState st)
    {
        var attacker = st.Attacker;
        var isOriginalStab = attacker.Pokemon.HasType(st.MoveType);
        var stabMod = 4096;
        if (isOriginalStab) stabMod += 2048;
        if (st.IsTeraStab) stabMod += 2048;
        if (st.AttackerAbility == "adaptability" && (attacker.IsTerastallized ? st.IsTeraStab : isOriginalStab))
            stabMod += st.IsTeraStab && isOriginalStab ? 1024 : 2048;
        st.StabMod = stabMod;
        if (stabMod != 4096) st.Modifiers.Add($"タイプ一致({stabMod / 4096.0:0.##}倍)");

        if (st.Effectiveness != 1.0) st.Modifiers.Add($"タイプ相性{st.Effectiveness:0.##}倍");

        // やけど: 物理技 0.5 倍（こんじょう・からげんきは除く）
        st.ApplyBurn = attacker.Status == StatusCondition.Burn && st.IsPhysical && st.AttackerAbility != "guts" && st.Move.Id != MoveIdFacade;
        if (st.ApplyBurn) st.Modifiers.Add("やけど(物理0.5倍)");
    }

    /// <summary>最後の補正を掛けて 16 通りのダメージと結果を作る（2 発目以降はマルチスケイル・半減きのみが無くなる）。</summary>
    private DamageResult BuildModernResult(ModernState st, int baseDamage, int attackStat, int defenseStat)
    {
        var request = st.Request;
        var modifiers = st.Modifiers;
        var finalMod = LegacyDamageCalculator.Chain(FinalModifiers(request, st.Attacker, st.Defender, st.Move, st.MoveType, st.IsPhysical, st.Effectiveness, st.IsCritical,
            st.Reflect, st.LightScreen, st.AttackerAbility, st.DefenderAbility, st.AttackerItem, st.DefenderItem, modifiers, firstHit: true), 41, 131072);
        var laterMod = LegacyDamageCalculator.Chain(FinalModifiers(request, st.Attacker, st.Defender, st.Move, st.MoveType, st.IsPhysical, st.Effectiveness, st.IsCritical,
            st.Reflect, st.LightScreen, st.AttackerAbility, st.DefenderAbility, st.AttackerItem, st.DefenderItem, new List<string>(), firstHit: false), 41, 131072);

        int[] ComputeRolls(int fm)
        {
            var result = new int[16];
            for (var i = 0; i < 16; i++)
                result[i] = LegacyDamageCalculator.FinalDamage(baseDamage, i, st.Effectiveness, st.ApplyBurn, st.StabMod, fm);
            return result;
        }
        var rolls = ComputeRolls(finalMod);
        var laterRolls = laterMod == finalMod ? rolls : ComputeRolls(laterMod);
        var koOptions = KnockOutOptions.For(request, st.Defender, st.DefenderHp, st.DefenderGrounded, laterRolls, modifiers);

        return new DamageResult
        {
            Rolls = rolls,
            LaterRolls = laterRolls,
            DefenderHP = st.DefenderHp,
            TypeEffectiveness = st.Effectiveness,
            IsStab = st.StabMod != 4096,
            AttackStat = attackStat,
            DefenseStat = defenseStat,
            BasePower = st.Power,
            MoveType = st.MoveType,
            KnockOut = KnockOutCalculator.Calculate(rolls, st.DefenderHp, koOptions),
            Modifiers = modifiers,
        };
    }

    /// <summary>
    /// 技を、<paramref name="template"/> の条件（攻撃側・防御側・天候・フィールドなど）で与える最大ダメージの多い順に並べる。
    /// 同じダメージなら元の並び順のまま。変化技や効果が無い技は最後。
    /// </summary>
    public IReadOnlyList<Move> SortByDamage(DamageRequest template, IEnumerable<Move> moves) =>
        moves
            .Select(m => (Move: m, Damage: m.IsDamaging ? Calculate(template.With(move: m)).MaxDamage : -1))
            .OrderByDescending(x => x.Damage)
            // 同じダメージなら、遅れて当たる技（みらいよち・はめつのねがい）は後ろにする
            .ThenBy(x => x.Move.Id is MoveIdFutureSight or MoveIdDoomDesire ? 1 : 0)
            .Select(x => x.Move)
            .ToList();

    /// <summary>防御側を決めていないときの仮の相手（タイプなし・種族値すべて 100・努力値なし）。物理と特殊を公平に比べるため。</summary>
    public static PokemonBuild NeutralDefender(EvSystem evSystem) =>
        new(new Pokemon { Name = "（防御側なし）", BaseStats = new StatSet(100, 100, 100, 100, 100, 100) }, evSystem);

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

    /// <summary>地面にいるか（ひこうタイプ・ふゆう・エレベート・ふうせんは浮いている）。</summary>
    private static bool IsGrounded(PokemonBuild build, string ability, Item? item)
    {
        if (ability is "levitate" or "eelevate") return false;
        if (item?.Id == ItemIdAirBalloon) return false;
        return !build.DefensiveTypes.Contains("Flying");
    }

    /// <summary>フィールドのシードがそのフィールドで上げる能力（発動しなければ null）。</summary>
    private static Stat? SeedStat(Item? item, Terrain terrain) => (item?.Id, terrain) switch
    {
        (ItemIdElectricSeed, Terrain.Electric) or (ItemIdGrassySeed, Terrain.Grassy) => Stat.Defense,
        (ItemIdPsychicSeed, Terrain.Psychic) or (ItemIdMistySeed, Terrain.Misty) => Stat.SpDefense,
        _ => null,
    };

    /// <summary>ちからのハチマキ・ものしりメガネ（物理・特殊の技の威力 1.1 倍）。</summary>
    private static bool IsCategoryBoostItem(Item? item, bool physical) =>
        item is { Category: not ItemCategory.TypeBoost } && item.TypeBoost == (physical ? "Physical" : "Special");

    // 威力の補正（@smogon/calc の calculateBPModsSMSSSV と同じ順番）
    private List<int> BasePowerModifiers(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType,
        int basePower, bool hasAte, string attackerAbility, string defenderAbility, Item? attackerItem, Item? defenderItem,
        Weather weather, Terrain terrain, bool attackerGrounded, bool defenderGrounded, double effectiveness, List<string> modifiers, ref MoveTarget target)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        // ダイマックス技は技が置き換わるので、技ごとの補正（はたきおとす・ソーラービームなど）・ちからずく・すてみは掛からない
        var moveId = attacker.IsDynamax ? 0 : move.Id;

        if (moveId == MoveIdFacade && attacker.Status is StatusCondition.Burn or StatusCondition.Poison or StatusCondition.BadlyPoisoned or StatusCondition.Paralysis)
            Add(8192, "からげんき(状態異常で2倍)");
        else if (moveId == MoveEffects.MoveIdBrine && defender.CurrentHp * 2 <= defender.MaxHp)
            Add(8192, "しおみず(相手のHPが半分以下で2倍)");
        else if (moveId == MoveEffects.MoveIdVenoshock && defender.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "ベノムショック(相手がどくで2倍)");
        else if (moveId == MoveIdExpandingForce && attackerGrounded && terrain == Terrain.Psychic)
        {
            target = MoveTarget.AllFoes;
            Add(6144, "ワイドフォース(サイコフィールドで1.5倍・全体)");
        }
        else if (moveId == MoveIdKnockOff && MoveEffects.KnockOffBoosted(defenderItem, defender))
            Add(6144, "はたきおとす(持ち物があるので1.5倍)");
        else if (moveId == MoveIdMistyExplosion && attackerGrounded && terrain == Terrain.Misty)
            Add(6144, "ミストバースト(ミストフィールドで1.5倍)");
        else if (moveId is MoveIdSolarBeam or MoveIdSolarBlade && weather is Weather.Rain or Weather.Sand or Weather.Snow)
            Add(2048, $"{move.Name}({FieldNames.Japanese(weather, _generation)}で0.5倍)");
        else if (moveId is MoveEffects.MoveIdCollisionCourse or MoveEffects.MoveIdElectroDrift && effectiveness >= 2)
            Add(5461, $"{move.Name}(効果抜群で1.33倍)");

        // フィールド（第 8 世代から 1.3 倍）
        if (attackerGrounded && (terrain == Terrain.Electric && moveType == "Electric" || terrain == Terrain.Grassy && moveType == "Grass"
            || terrain == Terrain.Psychic && moveType == "Psychic"))
            Add(5325, $"{FieldNames.Japanese(terrain)}(1.3倍)");
        if (defenderGrounded && terrain == Terrain.Misty && moveType == "Dragon")
            Add(2048, "ミストフィールド(ドラゴン技0.5倍)");
        else if (defenderGrounded && terrain == Terrain.Grassy && moveId is MoveIdEarthquake or MoveIdMagnitude or MoveIdBulldoze)
            Add(2048, "グラスフィールド(じしん系0.5倍)");

        if (attackerAbility == "technician" && basePower <= 60 || attackerAbility == "mega-launcher" && move.HasFlag("Pulse")
            || attackerAbility == "strong-jaw" && move.HasFlag("Bite") || attackerAbility == "steely-spirit" && moveType == "Steel"
            || attackerAbility == "sharpness" && MoveEffects.IsSlicing(move))
            Add(6144, $"{atkName}(1.5倍)");

        if (attackerAbility == "sheer-force" && !attacker.IsDynamax && MoveEffects.SheerForceBoosts(move, _generation)
            || attackerAbility == "sand-force" && weather == Weather.Sand && moveType is "Rock" or "Ground" or "Steel"
            || attackerAbility == "tough-claws" && move.IsContact || attackerAbility == "punk-rock" && move.IsSound)
            Add(5325, $"{atkName}(1.3倍)");

        if (hasAte && !attacker.IsDynamax) Add(4915, $"{atkName}(1.2倍)");
        if (attackerAbility == "reckless" && !attacker.IsDynamax && MoveEffects.IsRecoil(move) || attackerAbility == "iron-fist" && move.HasFlag("Punch"))
            Add(4915, $"{atkName}(1.2倍)");

        if (_generation <= 8 && defenderAbility == "heatproof" && moveType == "Fire") Add(2048, $"{defName}(0.5倍)");
        else if (defenderAbility == "dry-skin" && moveType == "Fire") Add(5120, $"{defName}(1.25倍)");

        // 持ち物（ジュエル 1.3 倍 / タイプ強化 1.2 倍 / ちからのハチマキ・ものしりメガネ 4505 / パンチグローブ 4506 / オーガポンのお面 4915）
        if (MoveEffects.GemType(attackerItem) == moveType)
            Add(5325, $"{attackerItem!.Name}(1.3倍)");
        else if (attackerItem is { Category: ItemCategory.TypeBoost } typeItem && typeItem.TypeBoost == moveType)
            Add(PokeRound(4096 * typeItem.TypeBoostMultiplier), $"{typeItem.Name}(威力{typeItem.TypeBoostMultiplier:0.##}倍)");
        else if (IsCategoryBoostItem(attackerItem, move.Category == MoveCategory.Physical))
            Add(4505, $"{attackerItem!.Name}(1.1倍)");
        else if (attackerItem?.Id == ItemIdPunchingGlove && move.HasFlag("Punch"))
            Add(4506, $"{attackerItem.Name}(1.1倍)");
        else if (MoveEffects.OgerponMaskBoosts(attacker.Pokemon, attackerItem))
            Add(4915, $"{attackerItem!.Name}(1.2倍)");
        return mods;
    }

    // 攻撃の補正（@smogon/calc の calculateAtModsSMSSSV と同じ順番）
    private List<int> AttackModifiers(PokemonBuild attacker, PokemonBuild defender, Move move, string moveType, bool isPhysical,
        string attackerAbility, string defenderAbility, Item? attackerItem, Weather weather, Terrain terrain, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var statName = isPhysical ? "A" : "C";
        // もうか・しんりょく・げきりゅう・むしのしらせ: HP 1/3 以下で対応タイプの技の攻撃・特攻 1.5 倍
        var pinch = attacker.CurrentHp * 3 <= attacker.MaxHp && (attackerAbility == "blaze" && moveType == "Fire"
            || attackerAbility == "overgrow" && moveType == "Grass" || attackerAbility == "torrent" && moveType == "Water"
            || attackerAbility == "swarm" && moveType == "Bug");

        if (attackerAbility == "defeatist" && attacker.CurrentHp * 2 <= attacker.MaxHp)
            Add(2048, $"{atkName}(HP半分以下で{statName}0.5倍)");
        else if (attackerAbility == "solar-power" && weather == Weather.Sun && !isPhysical)
            Add(6144, $"{atkName}({statName}1.5倍)");
        else if (attackerAbility == "gorilla-tactics" && isPhysical && !attacker.IsDynamax)
            Add(6144, $"{atkName}({statName}1.5倍)");
        else if (attackerAbility == "guts" && attacker.Status != StatusCondition.None && isPhysical)
            Add(6144, $"{atkName}(状態異常で{statName}1.5倍)");
        else if (attackerAbility == "toxic-boost" && isPhysical && attacker.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned
                 || attackerAbility == "flare-boost" && !isPhysical && attacker.Status == StatusCondition.Burn)
            Add(6144, $"{atkName}({StatusNames.Japanese(attacker.Status)}で{statName}1.5倍)");
        else if (pinch)
            Add(6144, $"{atkName}(HP1/3以下 1.5倍)");
        else if (attackerAbility == "steelworker" && moveType == "Steel" || attackerAbility == "dragons-maw" && moveType == "Dragon"
            || attackerAbility == "rocky-payload" && moveType == "Rock" || attackerAbility == "fire-mane" && moveType == "Fire")
            Add(6144, $"{atkName}({statName}1.5倍)");
        else if (attackerAbility == "transistor" && moveType == "Electric")
            Add(_generation >= 9 ? 5325 : 6144, $"{atkName}({statName}{(_generation >= 9 ? "1.3" : "1.5")}倍)");
        else if (attackerAbility == "water-bubble" && moveType == "Water" || attackerAbility is "huge-power" or "pure-power" && isPhysical)
            Add(8192, $"{atkName}({statName}2倍)");

        // 防御側の特性で攻撃側の能力が下がるもの
        if (defenderAbility == "thick-fat" && moveType is "Fire" or "Ice" || defenderAbility == "water-bubble" && moveType == "Fire"
            || defenderAbility == "purifying-salt" && moveType == "Ghost")
            Add(2048, $"{defName}({TypeNames.ToJapanese(moveType)}技の{statName}0.5倍)");
        if (_generation >= 9 && defenderAbility == "heatproof" && moveType == "Fire")
            Add(2048, $"{defName}(ほのお技の{statName}0.5倍)");
        // わざわいのおふだ（物理）・わざわいのうつわ（特殊）: 相手の攻撃・特攻 0.75 倍（同じ特性同士は効かない）
        if (defenderAbility == "tablets-of-ruin" && attackerAbility != "tablets-of-ruin" && isPhysical
            || defenderAbility == "vessel-of-ruin" && attackerAbility != "vessel-of-ruin" && !isPhysical)
            Add(3072, $"{defName}({statName}0.75倍)");

        if (attackerAbility is "protosynthesis" or "quark-drive" && ParadoxBoostActive(weather, terrain, attackerItem, attackerAbility)
            && HighestStat(attacker) == (isPhysical ? Stat.Attack : Stat.SpAttack))
            Add(5325, $"{atkName}({statName}1.3倍)");
        if (attackerAbility == "hadron-engine" && !isPhysical && terrain == Terrain.Electric
            || attackerAbility == "orichalcum-pulse" && isPhysical && weather == Weather.Sun)
            Add(5461, $"{atkName}({statName}1.33倍)");

        if (attackerItem is { } item)
        {
            if (item.Category == ItemCategory.ThickClub && isPhysical && attacker.Pokemon.SpeciesId is 104 or 105
                || item.Category == ItemCategory.LightBall && attacker.Pokemon.SpeciesId == 25)
                Add(8192, $"{item.Name}({statName}2倍)");
            else if (!attacker.IsDynamax && (isPhysical ? item.AttackMultiplier : item.SpAttackMultiplier) is var itemMod && itemMod != 1.0
                && item.Category is not (ItemCategory.ThickClub or ItemCategory.LightBall))
                Add(PokeRound(4096 * itemMod), $"{item.Name}({statName}{itemMod:0.##}倍)");
        }
        return mods;
    }

    // 防御の補正（@smogon/calc の calculateDfModsSMSSSV と同じ順番）
    private static List<int> DefenseModifiers(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, bool hitsPhysical,
        string attackerAbility, string defenderAbility, Item? defenderItem, Weather weather, Terrain terrain, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var statName = hitsPhysical ? "B" : "D";

        if (defenderAbility == "marvel-scale" && defender.Status != StatusCondition.None && hitsPhysical)
            Add(6144, $"{defName}(状態異常で{statName}1.5倍)");
        else if (defenderAbility == "grass-pelt" && terrain == Terrain.Grassy && hitsPhysical)
            Add(6144, $"{defName}({statName}1.5倍)");
        else if (defenderAbility == "fur-coat" && hitsPhysical)
            Add(8192, $"{defName}({statName}2倍)");

        // わざわいのつるぎ（物理）・わざわいのたま（特殊）: 相手の防御・特防 0.75 倍（同じ特性同士は効かない）
        if (attackerAbility == "sword-of-ruin" && defenderAbility != "sword-of-ruin" && hitsPhysical
            || attackerAbility == "beads-of-ruin" && defenderAbility != "beads-of-ruin" && !hitsPhysical)
            Add(3072, $"{atkName}(相手の{statName}0.75倍)");

        if (defenderAbility is "protosynthesis" or "quark-drive" && ParadoxBoostActive(weather, terrain, defenderItem, defenderAbility)
            && HighestStat(defender) == (hitsPhysical ? Stat.Defense : Stat.SpDefense))
            Add(5324, $"{defName}({statName}1.3倍)");

        if (defenderItem is { Category: ItemCategory.Eviolite } && defender.Pokemon.NotFullyEvolved
            || defenderItem is { Category: ItemCategory.AssaultVest } && !hitsPhysical)
            Add(6144, $"{defenderItem.Name}({statName}1.5倍)");
        else if (defenderItem is { Category: not (ItemCategory.Eviolite or ItemCategory.AssaultVest) } other
            && (hitsPhysical ? other.DefenseMultiplier : other.SpDefenseMultiplier) is var itemMod && itemMod != 1.0)
            Add(PokeRound(4096 * itemMod), $"{other.Name}({statName}{itemMod:0.##}倍)");
        return mods;
    }

    // 最後の補正（@smogon/calc の calculateFinalModsSMSSSV と同じ順番）。
    // firstHit: 1 発目なら true。マルチスケイル（満タン時のみ）と半減きのみ（1 回だけ）は 1 発目にだけ効く。
    private List<int> FinalModifiers(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType, bool isPhysical,
        double effectiveness, bool isCritical, bool reflect, bool lightScreen, string attackerAbility, string defenderAbility,
        Item? attackerItem, Item? defenderItem, List<string> modifiers, bool firstHit)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var doubles = request.Format == BattleFormat.Doubles;
        var wallMod = doubles ? 2732 : 2048;
        var wallText = doubles ? "ダブル 0.67倍" : "0.5倍";

        // 壁: 急所には効かない。リフレクターは物理技・ひかりのかべは特殊技（サイコショックは特殊技なのでひかりのかべ）
        if (!isCritical && reflect && isPhysical) Add(wallMod, $"リフレクター({wallText})");
        else if (!isCritical && lightScreen && !isPhysical) Add(wallMod, $"ひかりのかべ({wallText})");

        if (attackerAbility == "neuroforce" && effectiveness > 1) Add(5120, $"{atkName}(1.25倍)");
        else if (attackerAbility == "sniper" && isCritical) Add(6144, $"{atkName}(急所に1.5倍)");
        else if (attackerAbility == "tinted-lens" && effectiveness < 1) Add(8192, $"{atkName}(2倍)");

        if (defenderAbility is "multiscale" or "shadow-shield" && firstHit && defender.IsFullHp) Add(2048, $"{defName}(HP満タン 0.5倍)");
        // もふもふ・オーラガード: 接触技 0.5 倍（もふもふのほのお技 2 倍は別に掛ける）
        if (defenderAbility is "fluffy" or "aura-guard" && move.IsContact && !(attacker.IsDynamax)) Add(2048, $"{defName}(接触技0.5倍)");
        else if (defenderAbility == "punk-rock" && move.IsSound) Add(2048, $"{defName}(音技0.5倍)");
        else if (defenderAbility == "ice-scales" && !isPhysical) Add(2048, $"{defName}(特殊技0.5倍)");
        if (defenderAbility is "filter" or "solid-rock" or "prism-armor" && effectiveness > 1) Add(3072, $"{defName}(0.75倍)");
        if (defenderAbility == "fluffy" && moveType == "Fire") Add(8192, $"{defName}(ほのお技2倍)");

        if (attackerItem is { Category: ItemCategory.ExpertBelt } && effectiveness > 1) Add(4915, $"{attackerItem.Name}(1.2倍)");
        else if (attackerItem is { Category: ItemCategory.LifeOrb }) Add(5324, $"{attackerItem.Name}(1.3倍)");
        else if (attackerItem is { Category: not (ItemCategory.ExpertBelt or ItemCategory.LifeOrb) } other && other.DamageMultiplier != 1.0)
            Add(PokeRound(4096 * other.DamageMultiplier), $"{other.Name}({other.DamageMultiplier:0.##}倍)");

        // 半減きのみ（1 回だけ）。ホズのみはノーマル技なら相性に関係なく半減。きんちょうかん・じんばいったいの相手には使えない
        if (firstHit && defenderItem is { Category: ItemCategory.ResistBerry } berry && berry.TypeBoost == moveType
            && (effectiveness > 1 || moveType == "Normal") && attackerAbility is not ("unnerve" or "as-one-glastrier" or "as-one-spectrier"))
            Add(2048, $"{berry.Name}(1発目のみ 0.5倍)");
        return mods;
    }

    private static bool ParadoxBoostActive(Weather weather, Terrain terrain, Item? item, string ability)
    {
        if (item?.Id == ItemIdBoosterEnergy) return true;
        return ability == "protosynthesis" ? weather == Weather.Sun : terrain == Terrain.Electric;
    }

    /// <summary>ランク込みで一番高い能力（こだいかっせい・クォークチャージで上がる能力）。</summary>
    public static Stat HighestStat(PokemonBuild build)
    {
        var stats = StatCalculator.CalculateAll(build);
        var best = Stat.Attack;
        foreach (var stat in new[] { Stat.Attack, Stat.Defense, Stat.SpAttack, Stat.SpDefense, Stat.Speed })
        {
            if (StatCalculator.ApplyBoost(stats[stat], build.Boosts[stat]) > StatCalculator.ApplyBoost(stats[best], build.Boosts[best]))
                best = stat;
        }
        return best;
    }

    /// <summary>五捨五超入（0.5 ちょうどは切り捨て）。</summary>
    public static int PokeRound(double value)
    {
        var floor = Math.Floor(value);
        return value - floor > 0.5 ? (int)floor + 1 : (int)floor;
    }
}

/// <summary>特性の分類テーブル。識別子は PokeAPI のもの。</summary>
public static class AbilityEffects
{
    /// <summary>かたやぶり・テラボルテージ・ターボブレイズ（相手の特性のうち <see cref="IsIgnorable"/> なものを無視して攻撃する）。</summary>
    public static bool IgnoresTargetAbility(string ability) =>
        ability is "mold-breaker" or "teravolt" or "turboblaze";

    /// <summary>
    /// かたやぶり系で無視される防御側の特性（この計算機・タイプ相性表で効果を扱うもの）。
    /// シャドーシールド・プリズムアーマー・ファントムガードなどは無視できない。
    /// </summary>
    public static bool IsIgnorable(string ability) => ability is
        "battle-armor" or "shell-armor" or "bulletproof" or "dry-skin" or "earth-eater" or "eelevate" or "filter" or "flash-fire" or
        "fluffy" or "fur-coat" or "grass-pelt" or "heatproof" or "ice-scales" or "levitate" or "lightning-rod" or "marvel-scale" or
        "minds-eye" or "motor-drive" or "multiscale" or "punk-rock" or "purifying-salt" or "sap-sipper" or "solid-rock" or "soundproof" or
        "storm-drain" or "tera-shell" or "thick-fat" or "unaware" or "volt-absorb" or "water-absorb" or "water-bubble" or "well-baked-body" or
        "wind-rider" or "wonder-guard" or "aura-guard" or "queenly-majesty" or "dazzling" or "armor-tail";

    /// <summary>ノーてんき・エアロック（場の天候の効果を無くす）。</summary>
    public static bool NegatesWeather(string ability) => ability is "cloud-nine" or "air-lock";

    /// <summary>ノーマル技を別タイプに変える特性。</summary>
    public static string? SkinType(string ability) => ability switch
    {
        "pixilate" => "Fairy",
        "aerilate" => "Flying",
        "refrigerate" => "Ice",
        "galvanize" => "Electric",
        _ => null,
    };

    /// <summary>防御側の特性で技が無効になる場合、その理由。無効でなければ null。</summary>
    public static string? ImmunityReason(string ability, string moveType, Move move, double effectiveness) => ability switch
    {
        "levitate" or "earth-eater" or "eelevate" when moveType == "Ground" => "じめん技無効",
        "water-absorb" or "storm-drain" or "dry-skin" when moveType == "Water" => "みず技無効",
        "flash-fire" or "well-baked-body" when moveType == "Fire" => "ほのお技無効",
        "volt-absorb" or "lightning-rod" or "motor-drive" when moveType == "Electric" => "でんき技無効",
        "sap-sipper" when moveType == "Grass" => "くさ技無効",
        "soundproof" when move.IsSound => "音技無効",
        "bulletproof" when move.HasFlag("Bullet") => "弾技無効",
        "wonder-guard" when effectiveness <= 1 => "効果抜群以外無効",
        "queenly-majesty" or "dazzling" or "armor-tail" when move.Priority > 0 => "先制技無効",
        "wind-rider" when MoveEffects.IsWind(move) => "風技無効",
        _ => null,
    };

    /// <summary>
    /// 攻撃側として持っていると、この計算機でダメージが上がる特性（条件付きのものも含む）。
    /// ピンチ特性（もうか等）・こんじょう・スナイパーのように、状態異常・HP・急所が揃わないと効かないものは含めない。
    /// </summary>
    public static bool IsOffensive(string? ability) => ability is
        "huge-power" or "pure-power" or "adaptability" or "technician" or "sheer-force" or "tough-claws" or
        "pixilate" or "aerilate" or "refrigerate" or "galvanize" or "iron-fist" or "strong-jaw" or "mega-launcher" or
        "punk-rock" or "sand-force" or "steelworker" or "steely-spirit" or "transistor" or "dragons-maw" or
        "rocky-payload" or "water-bubble" or "sharpness" or "reckless" or "hustle" or "gorilla-tactics" or
        "solar-power" or "orichalcum-pulse" or "hadron-engine" or "protosynthesis" or "quark-drive" or
        "tinted-lens" or "neuroforce" or "fire-mane" or "sword-of-ruin" or "beads-of-ruin" or "scrappy" or "minds-eye";

    /// <summary>場に出ると天候・フィールドを作る特性（ひでり・サイコメイカー・デルタストリームなど）。</summary>
    public static bool SetsField(string? ability) =>
        FieldEffects.WeatherFromAbility(ability) is not null || FieldEffects.TerrainFromAbility(ability) is not null || ability == "delta-stream";

    /// <summary>既定の特性を選ぶときの立場。</summary>
    public enum Side
    {
        /// <summary>攻撃側（ダメージが上がる特性を優先）。</summary>
        Attacker,
        /// <summary>防御側（ちょすい・あついしぼうなど受けで効く特性を優先）。</summary>
        Defender,
        /// <summary>どちらにもなる（タイプ相性表など）。使用率 1 位が攻撃・受けどちらかで効く特性ならそのまま使う。</summary>
        Either,
    }

    /// <summary>
    /// ポケモンを選んだときの既定の特性。天候・フィールドを作る特性か、<paramref name="side"/> で効く特性
    /// （攻撃側はダメージが上がる特性、防御側は受けで効く特性）を持っていればそれを、
    /// 無ければ使用率 1 位（<paramref name="usageTop"/>。無ければ第 1 特性）を選ぶ。
    /// 使用率 1 位がもともとそういう特性ならそのまま使う（防御側のトリトドンはよびみずのまま）。
    /// </summary>
    public static Ability? ChooseDefault(IReadOnlyList<Ability> abilities, Ability? usageTop, Side side = Side.Attacker)
    {
        bool Offensive(Ability a) => side != Side.Defender && IsOffensive(a.Identifier);
        bool Defensive(Ability a) => side != Side.Attacker && IsDefensive(a.Identifier);
        if (usageTop is not null && (SetsField(usageTop.Identifier) || Offensive(usageTop) || Defensive(usageTop))) return usageTop;
        // 天候・フィールドを作る特性を優先（技の並び順や場の状態にも効くため）
        return abilities.FirstOrDefault(a => SetsField(a.Identifier))
            ?? abilities.FirstOrDefault(Offensive)
            ?? abilities.FirstOrDefault(Defensive)
            ?? usageTop
            ?? abilities.FirstOrDefault();
    }

    /// <summary>防御側に付くのが自然な特性（チャットコマンドの振り分けに使う）。</summary>
    public static bool IsDefensive(string ability) => ability is
        "levitate" or "earth-eater" or "water-absorb" or "storm-drain" or "dry-skin" or "flash-fire" or "well-baked-body" or
        "volt-absorb" or "lightning-rod" or "motor-drive" or "sap-sipper" or "soundproof" or "bulletproof" or "wonder-guard" or
        "multiscale" or "shadow-shield" or "filter" or "solid-rock" or "prism-armor" or "ice-scales" or "fur-coat" or
        "grass-pelt" or "thick-fat" or "heatproof" or "fluffy" or "purifying-salt" or "unaware" or "marvel-scale" or
        "eelevate" or "aura-guard" or "tera-shell" or "tablets-of-ruin" or "vessel-of-ruin" or "battle-armor" or "shell-armor" or
        "queenly-majesty" or "dazzling" or "armor-tail" or "wind-rider";
}

/// <summary>乱数 16 通りから確定数と確率を求める。防御側の状態異常による定数ダメージ（やけど・どく・もうどく）を織り込む。回復は考慮しない。</summary>
/// <summary>確定数の計算条件（開始 HP・定数ダメージ・回復）。</summary>
public sealed class KnockOutOptions
{
    /// <summary>計算開始時の HP（0 なら最大 HP）。</summary>
    public int StartHp { get; init; }
    public StatusCondition Status { get; init; }
    /// <summary>2 発目以降の乱数（マルチスケイル・半減きのみが消えた後）。null なら 1 発目と同じ。</summary>
    public int[]? LaterRolls { get; init; }
    /// <summary>毎ターン終了時の HP 増減（たべのこし +1/16、くろいヘドロ -1/8 など）。</summary>
    public int EndOfTurnHeal { get; init; }
    /// <summary>回復きのみが発動する HP（この値以下）。0 ならきのみなし。</summary>
    public int BerryThreshold { get; init; }
    public int BerryHeal { get; init; }

    public static KnockOutOptions Default { get; } = new();

    /// <summary>防御側の持ち物・状態・フィールドから条件を組み立て、反映した内容を modifiers に書き足す。</summary>
    /// <summary>計算した世代（やけどの定数ダメージ・回復きのみの量が変わる）。</summary>
    public int Generation { get; init; } = 9;

    public static KnockOutOptions For(DamageRequest request, PokemonBuild defender, int maxHp, bool grounded, int[] laterRolls, List<string> modifiers, int generation = 9)
    {
        var start = Math.Min(maxHp, defender.CurrentHp);
        if (start < maxHp) modifiers.Add($"防御側の残りHP {start}/{maxHp}");
        var eot = 0;
        var threshold = 0;
        var heal = 0;
        var sixteenth = Math.Max(1, maxHp / 16);
        if (request.Terrain == Terrain.Grassy && grounded && generation >= 6)
        {
            eot += sixteenth;
            modifiers.Add("グラスフィールド(毎ターン1/16回復)を確定数に反映");
        }
        switch (defender.Item)
        {
            case { Category: ItemCategory.Leftovers } item:
                eot += sixteenth;
                modifiers.Add($"{item.Name}(毎ターン1/16回復)を確定数に反映");
                break;
            case { Category: ItemCategory.BlackSludge } item when defender.DefensiveTypes.Contains("Poison") || defender.Pokemon.HasType("Poison"):
                eot += sixteenth;
                modifiers.Add($"{item.Name}(毎ターン1/16回復)を確定数に反映");
                break;
            case { Category: ItemCategory.BlackSludge } item:
                eot -= Math.Max(1, maxHp / 8);
                modifiers.Add($"{item.Name}(毎ターン1/8ダメージ)を確定数に反映");
                break;
            case { Category: ItemCategory.SitrusBerry } item when generation <= 3:
                // 第 3 世代: HP 1/2 以下で 30 回復
                threshold = maxHp / 2;
                heal = 30;
                modifiers.Add($"{item.Name}(HP1/2以下で30回復)を確定数に反映");
                break;
            case { Category: ItemCategory.SitrusBerry } item:
                threshold = maxHp / 2;
                heal = Math.Max(1, maxHp / 4);
                modifiers.Add($"{item.Name}(HP1/2以下で1/4回復)を確定数に反映");
                break;
            case { Category: ItemCategory.OranBerry } item:
                threshold = maxHp / 2;
                heal = 10;
                modifiers.Add($"{item.Name}(HP1/2以下で10回復)を確定数に反映");
                break;
            case { Category: ItemCategory.PinchBerry } item when generation <= 6:
                // 第 6 世代まで: HP 1/2 以下で 1/8 回復
                threshold = maxHp / 2;
                heal = Math.Max(1, maxHp / 8);
                modifiers.Add($"{item.Name}(HP1/2以下で1/8回復)を確定数に反映");
                break;
            case { Category: ItemCategory.PinchBerry } item when generation == 7:
                threshold = maxHp / 4;
                heal = Math.Max(1, maxHp / 2);
                modifiers.Add($"{item.Name}(HP1/4以下で1/2回復)を確定数に反映");
                break;
            case { Category: ItemCategory.PinchBerry } item:
                threshold = maxHp / 4;
                heal = Math.Max(1, maxHp / 3);
                modifiers.Add($"{item.Name}(HP1/4以下で1/3回復)を確定数に反映");
                break;
        }
        // ポイズンヒール: どく・もうどくで毎ターン 1/8 回復（定数ダメージの代わり）。マジックガード: 状態異常の定数ダメージを受けない
        var residualStatus = defender.Status;
        var defenderAbility = defender.Ability?.Identifier;
        if (defenderAbility == "poison-heal" && defender.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned)
        {
            eot += Math.Max(1, maxHp / 8);
            residualStatus = StatusCondition.None;
            modifiers.Add($"{defender.Ability!.Name}(毎ターン1/8回復)を確定数に反映");
        }
        else if (defenderAbility == "magic-guard" && defender.Status != StatusCondition.None)
        {
            residualStatus = StatusCondition.None;
            modifiers.Add($"{defender.Ability!.Name}(状態異常の定数ダメージなし)を確定数に反映");
        }
        return new KnockOutOptions
        {
            StartHp = start,
            Status = residualStatus,
            LaterRolls = laterRolls,
            EndOfTurnHeal = eot,
            BerryThreshold = threshold,
            BerryHeal = heal,
            Generation = generation,
        };
    }
}

public static class KnockOutCalculator
{
    public const int MaxHits = 10;

    public static KnockOut Calculate(int[] rolls, int hp) => Calculate(rolls, hp, KnockOutOptions.Default);

    public static KnockOut Calculate(int[] rolls, int hp, StatusCondition defenderStatus) =>
        Calculate(rolls, hp, new KnockOutOptions { Status = defenderStatus });

    /// <summary>
    /// 何発で倒せるかと、その確率。残り HP（と回復きのみを使ったか）の分布を 1 発ずつ進めて厳密に求める。
    /// 1 発ごとに「攻撃 → きのみ → ターン終了時の回復 → 状態異常の定数ダメージ → きのみ」の順で進める。
    /// </summary>
    public static KnockOut Calculate(int[] rolls, int hp, KnockOutOptions options)
    {
        if (rolls.Length == 0 || rolls[^1] <= 0 || hp <= 0)
            return new KnockOut(0, 0);

        var start = options.StartHp > 0 ? Math.Min(options.StartHp, hp) : hp;
        // キー: 残り HP * 2 + (きのみを使ったら 1)
        var dist = new Dictionary<int, double> { [start * 2] = 1.0 };
        var knockedOut = 0.0;
        var perRoll = 1.0 / rolls.Length;
        for (var hits = 1; hits <= MaxHits; hits++)
        {
            var hitRolls = hits == 1 ? rolls : options.LaterRolls ?? rolls;
            var residual = StatusNames.ResidualDamage(options.Status, hp, hits, options.Generation);
            var next = new Dictionary<int, double>();
            foreach (var (key, p) in dist)
            {
                var remaining = key / 2;
                var usedBerry = (key & 1) == 1;
                foreach (var roll in hitRolls)
                {
                    var r = remaining - roll;
                    var used = usedBerry;
                    if (r <= 0) { knockedOut += p * perRoll; continue; }
                    TryBerry(ref r, ref used);
                    r = Math.Min(hp, r + options.EndOfTurnHeal);
                    if (r <= 0) { knockedOut += p * perRoll; continue; }
                    r -= residual;
                    if (r <= 0) { knockedOut += p * perRoll; continue; }
                    TryBerry(ref r, ref used);
                    var k = r * 2 + (used ? 1 : 0);
                    next[k] = next.GetValueOrDefault(k) + p * perRoll;
                }
            }
            dist = next;
            if (knockedOut > 1e-9)
                return new KnockOut(hits, Math.Min(1.0, knockedOut));
        }
        return new KnockOut(MaxHits + 1, 0);

        void TryBerry(ref int r, ref bool used)
        {
            if (used || options.BerryThreshold <= 0 || r > options.BerryThreshold) return;
            r = Math.Min(hp, r + options.BerryHeal);
            used = true;
        }
    }
}
