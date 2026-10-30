#pragma warning disable CS0618   // 古い版の ActiveTeam を読み替えるために、このファイルの中だけで使う
using System.Text.Json;
using System.Text.Json.Serialization;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Roster;

/// <summary>登録データの上限（サイト・コマンド・クラウド（Worker の MAX_ID）で同じ値）。</summary>
public static class RosterLimits
{
    /// <summary>チーム番号の最大（Worker の <c>MAX_ID</c> と同じ。これを超える番号は、クラウドが 400 で断る）。</summary>
    public const int MaxTeamNumber = 2147483646;

    /// <summary>チーム番号として読む（1 以上 <see cref="MaxTeamNumber"/> 以下の整数）。</summary>
    public static bool TryParseTeamNumber(string? text, out int number) =>
        int.TryParse(text, out number) && number >= 1 && number <= MaxTeamNumber;
}

/// <summary>登録データのうち、1 回の変更で書き換える部分。</summary>
public enum RosterPart
{
    /// <summary>登録ポケモン（と次に振る登録 ID）。消したポケモンはチームからも外れる。</summary>
    Pokemon,
    /// <summary>チームと使用中チーム。</summary>
    Teams,
    /// <summary>相手チーム（対戦中の相手と、試合ごとの履歴）。</summary>
    Rival,
}

/// <summary>登録データ（育成済みポケモン・チーム）の保存先。</summary>
public interface IRosterStore
{
    /// <summary>登録データ全体を JSON（<see cref="RosterState"/>）で読む。まだ何も無ければ null。</summary>
    Task<string?> LoadAsync(CancellationToken ct = default);

    /// <summary>登録データ全体を保存する（ファイル・ブラウザ。クラウドではデータの移し替え用）。</summary>
    Task SaveAsync(string json, CancellationToken ct = default);

    /// <summary>
    /// 1 つのゲーム（<paramref name="dataSetKey"/>）の、<paramref name="part"/> の部分だけを保存する。
    /// <paramref name="state"/> は変更後の登録データ全体。ファイル・ブラウザは全体をそのまま保存し、
    /// クラウド（<see cref="RemoteRosterStore"/>）はその部分の API（/api/my/pokemon・/api/my/team）だけを呼ぶ。
    /// </summary>
    Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default);

    /// <summary>
    /// 他の場所（別の端末・ボット）からも書き換えられる保存先なら true（<see cref="RemoteRosterStore"/>）。
    /// <see cref="RosterRepository"/> は読み込んだ内容をキャッシュせず、読み書きのたびに読み直す。
    /// </summary>
    bool AlwaysReload => false;

    /// <summary>
    /// クラウドに保存しているなら、試合の履歴の窓口（<see cref="RemoteRosterStore"/>）。クラウドでは、履歴は登録データとは別にサーバーが持つ。
    /// ブラウザ・ファイル・メモリでは null（履歴は登録データの中にある）。
    /// </summary>
    ValueTask<IMatchHistory?> GetRemoteHistoryAsync(CancellationToken ct = default) => ValueTask.FromResult<IMatchHistory?>(null);

    /// <summary>
    /// 登録データの中に持てる試合の履歴の上限（ゲームごと）。保存先の容量が小さいとき（ブラウザの localStorage）だけ決める。
    /// null なら上限なし（ファイルなど）。クラウドは、サーバー側の上限（ゲームごとに 10,000 件）。
    /// </summary>
    int? LocalHistoryLimit => null;
}

/// <summary>ファイルに保存する（ボット用）。</summary>
public sealed class FileRosterStore : IRosterStore
{
    public string Path { get; }

    public FileRosterStore(string path)
    {
        Path = path;
    }

    public async Task<string?> LoadAsync(CancellationToken ct = default) =>
        File.Exists(Path) ? await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false) : null;

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, Path, overwrite: true);
    }

    public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
        SaveAsync(RosterRepository.Serialize(state), ct);
}

/// <summary>メモリ上だけ（テスト用）。</summary>
public sealed class MemoryRosterStore : IRosterStore
{
    public string? Json { get; set; }
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Json);
    public Task SaveAsync(string json, CancellationToken ct = default) { Json = json; return Task.CompletedTask; }
    public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
        SaveAsync(RosterRepository.Serialize(state), ct);
}

public sealed class RegisteredPokemon
{
    /// <summary>この版が知らない項目。新しい版のサイト・ボットが足した項目を、古い版が保存し直しても消さないために、そのまま持ち回る。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public int Id { get; set; }
    public int PokemonId { get; set; }
    /// <summary>表示用（データ更新で名前が変わっても一覧に出せるように保持）。</summary>
    public string PokemonName { get; set; } = "";
    public int HP { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int SpAttack { get; set; }
    public int SpDefense { get; set; }
    public int Speed { get; set; }
    public string Nature { get; set; } = "";
    public int? AbilityId { get; set; }
    /// <summary>
    /// 一時期、登録時に持ち物を保存していたときの項目。今は使わない（持ち物はチームに入れるときに選ぶ）が、
    /// そのころの保存データを読み書きしても消えないように残している。
    /// </summary>
    public int? ItemId { get; set; }
    public string? TeraType { get; set; }
    /// <summary>登録時に選んだ技（技の Id。0〜<see cref="MaxMoves"/> 個）。選ばなかった古い登録では空。</summary>
    public List<int> MoveIds { get; set; } = new();

    /// <summary>登録できる技の数。</summary>
    public const int MaxMoves = 4;

    [JsonIgnore]
    public StatSet EVs
    {
        get => new(HP, Attack, Defense, SpAttack, SpDefense, Speed);
        set { HP = value.HP; Attack = value.Attack; Defense = value.Defense; SpAttack = value.SpAttack; SpDefense = value.SpDefense; Speed = value.Speed; }
    }
}

/// <summary>「登録チームから選ぶ」の 1 件（登録ポケモンと、チームで持たせた持ち物）。</summary>
public sealed record RosterChoice(string Key, RegisteredPokemon Entry, Item? Item)
{
    public string Label => $"#{Entry.Id} {Entry.PokemonName}{(Item is null ? "" : " @" + Item.Name)}";
}

public sealed record RosterChoiceGroup(string Label, IReadOnlyList<RosterChoice> Choices);

/// <summary>推定で分かった、ある性格補正のときの防御（B か D）のポイントの範囲。</summary>
public sealed class RivalBand
{
    /// <summary>性格補正（1.1・1.0・0.9）。</summary>
    public double Modifier { get; set; }
    public int MinEv { get; set; }
    public int MaxEv { get; set; }
}

/// <summary>相手のダメージからの推定（scan）で絞り込んだ内容。推定のたびに、前の範囲と重なる部分に狭める。</summary>
public sealed class RivalScan
{
    /// <summary>推定した防御（<c>Defense</c> か <c>SpDefense</c>）。</summary>
    public string Defense { get; set; } = "";
    public List<RivalBand> Bands { get; set; } = new();
    /// <summary>HP のポイントの範囲（% で推定したとき。分からないときは null）。</summary>
    public int? HpMin { get; set; }
    public int? HpMax { get; set; }
    /// <summary>推定した回数。</summary>
    public int Count { get; set; }
    /// <summary>推定の根拠（どの技で何ダメージか）。</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>対戦中の相手のポケモン（1 体分）。同じポケモンは 1 体だけ。分かった部分だけを持つ（null は不明）。</summary>
public sealed class RivalPokemon
{
    /// <summary>この版が知らない項目。新しい版のサイト・ボットが足した項目を、古い版が保存し直しても消さないために、そのまま持ち回る。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public int Id { get; set; }
    public int PokemonId { get; set; }
    public string PokemonName { get; set; } = "";
    /// <summary>分かっているポイント（null は不明）。</summary>
    public int? HP { get; set; }
    public int? Attack { get; set; }
    public int? Defense { get; set; }
    public int? SpAttack { get; set; }
    public int? SpDefense { get; set; }
    public int? Speed { get; set; }
    /// <summary>分かっている性格の名前（不明なら null）。</summary>
    public string? Nature { get; set; }
    public int? AbilityId { get; set; }
    /// <summary>ダメージからの推定（scan）。防御（B・D）ごとに 1 つ。</summary>
    public List<RivalScan> Scans { get; set; } = new();
    /// <summary>選出された（試合に出てきた）。履歴で、選出した相手が分かるようにする。</summary>
    public bool Picked { get; set; }
    /// <summary>相手が使った（見えた）技の Id。0〜<see cref="RegisteredPokemon.MaxMoves"/> 個。</summary>
    public List<int> MoveIds { get; set; } = new();
    /// <summary>相手が持っている持ち物の Id（確定情報。不明なら null。メガシンカ後の姿は、メガストーンで確定なので保存しなくてよい）。</summary>
    public int? ItemId { get; set; }
    /// <summary>
    /// 性格の上がる能力・下がる能力のうち、分かっている方だけ（性格が 1 つに決まっていないとき。片方だけ分かるときがある）。
    /// 上がる・下がる能力の両方が分かって性格が決まったら <see cref="Nature"/> に入れて、ここは null に戻す。
    /// </summary>
    public Stat? NatureUp { get; set; }
    public Stat? NatureDown { get; set; }
    /// <summary>最後に更新した日時（ISO 8601）。</summary>
    public string Updated { get; set; } = "";

