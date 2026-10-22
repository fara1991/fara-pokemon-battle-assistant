using System.Text.RegularExpressions;
using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Text;

public sealed class DamageCommandOptions
{
    /// <summary>既定のデータセット（例: "Champions"）。</summary>
    public string DataSetKey { get; set; } = DataCatalog.DefaultDataSetKey;
    public BattleFormat Format { get; set; } = BattleFormat.Singles;
    /// <summary>攻撃側の既定努力値（攻撃技に応じて A または C に振る）。</summary>
    public int DefaultAttackerOffenseEv { get; set; } = 252;
    /// <summary>防御側の既定 HP 努力値。</summary>
    public int DefaultDefenderHpEv { get; set; } = 252;
    /// <summary>防御側の既定 B/D 努力値（技の分類に応じた方）。</summary>
    public int DefaultDefenderDefenseEv { get; set; } = 0;
    /// <summary>使用率データから、性格・特性と攻撃側の持ち物の既定値を埋めるか（防御側の持ち物は入れない）。</summary>
    public bool UseUsageDefaults { get; set; } = true;
    /// <summary>防御側の持ち物（登録済みの相手の持ち物など、確定している情報）。コマンドに持ち物を書いたときはそちらが優先。</summary>
    public Item? DefenderItem { get; set; }
    /// <summary>2〜5 回当たる連続技の回数（2 や 5。null なら既定）。</summary>
    public int? HitCount { get; set; }
    /// <summary>出力 1 行の最大長（Twitch は 500 文字）。</summary>
    public int MaxLength { get; set; } = 480;
    /// <summary>急所で計算する（トークンの「急所」と同じ）。</summary>
    public bool IsCritical { get; set; }
    /// <summary>
    /// 名前解決後のポケモンに対して、登録済みの構成（使用チームなど）を返す。
    /// 第 2 引数は攻撃側なら true。null を返すと既定値（使用率ベース）で埋める。
    /// </summary>
    public Func<Pokemon, bool, PokemonBuild?>? BuildProvider { get; set; }
}

public sealed class DamageCommandResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public DamageRequest? Request { get; init; }
    public DamageResult? Damage { get; init; }

    public override string ToString() => Message;
}

/// <summary>
/// チャット向けテキストコマンド。<c>!dmg 攻撃側 技 防御側 [オプション...]</c> を解釈して 1 行の結果を返す。
/// <para>オプション（順不同、名前の後ろに並べる）:</para>
/// <list type="bullet">
/// <item><c>A252 C252 S4</c> … 攻撃側の努力値、<c>H252 B4 D252</c> … 防御側の努力値</item>
/// <item>性格名（いじっぱり 等）… 上昇補正が攻撃/特攻/素早さなら攻撃側、防御/特防なら防御側</item>
/// <item>持ち物名（こだわりメガネ 等）… 防御側に持たせるのが自然な持ち物（とつげきチョッキ・しんかのきせき・たべのこし・オボンのみ・半減きのみなど）は防御側、それ以外は攻撃側（前置きで明示もできる）</item>
/// <item><c>攻:</c> / <c>防:</c>（<c>攻撃:</c> <c>防御:</c> <c>こうげき:</c> <c>ぼうぎょ:</c> <c>A:</c> <c>B:</c> <c>C:</c> <c>D:</c>、全角の「：」も可）を前に付けると側を明示（例: <c>防:ずぶとい 防:たべのこし</c>）</item>
/// <item><c>+1</c>〜<c>+6</c>、<c>-1</c>〜<c>-6</c> … 攻撃側の攻撃ランク（<c>防:+1</c> で防御側の防御ランク）</item>
/// <item><c>急所</c>、<c>ダブル</c> / <c>シングル</c>、<c>Lv100</c>、<c>無振り</c>（防御側の努力値を 0 に）</item>
/// <item>特性名（ちからもち 等）… そのポケモンが持てる側に付く。両方持てるなら防御的な特性は防御側</item>
/// <item><c>テラス</c>（技タイプにテラスタル）、<c>テラスほのお</c> / <c>ほのおテラス</c>、<c>防:テラスみず</c></item>
/// <item><c>ダイマ</c>（攻撃側をダイマックス）、<c>防:ダイマ</c>（防御側: HP 2 倍）、<c>Z</c>（Z 技。第 7 世代）</item>
/// <item><c>壁</c>（オーロラベール。物理・特殊とも半減）、<c>リフレクター</c>（物理のみ）、<c>ひかりのかべ</c>（特殊のみ）</item>
/// <item>天候: <c>晴れ</c> <c>雨</c> <c>砂</c> <c>雪</c>、フィールド: <c>エレキ</c> <c>グラス</c> <c>サイコ</c> <c>ミスト</c></item>
/// <item>状態異常: <c>やけど</c> <c>まひ</c>（既定は攻撃側）、<c>どく</c> <c>もうどく</c>（既定は防御側。確定数に定数ダメージを織り込む）</item>
/// </list>
/// </summary>
public sealed class DamageCommand
{
    private static readonly Regex IgnoredHpToken = new(@"^(?:残り)?hp\d{1,3}[%％]$", RegexOptions.Compiled);
    /// <summary>上がる能力・下がる能力での性格指定（<c>B↑A↓</c>、<c>B↑</c>、<c>A↓</c>。↑は + 、↓は - でもよい）。</summary>
    private static readonly Regex NatureArrowToken = new(@"^(?:([ABCDSabcds])[↑＋+](?:([ABCDSabcds])[↓－\-])?|([ABCDSabcds])[↓－\-])$", RegexOptions.Compiled);
    /// <summary>
    /// <c>B↑A↓</c>・<c>B↑</c>・<c>A↓</c> のような指定から、上がる能力・下がる能力を読み取る（片方だけでも、もう片方は補わない）。
    /// その形でなければ false。
    /// </summary>
    public static bool TryParseArrowStats(string token, out Stat? up, out Stat? down)
    {
        up = down = null;
        var arrow = NatureArrowToken.Match(token);
        if (!arrow.Success) return false;
        up = arrow.Groups[1].Success ? StatNames.Parse(arrow.Groups[1].Value.ToUpperInvariant()) : null;
        var downText = arrow.Groups[2].Success ? arrow.Groups[2].Value : arrow.Groups[3].Success ? arrow.Groups[3].Value : null;
        down = downText is not null ? StatNames.Parse(downText.ToUpperInvariant()) : null;
        return true;
    }

