using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 背景のバトルフィールドに立たせる 2 体（自分側＝手前、相手側＝奥）。ダブルバトルのときは、それぞれの側に 1 体ずつ足して 4 体（Mine2・Foe2）。
/// ダメージ計算機（攻撃側・防御側）と素早さ比較（ポケモン1・2）で選んだポケモンを、最後に選んだページの分だけ出す。
/// どちらのページでもまだ選んでいないときは、ランダムな 2 体を出す（ページの入力欄には入れない）。
/// ページで選んだポケモン（PickedMine / PickedFoe）は、もう一方のページを開いたときにその欄にも入れる。
/// </summary>
public sealed class BattleStage
{
    private const string StorageKey = "fara-pokemon-battle-assistant.battle-view";
    private readonly IJSRuntime _js;
    private Task? _init;

    public BattleStage(IJSRuntime js) => _js = js;

    /// <summary>「対戦画面で表示」のスイッチを前回の状態に戻す（ブラウザに保存している）。複数の場所から呼ばれても 1 回だけ読む。</summary>
    public Task InitializeAsync() => _init ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            var saved = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (saved == "1" && !BattleView)
            {
                BattleView = true;
                Changed?.Invoke();
            }
        }
#pragma warning disable CA1031 // 保存の読み込みは補助。どんな失敗でもオフのまま続ける
        catch
        {
            // localStorage が使えない環境（プライベートモード等）ではオフのまま
        }
#pragma warning restore CA1031
    }

    /// <summary>今の 2 体を決めたときのデータセット。データセットが変わったら選び直す。</summary>
    public string DataSetKey { get; private set; } = "";
    public Pokemon? Mine { get; private set; }
    public Pokemon? Foe { get; private set; }

    /// <summary>ダブルバトルか（背景のフィールドに、それぞれの側 2 体ずつ立たせる）。バトル形式の切り替えに合わせて <see cref="EnsureDefaults"/> が更新する。</summary>
    public bool Doubles { get; private set; }
    /// <summary>ダブルバトルのときの、それぞれの側の 2 体目（自分側 2・相手側 2）。シングルバトルでは使わない。</summary>
    public Pokemon? Mine2 { get; private set; }
    public Pokemon? Foe2 { get; private set; }
    /// <summary>ページで最後に選んだ 2 体目（もう一方のページを開いたときに、その欄にも入れる）と、その性格。</summary>
    public Pokemon? PickedMine2 { get; private set; }
    public Pokemon? PickedFoe2 { get; private set; }
    public Nature? PickedMine2Nature { get; private set; }
    public Nature? PickedFoe2Nature { get; private set; }

    /// <summary>ダメージ計算機・素早さ比較で最後に選んだポケモン（自分側＝攻撃側／ポケモン1、相手側＝防御側／ポケモン2）。ランダムな 2 体は入らない。</summary>
    public Pokemon? PickedMine { get; private set; }
    public Pokemon? PickedFoe { get; private set; }
    /// <summary>その 2 体に選んでいる性格（もう一方のページを開いたときに引き継ぐ）。</summary>
    public Nature? PickedMineNature { get; private set; }
    public Nature? PickedFoeNature { get; private set; }

    /// <summary>「登録チームから選ぶ」のオン・オフ（自分側＝攻撃側／ポケモン1、相手側＝防御側／ポケモン2）。もう一方のページを開いたときに引き継ぐ。null はまだどちらのページでも決めていない。</summary>
    public bool? UseRosterMine { get; private set; }
    public bool? UseRosterFoe { get; private set; }

    /// <summary>「登録チームから選ぶ」で選んでいる登録（RosterChoice.Key）と、メガシンカ後にしているときの姿。</summary>
    public string? RosterKeyMine { get; private set; }
    public string? RosterKeyFoe { get; private set; }
    public Pokemon? RosterMegaMine { get; private set; }
    public Pokemon? RosterMegaFoe { get; private set; }

    public void ShareRosterMode(string dataSetKey, bool mine, bool foe, string? keyMine = null, Pokemon? megaMine = null, string? keyFoe = null, Pokemon? megaFoe = null)
    {
        // 登録の Key（r1 など）はゲームごとに同じ値がある。ゲームを切り替えた直後の描画で、前のゲームの Key を入れない
        if (dataSetKey != DataSetKey) return;
        UseRosterMine = mine;
        UseRosterFoe = foe;
        RosterKeyMine = mine ? keyMine : null;
        RosterMegaMine = mine ? megaMine : null;
        RosterKeyFoe = foe ? keyFoe : null;
        RosterMegaFoe = foe ? megaFoe : null;
    }

    public event Action? Changed;

    /// <summary>「対戦画面で表示」のスイッチ。オンで結果（Scene）があるとき、背景のフィールドを止めて結果を重ねて出す。</summary>
    public bool BattleView { get; private set; }

    /// <summary>今開いているページが出している結果。結果がないページ・まだ結果が出ていないときは null。</summary>
    public BattleScene? Scene { get; private set; }

    private object? sceneOwner;
    private object? conditionsOwner;

    /// <summary>いま開いているページで入力している天候・フィールド（ダメージ計算機・素早さ比較・育成ポケモン）。背景のフィールドに見た目で出る。</summary>
    public Weather Weather { get; private set; }
    public Terrain Terrain { get; private set; }

    /// <summary>
    /// 「計算」の見出しの中（ダメージ計算機・素早さ比較）で、行き来しても引き継ぐ天候・フィールド。
    /// 各ページは、開いたときにこの値を入れ、変わったら書き戻す。
    /// </summary>
    public Weather SharedWeather { get; private set; }
    public Terrain SharedTerrain { get; private set; }

    public void ShareField(Weather weather, Terrain terrain)
    {
        SharedWeather = weather;
        SharedTerrain = terrain;
    }

    /// <summary>ページが、入力している天候・フィールドを出す。同じ内容なら何もしない（描画のたびに呼んでよい）。</summary>
    public void SetConditions(object owner, Weather weather, Terrain terrain)
    {
        if (ReferenceEquals(conditionsOwner, owner) && Weather == weather && Terrain == terrain) return;
        conditionsOwner = owner;
        Weather = weather;
        Terrain = terrain;
        Changed?.Invoke();
    }

    /// <summary>対戦画面を出している状態か（スイッチがオンなら、どのページでも出す。結果が無いページ・まだ結果が無いときは案内だけを出す）。</summary>
    public bool BattleActive => BattleView;

    public void SetBattleView(bool on)
    {
        if (BattleView == on) return;
        BattleView = on;
        Changed?.Invoke();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, BattleView ? "1" : "0"); }