    /// <summary>ポイントも性格も特性も推定も何も分かっていない（名前だけ）か。</summary>
    [JsonIgnore]
    public bool IsNameOnly => HP is null && Attack is null && Defense is null && SpAttack is null && SpDefense is null && Speed is null
                              && Nature is null && NatureUp is null && NatureDown is null && AbilityId is null && ItemId is null && Scans.Count == 0 && MoveIds.Count == 0;

    /// <summary>推定（scan）をしたことがあるか。</summary>
    [JsonIgnore]
    public bool IsScanned => Scans.Count > 0;

    public RivalPokemon Clone() => new()
    {
        Id = Id, PokemonId = PokemonId, PokemonName = PokemonName,
        HP = HP, Attack = Attack, Defense = Defense, SpAttack = SpAttack, SpDefense = SpDefense, Speed = Speed,
        Nature = Nature, AbilityId = AbilityId, Updated = Updated, Picked = Picked, MoveIds = MoveIds.ToList(), ItemId = ItemId, NatureUp = NatureUp, NatureDown = NatureDown,
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
        Scans = Scans.Select(scan => new RivalScan
        {
            Defense = scan.Defense, HpMin = scan.HpMin, HpMax = scan.HpMax, Count = scan.Count,
            Bands = scan.Bands.Select(b => new RivalBand { Modifier = b.Modifier, MinEv = b.MinEv, MaxEv = b.MaxEv }).ToList(),
            Notes = scan.Notes.ToList(),
        }).ToList(),
    };
}

/// <summary>終わった試合の記録（試合ごとの、そのとき分かった相手チーム）。</summary>
public sealed class RivalMatch
{
    /// <summary>この版が知らない項目。新しい版のサイト・ボットが足した項目を、古い版が保存し直しても消さないために、そのまま持ち回る。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public int Id { get; set; }
    /// <summary>
    /// 重複判定用の ID（クラウドに記録するときだけ付ける）。応答が届かずに、同じ試合をもう一度記録しようとしても、サーバーが同じ ClientId の試合を
    /// 足さずに、すでにある試合を返す（二重に記録されない）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; set; }
    /// <summary>試合を終えた日時（ISO 8601）。</summary>
    public string Date { get; set; } = "";
    public string Memo { get; set; } = "";
    public List<RivalPokemon> Foes { get; set; } = new();
}

public sealed class TeamSlot
{
    /// <summary>この版が知らない項目。新しい版のサイト・ボットが足した項目を、古い版が保存し直しても消さないために、そのまま持ち回る。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public int EntryId { get; set; }
    public int? ItemId { get; set; }
}

public sealed class Team
{
    /// <summary>この版が知らない項目。新しい版のサイト・ボットが足した項目を、古い版が保存し直しても消さないために、そのまま持ち回る。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public int Number { get; set; }
    public List<TeamSlot> Slots { get; set; } = new();

    /// <summary>
    /// このチームで戦うバトル形式。"Singles"（シングル）か "Doubles"（ダブル）。省略（null）はシングル。
    /// 古い版が保存したチームには無いので、省略はシングルとして読む。
    /// </summary>
    public string? Format { get; set; }

    /// <summary>このチームのバトル形式（<see cref="Format"/> を読み替えたもの。読み取り専用）。</summary>
    [JsonIgnore]
    public BattleFormat BattleFormat => TeamFormats.Parse(Format);

    /// <summary>チームを区別する鍵（バトル形式 + チーム番号）。チーム番号は、シングルとダブルで別々に数える。</summary>
    [JsonIgnore]
    public (BattleFormat Format, int Number) Key => (BattleFormat, Number);
}

/// <summary>バトル形式ごとのチームの呼び方（返信・画面の表記をそろえる）。</summary>
public static class TeamNames
{
    /// <summary>「シングルのチーム1」「ダブルのチーム2」。</summary>
    public static string Full(BattleFormat format, int number) => $"{TeamFormats.Label(format)}のチーム{number}";
}

/// <summary>チームのバトル形式（シングル・ダブル）の読み書き。サイト・コマンド・クラウドで同じ表記にそろえる。</summary>
public static class TeamFormats
{
    /// <summary>保存する名前（"Singles" / "Doubles"）。</summary>
    public static string Name(BattleFormat format) => format == BattleFormat.Doubles ? "Doubles" : "Singles";

    /// <summary>表示する名前（シングル / ダブル）。</summary>
    public static string Label(BattleFormat format) => format == BattleFormat.Doubles ? "ダブル" : "シングル";

    /// <summary>保存してある名前を読む。null・空・知らない値は、シングル。</summary>
    public static BattleFormat Parse(string? stored) =>
        string.Equals(stored?.Trim(), "Doubles", StringComparison.OrdinalIgnoreCase) ? BattleFormat.Doubles : BattleFormat.Singles;

    /// <summary>
    /// 言葉の並びの最後が「シングル」「ダブル」なら、それを形式として切り離す（持ち物の名前などとは読まない）。
    /// 切り離すのは、残りが <paramref name="minRemaining"/> 語より多いときだけ（例: <c>1 1 ダブル</c> は切り離し、<c>1 ダブル</c> はそのまま）。
    /// </summary>
    public static List<string> SplitTrailingFormat(IReadOnlyList<string> words, int minRemaining, out BattleFormat? format)
    {
        format = null;
        if (words.Count > minRemaining && TryParseWord(words[^1], out var parsed))
        {
            format = parsed;
            return words.Take(words.Count - 1).ToList();
        }
        return words.ToList();
    }