    /// <summary>
    /// <c>B↑A↓</c>・<c>B↑</c>・<c>A↓</c> のような、上がる・下がる能力での性格指定か。その形なら true で、合う性格（無ければ null）を返す。
    /// 片方だけのときは、もう片方を使わない攻撃能力にした性格。
    /// </summary>
    public static bool TryParseNatureArrow(string token, IReadOnlyList<Nature> natures, out Nature? nature)
    {
        nature = null;
        var arrow = NatureArrowToken.Match(token);
        if (!arrow.Success) return false;
        var up = arrow.Groups[1].Success ? StatNames.Parse(arrow.Groups[1].Value.ToUpperInvariant()) : null;
        var downText = arrow.Groups[2].Success ? arrow.Groups[2].Value : arrow.Groups[3].Success ? arrow.Groups[3].Value : null;
        var down = downText is not null ? StatNames.Parse(downText.ToUpperInvariant()) : null;
        if (up == Stat.HP || down == Stat.HP) return true;
        nature = up is not null && down is not null ? natures.FirstOrDefault(n => n.IncreasedStat == up && n.DecreasedStat == down)
            : up is not null ? NatureSelection.WithIncreased(natures, null, up)
            : down is not null ? NatureSelection.WithDecreased(natures, null, down)
            : null;
        return true;
    }