#pragma warning disable CA1031 // 保存できなくても、その場の表示には困らない
        catch { /* 保存できなくても、その場の表示には困らない */ }
#pragma warning restore CA1031
    }

    /// <summary>
    /// 背景に立たせる 2 体を、ページで「選んだ」ことにせずに出す（育成ポケモン用。ほかのページの入力欄には入れない）。
    /// 選んでいない側（null）は今のままにする。
    /// </summary>
    public void ShowOnly(string dataSetKey, Pokemon? mine, Pokemon? foe)
    {
        if (dataSetKey != DataSetKey) return;
        var next = (Mine: mine ?? Mine, Foe: foe ?? Foe);
        if (next.Mine?.Id == Mine?.Id && next.Foe?.Id == Foe?.Id) return;
        Mine = next.Mine;
        Foe = next.Foe;
        Changed?.Invoke();
    }

    /// <summary>ページが結果を出す。同じ内容なら何もしない（描画のたびに呼んでよい）。</summary>
    public void SetScene(object owner, BattleScene? scene)
    {
        if (scene is null) { ClearScene(owner); return; }
        if (ReferenceEquals(sceneOwner, owner) && Equals(Scene, scene)) return;
        sceneOwner = owner;
        Scene = scene;
        Changed?.Invoke();
    }

    /// <summary>ページが閉じたとき・結果がなくなったときに消す。別のページが出した結果は消さない。</summary>
    public void ClearScene(object owner)
    {
        var changed = false;
        if (ReferenceEquals(sceneOwner, owner))
        {
            sceneOwner = null;
            Scene = null;
            changed = true;
        }
        if (ReferenceEquals(conditionsOwner, owner))
        {
            conditionsOwner = null;
            Weather = Weather.None;
            Terrain = Terrain.None;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// そのデータセットで、まだ立たせるポケモンが決まっていなければランダムに決める（シングルは 2 体、ダブルは 4 体）。
    /// <paramref name="doubles"/> はバトル形式（ダブルか）。形式が変わったときも呼ぶと、足りない分だけ足す。
    /// </summary>
    public void EnsureDefaults(string dataSetKey, PokemonDataSet data, bool doubles = false) =>
        EnsureDefaults(dataSetKey, data.Pokemon, doubles);

    /// <summary>候補のポケモンの一覧から決める（<see cref="EnsureDefaults(string, PokemonDataSet, bool)"/> の本体）。</summary>
    public void EnsureDefaults(string dataSetKey, IReadOnlyList<Pokemon> pokemon, bool doubles = false)
    {
        var changed = false;
        if (Doubles != doubles)
        {
            Doubles = doubles;
            changed = true;
        }
        var candidates = pokemon.Where(p => !p.IsProvisional && !string.IsNullOrEmpty(p.Icon)).ToList();
        if (dataSetKey != DataSetKey || Mine is null || Foe is null)
        {
            if (candidates.Count < 2)
            {
                if (changed) Changed?.Invoke();
                return;
            }
            DataSetKey = dataSetKey;
            PickedMine = PickedFoe = PickedMine2 = PickedFoe2 = null;
            PickedMineNature = PickedFoeNature = PickedMine2Nature = PickedFoe2Nature = null;
            UseRosterMine = UseRosterFoe = null;
            RosterKeyMine = RosterKeyFoe = null;
            RosterMegaMine = RosterMegaFoe = null;
            var picks = PickDistinct(candidates, doubles ? 4 : 2);   // シングルは 2 体。ダブルに切り替えたときに、足りない 2 体を足す
            Mine = picks[0];
            Foe = picks[1];
            Mine2 = picks.Count > 2 ? picks[2] : null;
            Foe2 = picks.Count > 3 ? picks[3] : null;
            changed = true;
        }
        if (Doubles && (Mine2 is null || Foe2 is null) && candidates.Count >= 3)
        {
            // ダブルに切り替えたとき、足りない側を、今立っているポケモンと重ならないように足す
            var taken = new HashSet<int> { Mine!.Id, Foe!.Id };
            if (Mine2 is not null) taken.Add(Mine2.Id);
            if (Foe2 is not null) taken.Add(Foe2.Id);
            var extra = PickDistinct(candidates.Where(p => !taken.Contains(p.Id)).ToList(), 2);
            var next = 0;   // 足す候補を、先頭から順に 1 つずつ使う（空いている側の数だけ）
            if (Mine2 is null && next < extra.Count) { Mine2 = extra[next++]; changed = true; }
            if (Foe2 is null && next < extra.Count) { Foe2 = extra[next++]; changed = true; }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>候補から、重ならないように最大 <paramref name="count"/> 体をランダムに選ぶ。</summary>
    private static List<Pokemon> PickDistinct(List<Pokemon> candidates, int count)
    {
        var pool = candidates.ToList();
        var result = new List<Pokemon>();
        while (result.Count < count && pool.Count > 0)
        {
            var i = Random.Shared.Next(pool.Count);
            result.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return result;
    }

    /// <summary>
    /// ページで選んでいるポケモンを出す。選んでいない側（null）は今のままにする。
    /// <paramref name="mine2"/>・<paramref name="foe2"/> はダブルバトルの 2 体目（シングルバトルでは null）。
    /// </summary>
    public void Show(string dataSetKey, Pokemon? mine, Pokemon? foe, Nature? mineNature = null, Nature? foeNature = null,
        Pokemon? mine2 = null, Pokemon? foe2 = null, Nature? mine2Nature = null, Nature? foe2Nature = null)
    {
        if (dataSetKey != DataSetKey) return;
        if (mine is not null) { PickedMine = mine; PickedMineNature = mineNature; }
        if (foe is not null) { PickedFoe = foe; PickedFoeNature = foeNature; }
        if (mine2 is not null) { PickedMine2 = mine2; PickedMine2Nature = mine2Nature; }
        if (foe2 is not null) { PickedFoe2 = foe2; PickedFoe2Nature = foe2Nature; }
        var next = (Mine: mine ?? Mine, Foe: foe ?? Foe, Mine2: mine2 ?? Mine2, Foe2: foe2 ?? Foe2);
        if (next.Mine?.Id == Mine?.Id && next.Foe?.Id == Foe?.Id && next.Mine2?.Id == Mine2?.Id && next.Foe2?.Id == Foe2?.Id) return;
        Mine = next.Mine;
        Foe = next.Foe;
        Mine2 = next.Mine2;
        Foe2 = next.Foe2;
        Changed?.Invoke();
    }
}