    /// <summary>保存してある名前として正しいか（null か、"Singles" / "Doubles"）。クラウドへ送る前の確認に使う。</summary>
    public static bool IsValidStored(string? stored) =>
        stored is null || stored is "Singles" or "Doubles";

    /// <summary>
    /// コマンドの言葉（シングル / ダブル / しんぐる / だぶる / single(s) / double(s) / dbl）を読む。前後の空白と大文字小文字は区別しない。
    /// （全角の英数字は、コマンド全体を読む前の正規化で半角になっている。）バトル形式を表す言葉でなければ false。
    /// </summary>
    public static bool TryParseWord(string? word, out BattleFormat format)
    {
        format = BattleFormat.Singles;
        var w = (word ?? "").Trim().ToLowerInvariant();
        switch (w)
        {
            case "シングル" or "しんぐる" or "single" or "singles":
                format = BattleFormat.Singles;
                return true;
            case "ダブル" or "だぶる" or "double" or "doubles" or "dbl":
                format = BattleFormat.Doubles;
                return true;
            default:
                return false;
        }
    }
}

public sealed class DataSetRoster
{
    /// <summary>この版が知らない項目。新しい版のサイト・ボットが足した項目を、古い版が保存し直しても消さないために、そのまま持ち回る。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public int NextId { get; set; } = 1;
    public List<RegisteredPokemon> Pokemon { get; set; } = new();
    public List<Team> Teams { get; set; } = new();
    /// <summary>
    /// 古い版が保存した「使用中のチーム」（バトル形式に関係なく 1 つ）。新しい版は使わない（読み込み時に <see cref="ActiveTeams"/> へ移し、書かない）。
    /// </summary>
    [Obsolete("使用中のチームは形式ごとです。ActiveNumber(format) / ActiveTeamOf(format) / ActiveTeams を使ってください（この値は読み込み時に必ず null になります）")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ActiveTeam { get; set; }

    /// <summary>
    /// バトル形式ごとの使用中のチーム番号（キーは "Singles" / "Doubles"。値が無い・null は使用中なし）。
    /// 書いていない（null）ときは、使用中なし。古いデータ（<see cref="RosterState.Version"/> が 1）だけ、読み込み時に <see cref="UpgradeFormats"/> で作り直す。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int?>? ActiveTeams { get; set; }

    /// <summary>そのバトル形式のチーム。無ければ null。</summary>
    public Team? FindTeam(BattleFormat format, int number) =>
        Teams.FirstOrDefault(t => t.BattleFormat == format && t.Number == number);

    /// <summary>そのバトル形式で使用中のチーム番号（そのチームが無ければ null）。</summary>
    public int? ActiveNumber(BattleFormat format)
    {
        if (ActiveTeams is null || !ActiveTeams.TryGetValue(TeamFormats.Name(format), out var n) || n is not { } number) return null;
        return FindTeam(format, number) is null ? null : number;
    }

    /// <summary>そのバトル形式で使用中のチーム。使用中が無ければ null。</summary>
    public Team? ActiveTeamOf(BattleFormat format) => ActiveNumber(format) is { } n ? FindTeam(format, n) : null;

    /// <summary>保存してある使用中の番号（そのチームがもう無くても、書いてあるまま）。チームを消すときに、使用中の印を確実に外すために使う。</summary>
    internal int? StoredActiveNumber(BattleFormat format) =>
        ActiveTeams is not null && ActiveTeams.TryGetValue(TeamFormats.Name(format), out var n) ? n : null;

    /// <summary>そのバトル形式の使用中が <paramref name="number"/> なら外す（同じ番号のチームを後から作り直しても、勝手に使用中に戻らない）。</summary>
    internal void ReleaseActive(BattleFormat format, int number)
    {
        if (StoredActiveNumber(format) == number) SetActiveNumber(format, null);
    }

    /// <summary>そのバトル形式の使用中のチームを決める（null で解除）。</summary>
    public void SetActiveNumber(BattleFormat format, int? number)
    {
        ActiveTeams ??= new Dictionary<string, int?>();
        ActiveTeams[TeamFormats.Name(format)] = number;
    }

    /// <summary>
    /// バトル形式ごとにチーム番号を数える前の古いデータ（保存した <see cref="RosterState.Version"/> が 1 で、<see cref="ActiveTeams"/> が無い）を新しい形にする。
    /// 古いデータのチームは、保存してあったバトル形式が何でも、すべてシングルのチームとして扱う（番号は全体で一意なので、衝突しない）。
    /// 使用中のチームは、シングルの使用中になる。何度呼んでも結果は同じ（新しい形になったら何もしない）。
    /// データの世代が 2 以上なら、<see cref="ActiveTeams"/> が無くても（コードで作ったデータなど）チームの形式は変えない。
    /// </summary>
    /// <param name="legacyVersion">保存したデータの世代が 1（古い）か。</param>
    internal void UpgradeFormats(bool legacyVersion = false)
    {
        Teams ??= new();
        if (ActiveTeams is null)
        {
            ActiveTeams = new Dictionary<string, int?>();
            if (legacyVersion)
            {
                foreach (var team in Teams) team.Format = TeamFormats.Name(BattleFormat.Singles);
                if (ActiveTeam is { } legacy && Teams.Any(t => t.Number == legacy)) SetActiveNumber(BattleFormat.Singles, legacy);
            }
            else if (ActiveTeam is { } stray && Teams.FirstOrDefault(t => t.Number == stray) is { } strayTeam)
            {
                SetActiveNumber(strayTeam.BattleFormat, stray);
            }
            ActiveTeam = null;
            return;
        }
        ActiveTeam = null;
        // 新しい形でも、存在しないチームを指す使用中は外す
        foreach (var key in ActiveTeams.Keys.ToList())
        {
            var format = TeamFormats.Parse(key);
            if (ActiveTeams[key] is not { } n || FindTeam(format, n) is null) ActiveTeams.Remove(key);
        }
    }
    /// <summary>対戦中の相手チーム（同じポケモンは 1 体）。</summary>
    public int NextRivalId { get; set; } = 1;
    public List<RivalPokemon> Rivals { get; set; } = new();
    /// <summary>終わった試合の記録（上限なし）。</summary>
    public int NextMatchId { get; set; } = 1;
    public List<RivalMatch> History { get; set; } = new();

    /// <summary>
    /// 手で書き換えたデータや古いデータで、次に振る ID が今ある ID と重なっていると、同じ ID が 2 つできる。
    /// 今ある最大の ID の次より小さくならないようにそろえる。
    /// </summary>
    public void RepairIds()
    {
        Normalize();
        // 重なった ID は、2 つ目以降に新しい ID を振る（そのままだと、クラウドへ移すときに重複として断られる。チームの枠は最初のものを指したまま）
        NextId = RenumberDuplicates(Pokemon, p => p.Id, (p, id) => p.Id = id, NextId);
        NextRivalId = RenumberDuplicates(Rivals, r => r.Id, (r, id) => r.Id = id, NextRivalId);
        NextMatchId = RenumberDuplicates(History, m => m.Id, (m, id) => m.Id = id, NextMatchId);
    }

    /// <summary>読み込んだ JSON が null を書いていても、空の一覧として扱う（一覧の中の null も取り除く）。</summary>
    internal void Normalize(bool legacyVersion = false)
    {
        Pokemon ??= new();
        Teams ??= new();
        Rivals ??= new();
        History ??= new();
        Pokemon.RemoveAll(p => p is null);
        Teams.RemoveAll(t => t is null);
        Rivals.RemoveAll(r => r is null);
        History.RemoveAll(m => m is null);
        UpgradeFormats(legacyVersion);
    }

    /// <summary>ID の重複を直し、次に振る ID（今ある最大の ID の次より小さくならない）を返す。</summary>
    private static int RenumberDuplicates<T>(List<T> items, Func<T, int> getId, Action<T, int> setId, int next)
    {
        var seen = new HashSet<int>();
        var max = items.Count == 0 ? 0 : items.Max(getId);
        var nextId = (int)Math.Min(int.MaxValue - 1L, Math.Max((long)next, max + 1L));
        foreach (var item in items)
        {
            if (seen.Add(getId(item))) continue;
            setId(item, nextId);
            seen.Add(nextId++);
        }
        return nextId;
    }

    /// <summary>大文字小文字だけ違うキーで分かれていたデータを、このゲームのデータに足す（ID は <see cref="RepairIds"/> が直す）。</summary>
    internal void MergeFrom(DataSetRoster other)
    {
        // 同じ ID のポケモンは、このゲームのものを残し、other 側は新しい ID へ振り直す（other のチームの枠も、同じ新しい ID を指すように読み替える）。
        // そのままにすると、枠が別のポケモンを指してしまう。
        // 判定は「このゲームにある ID」ではなく「すでに使った ID（このゲームのもの・取り込み済みのもの・振り直した新しい ID）」で行う。
        // 次に振る ID は、両方の NextId と、両方の今ある最大の ID より後から始める（other.NextId が古くても、振り直した ID が後から来る ID と重ならない）。
        var taken = new HashSet<int>(Pokemon.Select(p => p.Id));
        var next = Math.Max(
            Math.Max(NextId, other.NextId),
            Math.Max(taken.Count == 0 ? 1 : taken.Max() + 1, other.Pokemon.Count == 0 ? 1 : other.Pokemon.Max(p => p.Id) + 1));
        // other の ID（元の値）→ 取り込んだ後の ID。同じ ID が other の中で 2 つあるときは、最初のものに合わせる（RenumberDuplicates と同じ）
        var pokemonMap = new Dictionary<int, int>();
        foreach (var p in other.Pokemon)
        {
            var original = p.Id;
            if (taken.Add(original))
            {
                pokemonMap.TryAdd(original, original);
            }
            else
            {
                while (taken.Contains(next)) next++;
                taken.Add(next);
                pokemonMap.TryAdd(original, next);
                p.Id = next;
                next++;
            }
            Pokemon.Add(p);
        }
        Rivals.AddRange(other.Rivals);
        History.AddRange(other.History);

        // チームは捨てず、同じ番号（形式ごと）のものは、このゲームの最後の番号の次へ振り直して両方残す（片方を捨てない）
        var teamNumberMap = new Dictionary<(BattleFormat Format, int Number), int>();
        var conflicts = new List<Team>();
        foreach (var team in other.Teams)
        {
            foreach (var slot in team.Slots)
                if (pokemonMap.TryGetValue(slot.EntryId, out var mapped)) slot.EntryId = mapped;
            if (FindTeam(team.BattleFormat, team.Number) is null) Teams.Add(team);
            else conflicts.Add(team);
        }
        foreach (var team in conflicts)
        {
            var format = team.BattleFormat;
            var number = Teams.Where(t => t.BattleFormat == format).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
            teamNumberMap[(format, team.Number)] = number;
            team.Number = number;
            Teams.Add(team);
        }

        NextId = next;
        NextRivalId = Math.Max(NextRivalId, other.NextRivalId);
        NextMatchId = Math.Max(NextMatchId, other.NextMatchId);
        foreach (var format in new[] { BattleFormat.Singles, BattleFormat.Doubles })
            if (ActiveNumber(format) is null && other.ActiveNumber(format) is { } active)
                SetActiveNumber(format, teamNumberMap.TryGetValue((format, active), out var renumbered) ? renumbered : active);
    }
}

public sealed class RosterState
{
    /// <summary>この版が知らない項目。新しい版のサイト・ボットが足した項目を、古い版が保存し直しても消さないために、そのまま持ち回る。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>データの世代。2 は、チーム番号をシングルとダブルで別々に数える形（<see cref="DataSetRoster.ActiveTeams"/> を持つ）。1 でも読める。</summary>
    public int Version { get => _version ?? 2; set => _version = value; }