    private static readonly Regex EvToken = new(@"^(?:([HABCDShabcds])(\d{1,3}))+$", RegexOptions.Compiled);
    private static readonly Regex EvPair = new(@"([HABCDShabcds])(\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex BoostToken = new(@"^([+-])([1-6])$", RegexOptions.Compiled);
    private static readonly Regex LevelToken = new(@"^(?:lv\.?|lv|レベル|れべる)(\d{1,3})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly char[] Separators = { ' ', '　', '\t', ',', '、' };

    private readonly DataCatalog _catalog;

    public DamageCommand(DataCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>略称込みのポケモン名解決器（他のコマンドと共有）。</summary>
    public static NameResolver<Pokemon> CreatePokemonResolver(PokemonDataSet data) =>
        new(data.Pokemon, p => p.Name, PokemonAliases(data));

    public static string Usage =>
        "使い方: !dmg 攻撃側 技 防御側 [A252 H252 性格 持ち物 特性 テラスほのお 晴れ サイコフィールド +1 急所 ダブル ...]  例: !dmg イエッサン♂ ワイドフォース メガリザードンX サイコフィールド";

    public async Task<DamageCommandResult> ExecuteAsync(string argumentText, DamageCommandOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DamageCommandOptions();
        argumentText = CommandText.Normalize(argumentText);
        try
        {
            return await ExecuteCoreAsync(argumentText, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"計算に失敗しました: {ex.Message}");
        }
    }

    private async Task<DamageCommandResult> ExecuteCoreAsync(string argumentText, DamageCommandOptions options, CancellationToken ct)
    {
        var tokens = (argumentText ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tokens.Count > 0 && tokens[0].StartsWith('!')) tokens.RemoveAt(0);
        if (tokens.Count > 0 && tokens[0].Equals("damage", StringComparison.OrdinalIgnoreCase)) tokens.RemoveAt(0);
        if (tokens.Count == 0 || tokens[0] is "help" or "ヘルプ" or "?")
            return Fail(Usage);
        if (tokens.Count < 3)
            return Fail($"攻撃側・技・防御側の 3 つを指定してください。{Usage}");

        // データセット名はトークンのどこにあっても拾う（例: !dmg champions ...）
        var dataSets = await _catalog.GetDataSetsAsync(ct).ConfigureAwait(false);
        var dataSetKey = await _catalog.ResolveDefaultKeyAsync(options.DataSetKey, ct).ConfigureAwait(false);
        var dataSetWords = DataSetWords(dataSets);
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (dataSetWords.TryGetValue(NameNormalizer.Normalize(tokens[i]), out var key))
            {
                dataSetKey = key;
                tokens.RemoveAt(i);
            }
        }
        if (tokens.Count < 3)
            return Fail($"攻撃側・技・防御側の 3 つを指定してください。{Usage}");
        if (dataSets.FirstOrDefault(d => d.Key == dataSetKey) is { CalcSupported: false } refOnly)
            return Fail(PokeCommand.NotCalcSupported(refOnly));

        var data = await _catalog.GetDataSetAsync(dataSetKey, ct).ConfigureAwait(false);
        var pokemonResolver = new NameResolver<Pokemon>(data.Pokemon, p => p.Name, PokemonAliases(data));
        var moveResolver = new NameResolver<Move>(data.Moves, m => m.Name);

        var attackerMatch = pokemonResolver.Resolve(tokens[0]);
        if (!attackerMatch.IsResolved)
            return Fail(NotFound("ポケモン", tokens[0], attackerMatch.Candidates.Select(p => p.Name)) + await ElsewhereHintAsync(tokens[0], data, dataSets, ct).ConfigureAwait(false));
        var (moveToken, moveRank) = SplitRankSuffix(tokens[1]);
        var moveMatch = moveResolver.Resolve(moveToken);
        if (!moveMatch.IsResolved) return Fail(NotFound("技", moveToken, moveMatch.Candidates.Select(m => m.Name)));
        var defenderMatch = pokemonResolver.Resolve(tokens[2]);
        if (!defenderMatch.IsResolved)
            return Fail(NotFound("ポケモン", tokens[2], defenderMatch.Candidates.Select(p => p.Name)) + await ElsewhereHintAsync(tokens[2], data, dataSets, ct).ConfigureAwait(false));

        var move = moveMatch.Value!;
        if (!move.IsDamaging)
            return Fail($"{move.Name} はダメージを与える技ではありません。");

        var p = new DamageParse(data, options, move, attackerMatch.Value!, defenderMatch.Value!, moveRank);
        foreach (var rawToken in tokens.Skip(3))
        {
            if (ApplyOptionToken(p, rawToken)) continue;
            var (_, token) = SplitSide(rawToken);
            if (await ItemExistsElsewhereAsync(_catalog, dataSetKey, token, ct).ConfigureAwait(false))
                p.warnings.Add($"「{rawToken}」は{p.data.Info.Name}にない持ち物のため無視");
            else
                p.warnings.Add($"「{rawToken}」は無視");
        }

        await ApplyDefaultsAsync(p, ct).ConfigureAwait(false);
        var request = new DamageRequest
        {
            Attacker = p.attacker,
            Move = p.move,
            Defender = p.defender,
            Format = p.format,
            IsCritical = p.isCritical,
            IsZMove = p.isZMove,
            Weather = p.weather,
            Terrain = p.terrain,
            Screen = p.screen,
            Reflect = p.reflect,
            LightScreen = p.lightScreen,
            HitCountOverride = p.options.HitCount,
        };
        var result = new DamageCalculator(p.data.TypeChart, p.data.Info.Generation).Calculate(request);

        var message = Format(request, result, p.data.Info, p.warnings);
        if (message.Length > p.options.MaxLength)
            message = message[..(p.options.MaxLength - 1)] + "…";

        return new DamageCommandResult { Success = true, Message = message, Request = request, Damage = result };
    }

    /// <summary>
    /// 技・防御側より後ろの 1 つの指定（急所・天候・努力値・性格・持ち物・特性など）を読み取って反映する。
    /// どれにも当てはまらなければ false（呼び出し側が「無視」の注意を出す）。
    /// </summary>
    private static bool ApplyOptionToken(DamageParse p, string rawToken)
    {
        var token = rawToken;
        var (forcedAttacker, bareToken) = SplitSide(token);
        token = bareToken;
        if (token.Length == 0) return true;

        var normalized = NameNormalizer.Normalize(token);

        if (normalized is "急所" or "きゅうしょ" or "crit")
        {
            p.isCritical = true;
            return true;
        }
        if (normalized is "z" or "z技" or "zわざ" or "zmove")
        {
            p.isZMove = true;
            return true;
        }
        if (normalized is "りふれくたー" or "reflect")
        {
            p.reflect = true;
            return true;
        }
        if (normalized is "ひかりのかべ" or "光の壁" or "lightscreen")
        {
            p.lightScreen = true;
            return true;
        }
        if (normalized is "壁" or "かべ" or "おーろらべーる" or "screen")
        {
            p.screen = true;
            return true;
        }
        // 残り HP のオプション（HP50% など）は無くなった。入力されても、何も言わずに読み飛ばす
        if (IgnoredHpToken.IsMatch(normalized)) return true;
        if (StatusNames.Parse(normalized) is { } status)
        {
            // 既定: やけど・まひは攻撃側（自分の火力/素早さ）、どく・もうどくは防御側（定数ダメージで確定数が変わる）
            var toAttacker = forcedAttacker ?? status is StatusCondition.Burn or StatusCondition.Paralysis;
            if (toAttacker) p.attacker.Status = status; else p.defender.Status = status;
            return true;
        }
        if (TryParseWeather(normalized) is { } w) { p.weather = w; return true; }
        if (TryParseTerrain(normalized) is { } t) { p.terrain = t; return true; }
        if (TryParseTera(normalized, out var teraType))
        {
            if (teraType is null)
            {
                if (forcedAttacker == false) p.warnings.Add("防御側のテラスタイプを指定してください（例: 防:テラスみず）");
                else p.attackerTeraToMoveType = true;
            }
            else if (forcedAttacker == false) p.defender.TeraType = teraType;
            else p.attacker.TeraType = teraType;
            return true;
        }
        if (IsNoItemWord(normalized))
        {
            if (forcedAttacker == true && p.attackerPreset is not null)
            {
                p.warnings.Add("攻撃側の持ち物は、育成ポケモン・自分チームの登録で決まります（持ち物なしは反映しません）");
                return true;
            }
            if (forcedAttacker == false) { p.defender.Item = null; p.defenderItemSet = true; }
            else if (forcedAttacker == true) { p.attacker.Item = null; p.attackerItemSet = true; }
            else if (p.attackerPreset is not null) { p.defender.Item = null; p.defenderItemSet = true; }   // 登録済みの攻撃側の持ち物は、登録で決まる
            else { p.attacker.Item = null; p.defender.Item = null; p.attackerItemSet = p.defenderItemSet = true; }
            return true;
        }
        if (normalized is "ダイマ" or "だいま" or "ダイマックス" or "だいまっくす" or "dmax" or "dynamax")
        {
            if (forcedAttacker == false) p.defender.IsDynamax = true; else p.attacker.IsDynamax = true;
            return true;
        }
        if (normalized is "ダブル" or "だぶる" or "double" or "doubles" or "dbl") { p.format = BattleFormat.Doubles; return true; }
        if (normalized is "シングル" or "しんぐる" or "single" or "singles") { p.format = BattleFormat.Singles; return true; }
        if (normalized is "無振り" or "むふり" or "むぶり" or "noev")
        {
            if (forcedAttacker == true) { p.attacker.EVs = new StatSet(); p.attackerEvSet = true; }
            else { p.defenderNoEv = true; p.defenderEvSet = true; }
            return true;
        }

        var level = LevelToken.Match(token);
        if (level.Success)
        {
            var lv = Math.Clamp(int.Parse(level.Groups[1].Value), 1, 100);
            if (forcedAttacker != false) p.attacker.Level = lv;
            if (forcedAttacker != true) p.defender.Level = lv;
            return true;
        }

        var boost = BoostToken.Match(token);
        if (boost.Success)
        {
            var stage = int.Parse(boost.Groups[2].Value) * (boost.Groups[1].Value == "-" ? -1 : 1);
            if (forcedAttacker == false) p.defender.Boosts[p.defenseStat] = stage;
            else p.attacker.Boosts[p.offenseStat] = stage;
            return true;
        }

        return ApplyValueToken(p, token, forcedAttacker);
    }

    /// <summary>努力値・性格・持ち物・特性のように、名前や数値で書く指定を反映する。どれにも当てはまらなければ false。</summary>
    private static bool ApplyValueToken(DamageParse p, string token, bool? forcedAttacker)
    {
        if (EvToken.IsMatch(token))
        {
            foreach (Match pair in EvPair.Matches(token))
            {
                var stat = StatNames.Parse(pair.Groups[1].Value.ToUpperInvariant())!.Value;
                var value = Math.Clamp(int.Parse(pair.Groups[2].Value), 0, EvRules.MaxPerStat(p.evSystem));
                var toAttacker = forcedAttacker ?? stat is Stat.Attack or Stat.SpAttack or Stat.Speed;
                if (toAttacker)
                {
                    if (!p.attackerEvSet) { p.attacker.EVs = new StatSet(); p.attackerEvSet = true; }
                    p.attacker.EVs[stat] = value;
                }
                else
                {
                    if (!p.defenderEvSet) { p.defender.EVs = new StatSet(); p.defenderEvSet = true; }
                    p.defender.EVs[stat] = value;
                }
            }
            return true;
        }

        // 「B↑A↓」「B↑」「A↓」のような、上がる・下がる能力での性格指定
        if (NatureArrowToken.Match(token) is { Success: true } arrow)
        {
            var up = arrow.Groups[1].Success ? StatNames.Parse(arrow.Groups[1].Value.ToUpperInvariant()) : null;
            var downText = arrow.Groups[2].Success ? arrow.Groups[2].Value : arrow.Groups[3].Success ? arrow.Groups[3].Value : null;
            var down = downText is not null ? StatNames.Parse(downText.ToUpperInvariant()) : null;
            var arrowNature = up is not null && down is not null ? p.data.Natures.FirstOrDefault(n => n.IncreasedStat == up && n.DecreasedStat == down)
                : up is not null ? NatureSelection.WithIncreased(p.data.Natures, null, up, physical: p.move.Category != MoveCategory.Special)
                : down is not null ? NatureSelection.WithDecreased(p.data.Natures, null, down, physical: p.move.Category != MoveCategory.Special)
                : null;
            if (arrowNature is not null && up != Stat.HP && down != Stat.HP)
            {
                var toAttacker = forcedAttacker ?? (up ?? down) is Stat.Attack or Stat.SpAttack or Stat.Speed;
                if (toAttacker) { p.attacker.Nature = arrowNature; p.attackerNatureSet = true; }
                else { p.defender.Nature = arrowNature; p.defenderNatureSet = true; }
                return true;
            }
        }

        var nature = p.natureResolver.Resolve(token);
        if (nature.IsResolved)
        {
            var n = nature.Value!;
            var toAttacker = forcedAttacker ?? n.IncreasedStat is Stat.Attack or Stat.SpAttack or Stat.Speed or null;
            if (toAttacker) { p.attacker.Nature = n; p.attackerNatureSet = true; }
            else { p.defender.Nature = n; p.defenderNatureSet = true; }
            return true;
        }

        var item = p.itemResolver.Resolve(token);
        if (item.IsResolved)
        {
            var it = item.Value!;
            // 登録済みの攻撃側の持ち物は、育成ポケモン・自分チームの登録で決まる。持ち物名は（前置きがなくても）防御側（相手）に付ける
            var toAttacker = forcedAttacker ?? (p.attackerPreset is null && !it.IsDefensive);
            if (toAttacker && p.attackerPreset is not null)
            {
                p.warnings.Add($"攻撃側の持ち物は、育成ポケモン・自分チームの登録で決まります（{it.Name}は反映しません）");
                return true;
            }
            if (toAttacker) { p.attacker.Item = it; p.attackerItemSet = true; }
            else { p.defender.Item = it; p.defenderItemSet = true; }
            return true;
        }

        var ability = p.abilityResolver.Resolve(token);
        if (ability.IsResolved)
        {
            var ab = ability.Value!;
            var attackerCan = p.attacker.Pokemon.AbilityIds.Contains(ab.Id);
            var defenderCan = p.defender.Pokemon.AbilityIds.Contains(ab.Id);
            var toAttacker = forcedAttacker ?? (attackerCan && !defenderCan ? true
                : defenderCan && !attackerCan ? false
                : !AbilityEffects.IsDefensive(ab.Identifier));
            if (toAttacker) { p.attacker.Ability = ab; p.attackerAbilitySet = true; }
            else { p.defender.Ability = ab; p.defenderAbilitySet = true; }
            return true;
        }
        return false;
    }

    /// <summary>指定されなかった項目（努力値・性格・持ち物・特性・テラスタイプ）に、使用率などの既定値を入れる。</summary>
    private static async Task ApplyDefaultsAsync(DamageParse p, CancellationToken ct)
    {
        UsageData? usage = null;
        if (p.options.UseUsageDefaults)
        {
            usage = await p.data.GetUsageAsync(p.format, ct).ConfigureAwait(false);
            if (usage.IsEmpty) usage = null;
        }

        if (!p.attackerEvSet)
            p.attacker.EVs[p.offenseStat] = Math.Min(p.options.DefaultAttackerOffenseEv, EvRules.Full(p.evSystem));
        if (!p.defenderEvSet)
        {
            p.defender.EVs[Stat.HP] = Math.Min(p.options.DefaultDefenderHpEv, EvRules.Full(p.evSystem));
            p.defender.EVs[p.defenseStat] = Math.Min(p.options.DefaultDefenderDefenseEv, EvRules.Full(p.evSystem));
        }
        else if (p.defenderNoEv)
        {
            p.defender.EVs = new StatSet();
        }

        if (!p.attackerNatureSet)
            p.attacker.Nature = usage?.TopNature(p.attacker.Pokemon.Id) ?? p.data.FindNature("まじめ") ?? p.data.Natures.FirstOrDefault(n => n.IsNeutral);
        if (!p.defenderNatureSet)
            p.defender.Nature = usage?.TopNature(p.defender.Pokemon.Id) ?? p.data.FindNature("まじめ") ?? p.data.Natures.FirstOrDefault(n => n.IsNeutral);
        // メガシンカ後の姿は、持ち物がメガストーンで確定（使用率の持ち物より優先する）
        if (!p.attackerItemSet && p.attacker.Pokemon.FixedItemId is { } attackerStone && p.data.FindItem(attackerStone) is { } attackerStoneItem)
            p.attacker.Item = attackerStoneItem;
        else if (!p.attackerItemSet && usage is not null)
            p.attacker.Item = usage.TopItem(p.attacker.Pokemon.Id);
        // 防御側は対戦中の相手なので、使用率の持ち物は入れない（書いた持ち物かメガストーンだけ。不明なら持ち物なしで計算し、表示にも出さない）
        if (!p.defenderItemSet && p.options.DefenderItem is { } knownDefenderItem)
            p.defender.Item = knownDefenderItem;
        else if (!p.defenderItemSet && p.defender.Pokemon.FixedItemId is { } defenderStone && p.data.FindItem(defenderStone) is { } defenderStoneItem)
            p.defender.Item = defenderStoneItem;
        // 通常のオーガポンは、使用率の持ち物がほかのすがたのお面でも、みどりのめんにする
        if (!p.attackerItemSet) p.attacker.Item = MoveEffects.NormalizeOgerponItem(p.attacker.Pokemon, p.attacker.Item, p.data);
        if (!p.attackerAbilitySet)
            p.attacker.Ability = usage?.DefaultAbility(p.attacker.Pokemon) ?? AbilityEffects.ChooseDefault(p.data.AbilitiesOf(p.attacker.Pokemon), null);
        if (!p.defenderAbilitySet)
            p.defender.Ability = usage?.DefaultAbility(p.defender.Pokemon, AbilityEffects.Side.Defender) ?? AbilityEffects.ChooseDefault(p.data.AbilitiesOf(p.defender.Pokemon), null, AbilityEffects.Side.Defender);
        if (p.attackerTeraToMoveType)
            p.attacker.TeraType = AbilityEffects.SkinType(p.attacker.Ability?.Identifier ?? "") is { } skin && p.move.Type == "Normal" ? skin : p.move.Type;
        // テラスタルしたオーガポンのテラスタイプは、お面のタイプで決まる（表示も計算に合わせる）
        foreach (var ogerpon in new[] { p.attacker, p.defender })
            if (ogerpon.Pokemon.SpeciesId == MoveEffects.OgerponSpeciesId && ogerpon.IsTerastallized) ogerpon.TeraType = ogerpon.Pokemon.Types.Last();

        if (!p.data.CanLearn(p.attacker.Pokemon.Id, p.move.Id))
            p.warnings.Add($"{p.attacker.Pokemon.Name}は{p.move.Name}を覚えません");
        foreach (var b in new[] { p.attacker, p.defender })
        {
            if (b.Pokemon.IsProvisional)
                p.warnings.Add($"{b.Pokemon.Name}は{p.data.Info.Name}未収録のため第9世代データで計算");
        }

        if (p.isZMove && !p.data.Info.HasZMove)
        {
            p.warnings.Add("Z技は第7世代だけ");
            p.isZMove = false;
        }
    }

    /// <summary>!dmg の指定を読み取る途中の状態（攻撃側・防御側の構成と、どの項目が指定済みか、場の状態）。</summary>
    private sealed class DamageParse
    {
        public DamageParse(PokemonDataSet data, DamageCommandOptions options, Move move, Pokemon attackerPokemon, Pokemon defenderPokemon, int moveRank)
        {
            this.data = data;
            this.options = options;
            this.move = move;
            evSystem = data.Info.EvSystem;
            attackerPreset = options.BuildProvider?.Invoke(attackerPokemon, true);
            defenderPreset = options.BuildProvider?.Invoke(defenderPokemon, false);
            attacker = attackerPreset?.Clone() ?? new PokemonBuild(attackerPokemon, evSystem);
            defender = defenderPreset?.Clone() ?? new PokemonBuild(defenderPokemon, evSystem);
            attacker.EvSystem = evSystem;
            defender.EvSystem = evSystem;
            format = options.Format;
            isCritical = options.IsCritical;
            attackerEvSet = attackerNatureSet = attackerItemSet = attackerPreset is not null;
            defenderEvSet = defenderNatureSet = defenderItemSet = defenderPreset is not null;
            attackerAbilitySet = attackerPreset?.Ability is not null;
            defenderAbilitySet = defenderPreset?.Ability is not null;
            if (moveRank != 0) attacker.Boosts[move.AttackStatUsed] = moveRank;
            offenseStat = move.AttackStatUsed;
            defenseStat = move.DefenseStatUsed;
            natureResolver = new NameResolver<Nature>(data.Natures, n => n.Name);
            itemResolver = new NameResolver<Item>(data.Items, i => i.Name);
            abilityResolver = new NameResolver<Ability>(data.Abilities, a => a.Name);
        }

        public readonly PokemonDataSet data;
        public readonly DamageCommandOptions options;
        public readonly Move move;
        public readonly EvSystem evSystem;
        public readonly PokemonBuild? attackerPreset, defenderPreset;
        public readonly PokemonBuild attacker, defender;
        public readonly Stat offenseStat, defenseStat;
        public readonly NameResolver<Nature> natureResolver;
        public readonly NameResolver<Item> itemResolver;
        public readonly NameResolver<Ability> abilityResolver;
        public readonly List<string> warnings = new();
        public BattleFormat format;
        public bool isCritical, isZMove, screen, reflect, lightScreen;
        public bool attackerEvSet, defenderEvSet, attackerNatureSet, defenderNatureSet, attackerItemSet, defenderItemSet;
        public bool attackerAbilitySet, defenderAbilitySet, defenderNoEv, attackerTeraToMoveType;
        public Weather weather = Weather.None;
        public Terrain terrain = Terrain.None;
    }

    public static string Format(DamageRequest request, DamageResult result, DataSetInfo dataSet, IReadOnlyList<string>? warnings = null)
    {
        var a = request.Attacker;
        var d = request.Defender;
        var tagText = TagText(request, result, dataSet, request.IsCritical);
        var warnText = warnings is { Count: > 0 } ? $" ※{string.Join("、", warnings)}" : "";

        return Header(request, result) + ": " +
               $"{result.RangeText} {result.KnockOut}{ParentalText(result)}{tagText}{warnText}";
    }

    /// <summary>「攻撃側(構成) 技 → 防御側(構成)」（結果の前の部分）。ダイマックス技・Z 技に変わるときは、変換後の技名も添える（例: じしん（ライジングランドオーバー））。</summary>
    /// <param name="defenderText">防御側の括弧の前までの表記を差し替える（相手の情報が範囲のとき。例: <c>フシギバナ(補正なし:B0〜6pt)</c>）。</param>
    public static string Header(DamageRequest request, DamageResult result, string? defenderText = null)
    {
        var a = request.Attacker;
        var d = request.Defender;
        var moveText = result.Converted is { } converted ? $"{request.Move.Name}（{converted.Name}）" : request.Move.Name;
        return $"{a.Pokemon.Name}({a.DescribeShort()}) {moveText} → {defenderText ?? $"{d.Pokemon.Name}({d.DescribeShort(includeAbility: false)})"}";
    }

    /// <summary>おやこあいのときの、1 回目・2 回目のダメージ（例: <c> (おやこあい 1回目 20〜24・2回目 5〜6)</c>）。</summary>
    public static string ParentalText(DamageResult result) =>
        result.ParentalBond && result.HitRolls is { Count: 2 } h
            ? $" (おやこあい 1回目 {h[0][0]}〜{h[0][^1]}・2回目 {h[1][0]}〜{h[1][^1]})"
            : "";

    /// <summary>
    /// 急所なし・ありを 1 つにまとめた表示（ダメージ計算機の「チャットでの表示」用）。
    /// 例: 〜: 12.6〜15.3% (24〜29) 乱数7発(8.4%) / 急所 18.9〜22.6% (36〜43) 乱数5発(97.5%) [いまひとつ(0.5倍) 雪]。
    /// <paramref name="request"/> は急所なしの条件、<paramref name="crit"/> は同じ条件で急所に当たったときの結果。
    /// </summary>
    public static string FormatBoth(DamageRequest request, DamageResult result, DamageResult crit, DataSetInfo dataSet, IReadOnlyList<string>? warnings = null)
    {
        var a = request.Attacker;
        var d = request.Defender;
        var tagText = TagText(request, result, dataSet, withCritical: false);
        var warnText = warnings is { Count: > 0 } ? $" ※{string.Join("、", warnings)}" : "";
        var moveText = result.Converted is { } converted ? $"{request.Move.Name}（{converted.Name}）" : request.Move.Name;
        return $"{a.Pokemon.Name}({a.DescribeShort()}) {moveText} → {d.Pokemon.Name}({d.DescribeShort(includeAbility: false)}): " +
               $"{result.RangeText} {result.KnockOut}{ParentalText(result)} / 急所 {crit.RangeText} {crit.KnockOut}{ParentalText(crit)}{tagText}{warnText}";
    }

    /// <summary>末尾の [相性 Z技 急所 天候 フィールド 壁 ダブル]。</summary>
    private static string TagText(DamageRequest request, DamageResult result, DataSetInfo dataSet, bool withCritical)
    {
        var tags = new List<string>();
        if (result.TypeEffectiveness != 1.0) tags.Add(result.EffectivenessText);
        if (request.IsZMove) tags.Add("Z技");
        if (request.HitCountOverride is { } hitCount and > 1 && (MoveEffects.HitsTwoToFive(request.Move) || MoveEffects.IsEscalating(request.Move))) tags.Add($"{hitCount}回");
        if (withCritical) tags.Add("急所");
        if (request.Weather != Weather.None) tags.Add(FieldNames.Japanese(request.Weather, dataSet.Generation));
        if (request.Terrain != Terrain.None) tags.Add(FieldNames.Japanese(request.Terrain));
        if (request.Screen) tags.Add("壁");
        else
        {
            if (request.Reflect) tags.Add("リフレクター");
            if (request.LightScreen) tags.Add("ひかりのかべ");
        }
        if (request.Format == BattleFormat.Doubles) tags.Add("ダブル");
        return tags.Count > 0 ? $" [{string.Join(" ", tags)}]" : "";
    }

    private static readonly Regex RankSuffix = new(@"^(.*?)([+\-＋－][1-6])$", RegexOptions.Compiled);   // 「+-＋」と書くと文字の範囲になり、ほとんどの文字が符号になってしまう

    /// <summary>「じしん+2」→ ("じしん", 2)。</summary>
    public static (string Name, int Rank) SplitRankSuffix(string token)
    {
        var m = RankSuffix.Match(token);
        if (!m.Success || m.Groups[1].Value.Length == 0) return (token, 0);
        var sign = m.Groups[2].Value[0] is '-' or '－' ? -1 : 1;
        return (m.Groups[1].Value, sign * (m.Groups[2].Value[1] - '0'));
    }

    /// <summary>「晴れ」「雨」などの天候トークンを解釈する（正規化前の文字列でよい）。</summary>
    public static Weather? ParseWeather(string token) => TryParseWeather(NameNormalizer.Normalize(token));
    /// <summary>「サイコ」「エレキフィールド」などのフィールドトークンを解釈する。</summary>
    public static Terrain? ParseTerrain(string token) => TryParseTerrain(NameNormalizer.Normalize(token));

    private static Weather? TryParseWeather(string n) => n switch
    {
        "晴れ" or "はれ" or "にほんばれ" or "日本晴れ" or "sun" or "sunny" => Weather.Sun,
        "雨" or "あめ" or "あまごい" or "雨乞い" or "rain" => Weather.Rain,
        "砂" or "すな" or "すなあらし" or "砂嵐" or "sand" => Weather.Sand,
        "雪" or "ゆき" or "ゆきげしき" or "あられ" or "snow" or "hail" => Weather.Snow,
        _ => null,
    };

    private static Terrain? TryParseTerrain(string n) => n switch
    {
        "えれき" or "えれきふぃーるど" or "ef" or "electric" => Terrain.Electric,
        "ぐらす" or "ぐらすふぃーるど" or "gf" or "grassy" => Terrain.Grassy,
        "さいこ" or "さいこふぃーるど" or "pf" or "psychic" => Terrain.Psychic,
        "みすと" or "みすとふぃーるど" or "mf" or "misty" => Terrain.Misty,
        _ => null,
    };

    /// <summary>「テラス」「テラスほのお」「ほのおテラス」「tera:fire」を解釈する。テラスだけなら type は null。</summary>
    private static bool TryParseTera(string n, out string? type)
    {
        type = null;
        const string word = "てらす";
        string? rest = null;
        if (n.StartsWith(word, StringComparison.Ordinal)) rest = n[word.Length..];
        else if (n.EndsWith(word, StringComparison.Ordinal)) rest = n[..^word.Length];
        else if (n.StartsWith("tera", StringComparison.Ordinal)) rest = n[4..];
        else return false;
        rest = rest.TrimStart(':', '：', 'た', 'る');
        if (rest.Length == 0) return true;
        foreach (var t in TypeNames.All)
        {
            if (NameNormalizer.Normalize(TypeNames.ToJapanese(t)) == rest || t.Equals(rest, StringComparison.OrdinalIgnoreCase))
            {
                type = t;
                return true;
            }
        }
        return false;
    }

    /// <summary>「持ち物なし」と書いたトークンか（前置きを外して正規化したあとの形）。</summary>
    public static bool IsNoItemWord(string normalized) => normalized is "持ち物なし" or "もちものなし" or "道具なし" or "どうぐなし" or "noitem";

    /// <summary>コマンドの引数に「持ち物なし」（前置き付きも含む）が書かれているか。</summary>
    public static bool MentionsNoItem(IEnumerable<string> args) => args.Any(a => IsNoItemWord(NameNormalizer.Normalize(SplitSide(a).Remainder)));

    // 攻撃側・防御側を明示する前置き（全角・半角のコロン、英字は大文字小文字を問わない）。
    // 攻撃側: 攻 / 攻撃 / こうげき / A / C / atk、防御側: 防 / 防御 / ぼうぎょ / B / D / def
    private static readonly string[] AttackerWords = { "攻", "攻撃", "こうげき", "a", "c", "atk" };
    private static readonly string[] DefenderWords = { "防", "防御", "ぼうぎょ", "b", "d", "def" };

    private static readonly (string Prefix, bool IsAttacker)[] SidePrefixes =
        AttackerWords.SelectMany(w => new[] { (w + ":", true), (w + "：", true) })
            .Concat(DefenderWords.SelectMany(w => new[] { (w + ":", false), (w + "：", false) }))
            .OrderByDescending(x => x.Item1.Length)
            .ToArray();

    /// <summary>
    /// 「防:たべのこし」「防御:たべのこし」「B：たべのこし」のように、側を明示する前置きを外す。
    /// 前置きが無ければ <c>IsAttacker</c> は null で、<c>Remainder</c> は元の文字列。
    /// </summary>
    public static (bool? IsAttacker, string Remainder) SplitSide(string token)
    {
        foreach (var (prefix, side) in SidePrefixes)
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (side, token[prefix.Length..]);
        return (null, token);
    }

    private static Dictionary<string, string> DataSetWords(IEnumerable<DataSetInfo> dataSets)
    {
        var words = new Dictionary<string, string>();
        foreach (var ds in dataSets)
        {
            words.TryAdd(NameNormalizer.Normalize(ds.Key), ds.Key);
            words.TryAdd(NameNormalizer.Normalize(ds.Name), ds.Key);
            var extra = ds.Key switch
            {
                "Champions" => new[] { "チャンピオンズ", "champion", "pc" },
                "Gen9" => new[] { "SV", "スカーレット", "バイオレット", "第9世代" },
                "Gen8" => new[] { "剣盾", "ソード", "シールド", "第8世代" },
                _ => Array.Empty<string>(),
            };
            foreach (var w in extra)
                words.TryAdd(NameNormalizer.Normalize(w), ds.Key);
        }
        return words;
    }

    /// <summary>よく使う略称。メガ○○ → 「メガ」+ 略称 もここで吸収する。</summary>
    internal static IEnumerable<(string Alias, string Name)> PokemonAliases(PokemonDataSet data)
    {
        var byName = new HashSet<string>(data.Pokemon.Select(p => p.Name));
        var aliases = new (string Alias, string Name)[]
        {
            ("メガリザX", "メガリザードンX"), ("メガリザY", "メガリザードンY"), ("リザX", "メガリザードンX"), ("リザY", "メガリザードンY"),
            ("メガガルド", "メガガルーラ"), ("メガガブ", "メガガブリアス"), ("メガバシャ", "メガバシャーモ"), ("メガボーマンダ", "メガボーマンダ"),
            ("メガマンダ", "メガボーマンダ"), ("メガクチート", "メガクチート"), ("メガゲンガー", "メガゲンガー"), ("メガルカ", "メガルカリオ"),
            ("メガバンギ", "メガバンギラス"), ("メガメタグロス", "メガメタグロス"), ("メガグロス", "メガメタグロス"), ("メガミミロップ", "メガミミロップ"),
            ("ガブ", "ガブリアス"), ("バシャ", "バシャーモ"), ("マンダ", "ボーマンダ"), ("バンギ", "バンギラス"), ("グロス", "メタグロス"),
            ("ハバタクカミ", "ハバタクカミ"), ("カミ", "ハバタクカミ"), ("テツノツツミ", "テツノツツミ"), ("ツツミ", "テツノツツミ"),
            ("イエッサン♂", "イエッサン(♂)"), ("イエッサンオス", "イエッサン(♂)"), ("イエッサンメス", "イエッサン(♀)"), ("イエッサン♀", "イエッサン(♀)"),
            ("ニャオニクス♂", "ニャオニクス(♂)"), ("ニャオニクス♀", "ニャオニクス(♀)"),
            ("ランドロス", "ランドロス(れいじゅうフォルム)"), ("霊獣ランド", "ランドロス(れいじゅうフォルム)"), ("化身ランド", "ランドロス"),
            ("ウーラオス", "ウーラオス"), ("連撃ウーラオス", "ウーラオス(れんげきのかた)"), ("水ウーラ", "ウーラオス(れんげきのかた)"), ("悪ウーラ", "ウーラオス"),
            ("ガオガエン", "ガオガエン"), ("ドラパルト", "ドラパルト"), ("パオジアン", "パオジアン"), ("パオ", "パオジアン"),
        };
        foreach (var (alias, name) in aliases)
        {
            if (byName.Contains(name)) yield return (alias, name);
        }
    }

    /// <summary>別のデータセットにその名前があれば、切り替え方を案内する。</summary>
    /// <summary>
    /// 今のゲームにない持ち物が、ほかのゲーム（第9世代＝すべての持ち物がある）の持ち物名と完全に一致するか。
    /// 「そのゲームにない持ち物」と「ただの打ち間違い」を、案内の文で区別するために使う。
    /// </summary>
    internal static async Task<bool> ItemExistsElsewhereAsync(DataCatalog catalog, string currentKey, string token, CancellationToken ct)
    {
        const string AllItemsKey = "Gen9";
        if (currentKey == AllItemsKey || string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var all = await catalog.GetDataSetAsync(AllItemsKey, ct).ConfigureAwait(false);
            var normalized = NameNormalizer.Normalize(token.TrimStart('@', '＠'));
            return all.Items.Any(i => !i.IsMegaStone && NameNormalizer.Normalize(i.Name) == normalized);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<string> ElsewhereHintAsync(string query, PokemonDataSet current, IReadOnlyList<DataSetInfo> dataSets, CancellationToken ct)
    {
        foreach (var info in dataSets.Reverse())
        {
            if (info.Key == current.Info.Key) continue;
            PokemonDataSet other;
            try { other = await _catalog.GetDataSetAsync(info.Key, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { continue; }
            var match = new NameResolver<Pokemon>(other.Pokemon, p => p.Name, PokemonAliases(other)).Resolve(query);
            if (match.IsResolved)
                return $"（{match.Value!.Name}は「{info.Name}」のデータにあります。現在は「{current.Info.Name}」です。コマンドに {info.Key.ToLowerInvariant()} を足すか、データを切り替えてください）";
        }
        return "";
    }

    private static string NotFound(string kind, string query, IEnumerable<string> candidates)
    {
        var list = candidates.Take(5).ToList();
        return list.Count > 0
            ? $"{kind}「{query}」が特定できません。候補: {string.Join("、", list)}"
            : $"{kind}「{query}」が見つかりません。";
    }

    private static DamageCommandResult Fail(string message) => new() { Success = false, Message = message };
}