    private int? _version;

    /// <summary>読んだ JSON に <c>Version</c> が無かったか（コードで作った状態も true）。世代が分からないデータは、古いデータとして読み替える。</summary>
    internal bool VersionMissing => _version is null;
    public Dictionary<string, DataSetRoster> DataSets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DataSetRoster For(string dataSetKey)
    {
        if (!DataSets.TryGetValue(dataSetKey, out var r))
            DataSets[dataSetKey] = r = new DataSetRoster { ActiveTeams = new Dictionary<string, int?>() };
        return r;
    }
}

/// <summary>
/// 登録データの読み書きと、登録内容から <see cref="PokemonBuild"/> を組み立てる処理。
/// Web とボットで共通。保存先だけ <see cref="IRosterStore"/> で差し替える。
/// </summary>
/// <remarks>
/// 変更する操作は「読み込む → 変更する → 保存する」を 1 つずつ（同時に走らないように）行う。
/// 保存先が <see cref="IRosterStore.AlwaysReload"/> のとき（クラウド）は毎回読み直し、保存時に他の場所の更新と競合したら
/// （<see cref="RosterConflictException"/>）読み直して最大 <see cref="MaxConflictRetries"/> 回やり直す。
/// 保存は変えた部分だけ（ポケモンの操作はポケモン、チームの操作はチーム。<see cref="IRosterStore.SavePartAsync"/>）。
/// ポケモンを消したときにチームから外す処理は、クラウドではサーバーが同じ処理の中で行う（ここでも同じ変更をして表示に使う）。
/// </remarks>
public sealed class RosterRepository
{
    /// <summary>ID から登録ポケモンを引く表。ID が重なっているデータでも落ちないよう、最初のものを使う。</summary>
    private static Dictionary<int, RegisteredPokemon> ById(IEnumerable<RegisteredPokemon> pokemon)
    {
        var byId = new Dictionary<int, RegisteredPokemon>();
        foreach (var p in pokemon) byId.TryAdd(p.Id, p);
        return byId;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>保存が競合したときにやり直す回数。</summary>
    public const int MaxConflictRetries = 3;

    private readonly IRosterStore _store;
    // 読み込み・保存を 1 つずつにする（クラウドでは、読んだ版を基準に保存するため）
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RosterState? _state;

    public RosterRepository(IRosterStore store)
    {
        _store = store;
    }

    /// <summary>保存先。</summary>
    public IRosterStore Store => _store;

    /// <summary>
    /// 登録データ。ファイル・ブラウザの保存先では一度読んだら使い回す。
    /// クラウド（<see cref="IRosterStore.AlwaysReload"/>）では呼ぶたびに読み直す。
    /// </summary>
    public async Task<RosterState> GetAsync(CancellationToken ct = default)
    {
        if (_state is not null && !_store.AlwaysReload) return _state;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.AlwaysReload) _state = null;
            return await LoadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is null) return;
            await _store.SaveAsync(Serialize(_state), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>キャッシュを捨てて次回読み直す（別プロセスが書いた場合や、保存先を切り替えた場合など）。</summary>
    public void Invalidate() => _state = null;

    /// <summary>登録データを JSON にする（保存先に書く形式）。</summary>
    public static string Serialize(RosterState state) => JsonSerializer.Serialize(state, JsonOptions);

    /// <summary>保存先の JSON を読む。空なら新しい登録データ。</summary>
    public static RosterState Deserialize(string? json)
    {
        var state = string.IsNullOrWhiteSpace(json) ? new RosterState() : JsonSerializer.Deserialize<RosterState>(json!, JsonOptions) ?? new RosterState();
        // 世代 1 のデータ（チーム番号を形式ごとに数える前）は、読み込み時にチームをすべてシングルとして扱う
        var legacyVersion = state.VersionMissing || state.Version < 2;
        // 読み込むと、ゲームのキーの比較（大文字小文字を区別しない）が標準に戻るので作り直す。ID の採番は、今ある ID より後にそろえる
        var sets = new Dictionary<string, DataSetRoster>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, set) in state.DataSets ?? new())
        {
            var loaded = set ?? new DataSetRoster();
            loaded.Normalize(legacyVersion);
            // 大文字小文字だけ違うキーが 2 つあるときは、中身を 1 つにまとめる（片方を捨てない）
            if (sets.TryGetValue(key, out var existing)) existing.MergeFrom(loaded);
            else sets[key] = loaded;
        }
        state.DataSets = sets;
        // チームの番号を形式ごとに数える形に、すべてのゲームを読み替えた（Normalize）ので、データの世代も 2 にそろえる
        state.Version = Math.Max(state.Version, 2);
        foreach (var set in sets.Values) set.RepairIds();
        return state;
    }

    private async Task<RosterState> LoadCoreAsync(CancellationToken ct)
    {
        if (_state is not null) return _state;
        RosterState state;
        try
        {
            state = Deserialize(await _store.LoadAsync(ct).ConfigureAwait(false));
        }
        catch (JsonException ex)
        {
            // 保存先の内容が壊れている（別のバージョンのクライアントなどが型違いの値を書いた場合など）。素の JsonException を各画面に漏らさず、読めなかった扱いにする
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "保存されている登録データの形式が正しくないため、読み込めませんでした。データ共有ページでアクセスキーを入れて共有すると、クラウドのデータに置き換えられます（このブラウザの登録データは削除されます）", ex);
        }
        _state = state;
        return state;
    }

    /// <summary>
    /// 読み込む → <paramref name="apply"/> で変更する → 変更があれば保存する。
    /// クラウドでは毎回読み直し、競合したら読み直してやり直す（<paramref name="apply"/> は何度か呼ばれることがある）。
    /// </summary>
    private async Task<T> MutateAsync<T>(string dataSetKey, RosterPart part, Func<DataSetRoster, (T Result, bool Changed)> apply, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (_store.AlwaysReload) _state = null;
                var state = await LoadCoreAsync(ct).ConfigureAwait(false);
                var (result, changed) = apply(state.For(dataSetKey));
                if (!changed) return result;
                try
                {
                    await _store.SavePartAsync(state, dataSetKey, part, ct).ConfigureAwait(false);
                    return result;
                }
                catch (RosterConflictException) when (attempt < MaxConflictRetries)
                {
                    // 他の場所が先に書いていた。読み直してやり直す
                    _state = null;
                }
                catch
                {
                    // 保存できなかった変更は、どの保存先でも捨てる（残すと、次の保存で「失敗した」と伝えた変更まで一緒に書かれる）。
                    // 次に読むときは、保存先の内容
                    _state = null;
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- 相手チーム（対戦中の相手と、試合ごとの履歴）

    /// <summary>
    /// 相手チームを読んで変更する（<paramref name="apply"/> が (結果, 変更したか) を返す。クラウドでは競合したら読み直して何度か呼ばれる）。
    /// 登録 ID は <see cref="DataSetRoster.NextRivalId"/> から振る。
    /// </summary>
    public Task<T> ChangeRivalsAsync<T>(string dataSetKey, Func<DataSetRoster, (T Result, bool Changed)> apply, CancellationToken ct = default) =>
        MutateAsync(dataSetKey, RosterPart.Rival, apply, ct);

    /// <summary>ブラウザ（localStorage）に保存できる試合の履歴の上限（ゲームごと）。保存領域（約 5 MB）に収まるようにする。</summary>
    public const int BrowserHistoryLimit = 2000;

    /// <summary>EndMatchRemoteAsync を 1 つずつにする（同じ相手を 2 回記録しない）。</summary>
    private readonly SemaphoreSlim _endGate = new(1, 1);

    /// <summary>
    /// 試合を終える。対戦中の相手を、名前だけの相手も含めて全員履歴に移し（選出・技・ポイント・性格・特性・推定も一緒に残す）、対戦中の相手は全部消す。
    /// 対戦中の相手がいなければ履歴は作らない（<see cref="EndMatchResult.Match"/> は null）。
    /// 履歴が上限に達しているときは <see cref="HistoryLimitException"/>（何も変えない）。
    /// </summary>
    public async Task<EndMatchResult> EndMatchAsync(string dataSetKey, string? memo, DateTimeOffset now, CancellationToken ct = default)
    {
        if (await _store.GetRemoteHistoryAsync(ct).ConfigureAwait(false) is { } remote)
            return await EndMatchRemoteAsync(remote, dataSetKey, memo, now, ct).ConfigureAwait(false);
        return await ChangeRivalsAsync(dataSetKey, roster =>
        {
            if (roster.Rivals.Count == 0) return (new EndMatchResult(null, 0), false);
            if (_store.LocalHistoryLimit is { } limit && roster.History.Count >= limit)
                throw new HistoryLimitException($"このブラウザに保存できる試合の履歴は {limit:N0} 件までです。データ共有（クラウド）を使うか、古い履歴を削除してください");
            // 振り返りのため、選出しなかった相手も含めて全員を履歴に移す
            var all = roster.Rivals.Select(r => r.Clone()).ToList();
            var match = new RivalMatch { Id = roster.NextMatchId++, Date = now.ToString("o"), Memo = (memo ?? "").Trim(), Foes = all };
            roster.History.Add(match);
            roster.Rivals.Clear();
            return (new EndMatchResult(match, all.Count), true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>end のやり直しを見分ける印。同じ相手の組なら同じ文字列になる。サーバーの上限（100 文字）を超えるほど長いときは付けない（二重の防止が効かないだけ）。</summary>
    private static string? EndClientId(IEnumerable<RivalPokemon> foes)
    {
        var id = "end:" + string.Join(",", foes.Select(r => r.Id).OrderBy(i => i));
        return id.Length <= 100 ? id : null;
    }

    /// <summary>
    /// クラウドで試合を終える。履歴は別の API なので、先に履歴へ足し（上限なら何も変えずに失敗）、そのあと対戦中の相手を消す。
    /// 履歴に足したあと、相手を消せなかったときは、その旨を伝える（履歴には記録済みなので、もう一度 end せず clear -r で消す）。
    /// </summary>
    private async Task<EndMatchResult> EndMatchRemoteAsync(IMatchHistory history, string dataSetKey, string? memo, DateTimeOffset now, CancellationToken ct)
    {
        await _endGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rivals = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey).Rivals;
            if (rivals.Count == 0) return new EndMatchResult(null, 0);
            var all = rivals.Select(r => r.Clone()).ToList();
            // 同じ相手（Id は使い回さない）での end のやり直しは、同じ ClientId になる。記録の応答が届かなかった・相手を消せなかったときに、
            // もう一度 end しても、履歴に二重に記録されず、記録済みの試合に紐づけて相手を消せる
            var match = new RivalMatch
            {
                ClientId = EndClientId(all),
                Date = now.ToString("o"),
                Memo = (memo ?? "").Trim(),
                Foes = all,
            };
            var recorded = await history.AppendAsync(dataSetKey, match, ct).ConfigureAwait(false);
            // 履歴に記録した相手だけを消す（読んでから消すまでの間に、BOT や別の端末が足した相手は残す）
            var recordedIds = all.Select(r => r.Id).ToHashSet();
            try
            {
                await ChangeRivalsAsync(dataSetKey, roster => { var n = roster.Rivals.RemoveAll(r => recordedIds.Contains(r.Id)); return (n, n > 0); }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                    $"試合は履歴 #{recorded.Id} に記録しましたが、対戦中の相手を消せませんでした。もう一度 end してください（対戦中の相手が変わっていなければ、記録済みの試合に紐づけて相手を消します。履歴は二重になりません。相手を足した・消したあとは、二重に記録されることがあります）", ex);
            }
            return new EndMatchResult(recorded, all.Count);
        }
        finally
        {
            _endGate.Release();
        }
    }

    // ---- 試合の履歴（クラウドはサーバーの履歴、それ以外は登録データの中）

    /// <summary>試合の一覧（新しい順）。<paramref name="beforeId"/> を付けると、その Id より前の試合から。</summary>
    public async Task<MatchPage> ListHistoryAsync(string dataSetKey, int limit, int? beforeId = null, CancellationToken ct = default)
    {
        if (await _store.GetRemoteHistoryAsync(ct).ConfigureAwait(false) is { } remote)
            return await remote.ListAsync(dataSetKey, limit, beforeId, ct).ConfigureAwait(false);
        limit = Math.Clamp(limit, 1, RemoteRosterStore.MaxPageSize);
        var history = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey).History;
        var older = history.Where(m => beforeId is null || m.Id < beforeId).OrderByDescending(m => m.Id).Take(limit + 1).ToList();
        return new MatchPage(history.Count, older.Take(limit).ToList(), older.Count > limit);
    }

    /// <summary>1 試合を読む。無ければ null。</summary>
    public async Task<RivalMatch?> GetMatchAsync(string dataSetKey, int id, CancellationToken ct = default)
    {
        if (await _store.GetRemoteHistoryAsync(ct).ConfigureAwait(false) is { } remote)
            return await remote.GetAsync(dataSetKey, id, ct).ConfigureAwait(false);
        return (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey).History.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>1 試合を消す。無ければ false。</summary>
    public async Task<bool> RemoveMatchAsync(string dataSetKey, int id, CancellationToken ct = default)
    {
        if (await _store.GetRemoteHistoryAsync(ct).ConfigureAwait(false) is { } remote)
            return await remote.RemoveAsync(dataSetKey, id, ct).ConfigureAwait(false);
        return await ChangeRivalsAsync(dataSetKey, roster => { var n = roster.History.RemoveAll(m => m.Id == id); return (n > 0, n > 0); }, ct).ConfigureAwait(false);
    }

    /// <summary>新しい <paramref name="keepLatest"/> 件を残して、古い試合を消す。消した件数を返す。</summary>
    public async Task<int> TrimHistoryAsync(string dataSetKey, int keepLatest, CancellationToken ct = default)
    {
        if (await _store.GetRemoteHistoryAsync(ct).ConfigureAwait(false) is { } remote)
            return await remote.TrimAsync(dataSetKey, keepLatest, ct).ConfigureAwait(false);
        return await ChangeRivalsAsync(dataSetKey, roster =>
        {
            var keep = roster.History.OrderByDescending(m => m.Id).Take(keepLatest).Select(m => m.Id).ToHashSet();
            var n = roster.History.RemoveAll(m => !keep.Contains(m.Id));
            return (n, n > 0);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>対戦中の相手を全部消す（履歴には残さない）。消した数を返す。</summary>
    public Task<int> ClearRivalsAsync(string dataSetKey, CancellationToken ct = default) =>
        ChangeRivalsAsync(dataSetKey, roster =>
        {
            var count = roster.Rivals.Count;
            roster.Rivals.Clear();
            return (count, count > 0);
        }, ct);

    // ---- 登録ポケモン

    public async Task<RegisteredPokemon> AddPokemonAsync(string dataSetKey, Pokemon pokemon, StatSet evs, Nature nature, Ability? ability, CancellationToken ct = default) =>
        await AddPokemonAsync(dataSetKey, pokemon, evs, nature, ability, null, ct).ConfigureAwait(false);

    /// <param name="dataSetKey">登録先のゲームのキー。</param>
    /// <param name="pokemon">登録するポケモン。</param>
    /// <param name="evs">努力値。</param>
    /// <param name="nature">性格。</param>
    /// <param name="ability">特性（未設定なら null）。</param>
    /// <param name="moveIds">登録する技の Id（重複と 5 個目以降は無視する）。</param>
    /// <param name="ct">キャンセル用。</param>
    public async Task<RegisteredPokemon> AddPokemonAsync(string dataSetKey, Pokemon pokemon, StatSet evs, Nature nature, Ability? ability, IEnumerable<int>? moveIds, CancellationToken ct = default)
    {
        var moves = (moveIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().Take(RegisteredPokemon.MaxMoves).ToList();
        return await MutateAsync(dataSetKey, RosterPart.Pokemon, roster =>
        {
            var entry = new RegisteredPokemon
            {
                Id = roster.NextId++,
                PokemonId = pokemon.Id,
                PokemonName = pokemon.Name,
                EVs = evs,
                Nature = nature.Name,
                AbilityId = ability?.Id,
                MoveIds = moves,
            };
            roster.Pokemon.Add(entry);
            return (entry, true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 登録済みのポケモンの、ポイント・性格・特性・技を更新する（ポケモン自体・登録 ID・チームでの位置・持ち物は変えない）。
    /// 登録が無ければ null。
    /// </summary>
    public async Task<RegisteredPokemon?> UpdatePokemonAsync(string dataSetKey, int entryId, StatSet evs, Nature nature, Ability? ability, IEnumerable<int>? moveIds, CancellationToken ct = default)
    {
        var moves = (moveIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().Take(RegisteredPokemon.MaxMoves).ToList();
        return await MutateAsync<RegisteredPokemon?>(dataSetKey, RosterPart.Pokemon, roster =>
        {
            var entry = roster.Pokemon.FirstOrDefault(p => p.Id == entryId);
            if (entry is null) return (null, false);
            entry.EVs = evs;
            entry.Nature = nature.Name;
            entry.AbilityId = ability?.Id;
            entry.MoveIds = moves;
            return (entry, true);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemovePokemonAsync(string dataSetKey, int entryId, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Pokemon, roster =>
        {
            var removed = roster.Pokemon.RemoveAll(p => p.Id == entryId) > 0;
            foreach (var team in roster.Teams)
                team.Slots.RemoveAll(s => s.EntryId == entryId);
            RemoveEmptyTeams(roster);
            return (removed, removed);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>0 体になったチームを削除する（使用中ならその形式の使用も解除）。削除したチーム（バトル形式と番号）を返す。</summary>
    public static IReadOnlyList<(BattleFormat Format, int Number)> RemoveEmptyTeams(DataSetRoster roster)
    {
        var empty = roster.Teams.Where(t => t.Slots.Count == 0).Select(t => t.Key).ToList();
        roster.Teams.RemoveAll(t => t.Slots.Count == 0);
        foreach (var (format, number) in empty) roster.ReleaseActive(format, number);
        return empty;
    }

    // ---- チーム（バトル形式 + 番号で 1 つのチームを指す。番号はシングルとダブルで別々）

    /// <summary>チームに登録ポケモンを入れる（無ければそのバトル形式のチームを作る。もう入っていれば持ち物を更新する）。</summary>
    public async Task<(Team Team, TeamSlot Slot, bool Replaced)> AddToTeamAsync(string dataSetKey, BattleFormat format, int teamNumber, int entryId, Item? item, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            if (roster.Pokemon.All(p => p.Id != entryId))
                throw new ArgumentException($"登録ID {entryId} はありません");
            var team = roster.FindTeam(format, teamNumber);
            if (team is null)
            {
                team = new Team { Number = teamNumber, Format = TeamFormats.Name(format) };
                roster.Teams.Add(team);
                SortTeams(roster.Teams);
            }
            var existing = team.Slots.FirstOrDefault(s => s.EntryId == entryId);
            var replaced = existing is not null;
            if (existing is null)
            {
                if (team.Slots.Count >= 6)
                    throw new InvalidOperationException($"{TeamNames.Full(format, teamNumber)}は既に 6 体います");
                existing = new TeamSlot { EntryId = entryId };
                team.Slots.Add(existing);
            }
            existing.ItemId = item?.Id;
            return ((team, existing, replaced), true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>チームの並び順（番号の小さい順。同じ番号ならシングルが先）。</summary>
    private static void SortTeams(List<Team> teams) =>
        teams.Sort((a, b) => a.Number != b.Number ? a.Number.CompareTo(b.Number) : a.BattleFormat.CompareTo(b.BattleFormat));

    /// <summary>
    /// チームを別のバトル形式へ移す（その形式で同じ番号か <paramref name="newNumber"/> のチームが既にあれば移さない。番号は自動では振り替えない）。
    /// 使用中だったチームを移すと、移す前の形式の使用中は解除する（移した先の使用中は変えない）。
    /// </summary>
    public async Task<TeamMoveResult> MoveTeamAsync(string dataSetKey, BattleFormat from, int teamNumber, BattleFormat to, int? newNumber = null, CancellationToken ct = default)
    {
        var target = newNumber ?? teamNumber;
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            var team = roster.FindTeam(from, teamNumber);
            if (team is null) return (new TeamMoveResult(TeamMoveStatus.NotFound, target, false), false);
            if (from == to && target == teamNumber) return (new TeamMoveResult(TeamMoveStatus.Unchanged, target, false), false);
            if (roster.FindTeam(to, target) is not null) return (new TeamMoveResult(TeamMoveStatus.Occupied, target, false), false);
            var wasActive = roster.StoredActiveNumber(from) == teamNumber;
            team.Format = TeamFormats.Name(to);
            team.Number = target;
            roster.ReleaseActive(from, teamNumber);   // 移す前の番号の使用中は残さない（同じ番号を作り直しても、使用中に戻らない）
            SortTeams(roster.Teams);
            return (new TeamMoveResult(TeamMoveStatus.Moved, target, wasActive), true);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveFromTeamAsync(string dataSetKey, BattleFormat format, int teamNumber, Func<RegisteredPokemon, bool> predicate, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            var team = roster.FindTeam(format, teamNumber);
            if (team is null) return (false, false);
            var byId = ById(roster.Pokemon);
            var removed = team.Slots.RemoveAll(s => byId.TryGetValue(s.EntryId, out var p) && predicate(p)) > 0;
            RemoveEmptyTeams(roster);
            return (removed, removed);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveTeamAsync(string dataSetKey, BattleFormat format, int teamNumber, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            var removed = roster.Teams.RemoveAll(t => t.BattleFormat == format && t.Number == teamNumber) > 0;
            roster.ReleaseActive(format, teamNumber);
            return (removed, removed);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>そのバトル形式の使用中のチームにする。チームが無ければ false。</summary>
    public async Task<bool> UseTeamAsync(string dataSetKey, BattleFormat format, int teamNumber, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            if (roster.FindTeam(format, teamNumber) is null) return (false, false);
            roster.SetActiveNumber(format, teamNumber);
            return (true, true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>そのバトル形式の使用中のチームを解除する。解除したら true（もともと無ければ false）。</summary>
    public async Task<bool> ClearActiveTeamAsync(string dataSetKey, BattleFormat format, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            if (roster.ActiveNumber(format) is null) return (false, false);
            roster.SetActiveNumber(format, null);
            return (true, true);
        }, ct).ConfigureAwait(false);
    }

    // ---- 構成の組み立て

    /// <summary>登録内容から構成を作る。データに無いポケモンなら null。</summary>
    public static PokemonBuild? ToBuild(RegisteredPokemon entry, PokemonDataSet data, Item? item = null)
    {
        var pokemon = data.FindPokemon(entry.PokemonId);
        if (pokemon is null) return null;
        return new PokemonBuild(pokemon, data.Info.EvSystem)
        {
            EVs = entry.EVs.Clone(),   // 画面で変えても登録データは変わらない
            Nature = data.FindNature(entry.Nature),
            Ability = entry.AbilityId is { } aid ? data.FindAbility(aid) : data.AbilitiesOf(pokemon).FirstOrDefault(),
            // メガシンカ後のポケモンは、メガストーンを持つ（持ち物は固定）
            Item = pokemon.FixedItemId is { } fixedItemId ? data.FindItem(fixedItemId) ?? item : item,
            TeraType = entry.TeraType,
        };
    }

    /// <summary>そのポケモンがメガシンカした後の姿（メガリザードン X・Y のように複数ある）。メガシンカしないポケモン・メガシンカ後の姿自身なら空。</summary>
    public static IReadOnlyList<Pokemon> MegaFormsOf(PokemonDataSet data, Pokemon pokemon) =>
        pokemon.IsMega ? Array.Empty<Pokemon>() : data.Pokemon.Where(p => p.IsMega && p.SpeciesId == pokemon.SpeciesId).ToList();

    /// <summary>
    /// 構成を、メガシンカ後の姿にした構成にする（ポイント・性格・レベルなどは引き継ぎ、持ち物はメガストーン、特性はメガシンカ後のもの）。
    /// 元の構成は変えない。
    /// </summary>
    public static PokemonBuild ToMegaBuild(PokemonBuild build, Pokemon mega, PokemonDataSet data)
    {
        var result = build.WithPokemon(mega);
        if (mega.MegaStoneId is { } stone) result.Item = data.FindItem(stone) ?? result.Item;
        result.Ability = data.AbilitiesOf(mega).FirstOrDefault() ?? result.Ability;
        return result;
    }

    /// <summary>メガシンカ後の構成を、登録したメガシンカ前の姿に戻す（持ち物・特性は登録・チームのもの。ランク補正などは今のまま）。</summary>
    public static PokemonBuild ToBaseBuild(PokemonBuild current, RegisteredPokemon entry, PokemonDataSet data, Item? item)
    {
        var fresh = ToBuild(entry, data, item);
        if (fresh is null) return current;
        var result = current.WithPokemon(fresh.Pokemon);
        result.Item = fresh.Item;
        result.Ability = fresh.Ability;
        return result;
    }

    /// <summary>
    /// 計算機・素早さ比較の「登録チームから選ぶ」用に、そのバトル形式の登録チームのメンバーを、チームごとにまとめて返す。
    /// 使用中のチームを先頭に、残りは番号順。チームに入っていない育成ポケモンは含めない（選びたいときは育成ポケモンのページから）。
    /// 返す構成は登録データの写しで、呼び出し側で値を変えても登録データは変わらない（<see cref="ToBuild"/>）。
    /// </summary>
    public async Task<IReadOnlyList<RosterChoiceGroup>> ChoiceGroupsAsync(string dataSetKey, PokemonDataSet data, BattleFormat format, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var byId = ById(roster.Pokemon);
        var groups = new List<RosterChoiceGroup>();
        var activeNumber = roster.ActiveNumber(format);
        foreach (var team in roster.Teams.Where(t => t.BattleFormat == format).OrderBy(t => t.Number == activeNumber ? 0 : 1).ThenBy(t => t.Number))
        {
            var choices = new List<RosterChoice>();
            foreach (var slot in team.Slots)
            {
                if (!byId.TryGetValue(slot.EntryId, out var entry)) continue;
                var item = slot.ItemId is { } iid ? data.FindItem(iid) : null;
                choices.Add(new RosterChoice($"{ChoiceKeyPrefix(format)}{team.Number}-{entry.Id}", entry, item));
            }
            if (choices.Count > 0)
                groups.Add(new RosterChoiceGroup($"チーム{team.Number}{(activeNumber == team.Number ? "（使用中）" : "")}", choices));
        }
        return groups;
    }

    /// <summary>「登録チームから選ぶ」の選択キーの先頭（バトル形式ごとに分ける。同じ番号のチームがシングルとダブルにあっても、キーが重ならない）。</summary>
    public static string ChoiceKeyPrefix(BattleFormat format) => format == BattleFormat.Doubles ? "d" : "s";

    /// <summary>そのバトル形式の使用中チームのメンバー（登録ポケモンと持ち物）。</summary>
    public async Task<IReadOnlyList<(RegisteredPokemon Entry, Item? Item)>> ActiveTeamMembersAsync(string dataSetKey, PokemonDataSet data, BattleFormat format, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var team = roster.ActiveTeamOf(format);
        if (team is null) return Array.Empty<(RegisteredPokemon, Item?)>();
        var byId = ById(roster.Pokemon);
        return team.Slots
            .Where(s => byId.ContainsKey(s.EntryId))
            .Select(s => (byId[s.EntryId], s.ItemId is { } iid ? data.FindItem(iid) : null))
            .ToList();
    }

    /// <summary>
    /// 登録 ID で指定した育成ポケモンの構成を返す（使用チームにいれば、チームで持たせた持ち物つき）。登録が無ければ null。
    /// 同じポケモンを別の育成で複数登録しているとき、どの 1 体かを登録 ID で指定するために使う。
    /// </summary>
    public async Task<PokemonBuild?> BuildForEntryAsync(string dataSetKey, PokemonDataSet data, int entryId, BattleFormat format, CancellationToken ct = default)
    {
        var members = await ActiveTeamMembersAsync(dataSetKey, data, format, ct).ConfigureAwait(false);
        var member = members.FirstOrDefault(m => m.Entry.Id == entryId);
        if (member.Entry is not null) return ToBuild(member.Entry, data, member.Item);
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var entry = roster.Pokemon.FirstOrDefault(p => p.Id == entryId);
        return entry is null ? null : ToBuild(entry, data);
    }

    /// <summary>
    /// 使用中チームから、指定ポケモンの構成を返す（<see cref="Text.DamageCommandOptions.BuildProvider"/> 用）。
    /// 同じポケモンが複数いるときは先頭。
    /// </summary>
    public async Task<Func<Pokemon, bool, PokemonBuild?>> ActiveTeamBuildProviderAsync(string dataSetKey, PokemonDataSet data, BattleFormat format, CancellationToken ct = default)
    {
        var members = await ActiveTeamMembersAsync(dataSetKey, data, format, ct).ConfigureAwait(false);
        var builds = new Dictionary<int, PokemonBuild>();
        foreach (var (entry, item) in members)
        {
            if (!builds.ContainsKey(entry.PokemonId) && ToBuild(entry, data, item) is { } build)
                builds[entry.PokemonId] = build;
        }
        return (pokemon, _) => builds.GetValueOrDefault(pokemon.Id);
    }

    /// <summary>
    /// 登録済みのポケモンの構成を返す。使用チームのメンバー（チームで持たせた持ち物つき）を先にし、
    /// 次にほかの登録（持ち物なし。メガシンカ後はメガストーン）。登録が無いポケモンは null。
    /// チャットの dmg・calc・faster で、育成ポケモンを登録済みに限るために使う。
    /// </summary>
    public async Task<Func<Pokemon, bool, PokemonBuild?>> RegisteredBuildProviderAsync(string dataSetKey, PokemonDataSet data, BattleFormat format, CancellationToken ct = default)
    {
        var members = await ActiveTeamMembersAsync(dataSetKey, data, format, ct).ConfigureAwait(false);
        var builds = new Dictionary<int, PokemonBuild>();
        foreach (var (entry, item) in members)
        {
            if (!builds.ContainsKey(entry.PokemonId) && ToBuild(entry, data, item) is { } build)
                builds[entry.PokemonId] = build;
        }
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        foreach (var entry in roster.Pokemon.OrderBy(p => p.Id))
        {
            if (!builds.ContainsKey(entry.PokemonId) && ToBuild(entry, data) is { } build)
                builds[entry.PokemonId] = build;
        }
        return (pokemon, _) => builds.GetValueOrDefault(pokemon.Id);
    }
}

/// <summary><see cref="RosterRepository.MoveTeamAsync"/> の結果。</summary>
public enum TeamMoveStatus
{
    /// <summary>移した。</summary>
    Moved,
    /// <summary>移すチームが無い。</summary>
    NotFound,
    /// <summary>移す先に、同じ番号のチームが既にある（何も変えない）。</summary>
    Occupied,
    /// <summary>形式も番号も変わらない（何も変えない）。</summary>
    Unchanged,
}

/// <param name="Status">移した結果。</param>
/// <param name="Number">移した先（移そうとした先）のチーム番号。</param>
/// <param name="WasActive">移したチームが、移す前の形式で使用中だった（その使用中は解除した）。</param>
public sealed record TeamMoveResult(TeamMoveStatus Status, int Number, bool WasActive);

/// <summary>試合を終えた結果（履歴に移した記録と、移した数）。</summary>
public sealed record EndMatchResult(RivalMatch? Match, int Moved);
