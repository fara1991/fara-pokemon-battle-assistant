using System.Text.Json;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 登録データの保存先（アプリ全体の <see cref="IRosterStore"/>）。アクセスキーがあればクラウド（<see cref="RemoteRosterStore"/>）、
/// 無ければこのブラウザの localStorage（<see cref="BrowserRosterStore"/>）を使う。
/// </summary>
/// <remarks>
/// データ共有中はクラウドだけが正で、このブラウザには登録データを残さない（localStorage に置くのはアクセスキーだけ）。
/// アクセスキーはパスワードと同じ扱いで、ログ・エラー表示には出さない。
/// </remarks>
public sealed class CloudSync : IRosterStore
{
    /// <summary>アクセスキーを置く localStorage のキー。</summary>
    public const string KeyStorageKey = "fara-pokemon-battle-assistant.syncKey";

    private readonly IJSRuntime _js;
    private readonly HttpClient _http;
    private readonly string _baseUri;
    private readonly BrowserRosterStore _local;
    private readonly IServiceProvider _services;
    private RemoteRosterStore? _remote;
    private string? _key;
    private bool _initialized;
    private bool _conflicted;

    public CloudSync(IJSRuntime js, HttpClient http, string baseUri, IServiceProvider services)
    {
        _js = js;
        _http = http;
        _baseUri = baseUri;
        _local = new BrowserRosterStore(js);
        _services = services;
    }

    /// <summary>データ共有中か。</summary>
    public bool IsSyncing => _remote is not null;

    /// <summary>アクセスキー（データ共有していなければ null）。画面に出すときは伏せ字にする。</summary>
    public string? Key => _key;

    /// <summary>最後にクラウドとの読み書きが成功した時刻。</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>最後の読み書きが失敗したときのメッセージ（成功したら消える）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最後の保存で他の場所の更新と競合し、読み直して保存し直したときのお知らせ。</summary>
    public string? LastNotice { get; private set; }

    /// <summary>データ共有の状態（開始・停止・成功・失敗）が変わったとき。</summary>
    public event Action? Changed;

    public bool AlwaysReload => _remote is not null;

    /// <summary>データ共有していないときは、ブラウザに保存する履歴の上限。クラウドでは、サーバー側の上限。</summary>
    public int? LocalHistoryLimit => _remote is null ? _local.LocalHistoryLimit : null;

    /// <summary>データ共有中なら、クラウドの試合の履歴の窓口。共有していなければ null（履歴はブラウザの登録データの中）。</summary>
    public async ValueTask<IMatchHistory?> GetRemoteHistoryAsync(CancellationToken ct = default) => await CurrentRemoteAsync();

    // ---- IRosterStore

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        var remote = await CurrentRemoteAsync();
        if (remote is null) return await _local.LoadAsync(ct);
        try
        {
            var json = await remote.LoadAsync(ct);
            RecordSuccess();
            return json;
        }
        catch (RosterUnavailableException ex)
        {
            RecordError(ex.Message);
            throw;
        }
    }

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        var remote = await CurrentRemoteAsync();
        if (remote is null)
        {
            await _local.SaveAsync(json, ct);
            return;
        }
        await SaveRemoteAsync(() => remote.SaveAsync(json, ct));
    }

    /// <summary>1 つのゲームのポケモンかチームだけを保存する（クラウドでは /api/my/pokemon か /api/my/team だけを呼ぶ）。</summary>
    public async Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default)
    {
        var remote = await CurrentRemoteAsync();
        if (remote is null)
        {
            await _local.SavePartAsync(state, dataSetKey, part, ct);
            return;
        }
        await SaveRemoteAsync(() => remote.SavePartAsync(state, dataSetKey, part, ct));
    }

    private async Task SaveRemoteAsync(Func<Task> save)
    {
        try
        {
            await save();
            // 競合して読み直したあとの保存なら、そのことを知らせる
            LastNotice = _conflicted ? "他の場所（配信用BOTや別の端末）で更新されていたので、最新の内容を読み込み直してから保存しました" : null;
            _conflicted = false;
            RecordSuccess();
        }
        catch (RosterConflictException)
        {
            // RosterRepository が読み直してやり直す
            _conflicted = true;
            throw;
        }
        catch (RosterUnavailableException ex)
        {
            _conflicted = false;
            RecordError(ex.Message);
            throw;
        }
    }

    // ---- データ共有の開始・参加・作り直し・停止

    /// <summary>
    /// 保存してあるアクセスキーを読み、データ共有中かどうかを確かめる（データ共有ページを直接開いたときに、まだ何も読み込んでいなくても
    /// 「データ共有中」と表示するため）。
    /// </summary>
    public Task InitializeAsync() => EnsureInitializedAsync();

    /// <summary>このブラウザに（データ共有していないときの）登録データがあるか。</summary>
    public async Task<bool> LocalHasDataAsync()
    {
        await EnsureInitializedAsync();
        try
        {
            return HasData(RosterRepository.Deserialize(await ReadLocalAsync()));
        }
        catch (JsonException)
        {
            return true;   // 読めない（壊れている）ときは、あるものとして扱い、消す前の確認を必ず出す
        }
    }

    /// <summary>新しいアクセスキーを作ってデータ共有を始める。このブラウザの登録データをクラウドに上げ、ブラウザからは消す。</summary>
    public async Task StartNewAsync()
    {
        await EnsureInitializedAsync();
        if (IsSyncing) throw new InvalidOperationException("すでにデータ共有しています");
        var key = RemoteRosterStore.GenerateKey();
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        RosterState local;
        try
        {
            local = RosterRepository.Deserialize(await ReadLocalAsync());
        }
        catch (JsonException ex)
        {
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "このブラウザの登録データが壊れているため、新しく共有を始められません。すでにアクセスキーがあるなら、アクセスキーで参加してください（このブラウザの登録データは削除されます）", ex);
        }
        var trimmed = 0;
        if (HasData(local))
            trimmed = (await MoveToNewKeyAsync(remote, RosterRepository.Serialize(local))).Trimmed;
        else
            await remote.LoadAsync(); // つながるか確かめる
        await SwitchToAsync(key, remote);
        if (trimmed > 0) LastNotice = TrimmedNotice(trimmed);
        await _local.ClearAsync();
    }

    /// <summary>
    /// 既存のアクセスキーで参加する。このブラウザの登録データは消え、クラウドの内容に置き換わる。
    /// クラウドが空で、このブラウザに登録データがあるときは、キーの打ち間違いかもしれないので、
    /// <paramref name="allowEmptyCloud"/> が true でない限り何も変えずに <see cref="JoinResult.CloudEmpty"/> を返す。
    /// </summary>
    public async Task<JoinResult> JoinAsync(string key, bool allowEmptyCloud = false)
    {
        await EnsureInitializedAsync();
        if (IsSyncing) throw new InvalidOperationException("すでにデータ共有しています");
        key = key.Trim();
        if (!RemoteRosterStore.IsValidKey(key))
            throw new ArgumentException("アクセスキーの形式が正しくありません（43 文字の英数字・-・_）");
        await ReadLocalAsync();   // このブラウザの登録データを読めないまま、確認なしに消さない
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var json = await remote.LoadAsync();
        if (json is null && !allowEmptyCloud && await LocalHasDataAsync())
            return JoinResult.CloudEmpty;
        await SwitchToAsync(key, remote);
        await _local.ClearAsync();
        return json is not null ? JoinResult.Joined : JoinResult.JoinedEmpty;
    }

    /// <summary>
    /// アクセスキーを作り直す。今のデータを新しいキーの置き場に移し、古いキーの置き場は消す（古いキーは使えなくなる）。
    /// 古い置き場を消せなかったときは false（新しいキーへの切り替えは済んでいる）。
    /// </summary>
    public async Task<bool> RotateAsync()
    {
        await EnsureInitializedAsync();
        var old = _remote ?? throw new InvalidOperationException("データ共有していません");
        var key = RemoteRosterStore.GenerateKey();
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var json = await old.LoadAsync();
        // 相手チームを読めていないと、新しいキーに相手チームと履歴が移らないまま、古い置き場を消してしまう
        if (old.RivalUnreadable)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "クラウドの相手チームを読み込めなかったため、キーを作り直せません。時間をおいて、もう一度試してください");
        // 登録データ（文書）が空でも、履歴だけがあるゲームがありうるので、履歴の写しは文書の有無によらず行う
        var (trimmed, copied) = await MoveToNewKeyAsync(remote, json, old);
        await SwitchToAsync(key, remote);
        // 古いキーは、履歴が写した時点の状態のままのときだけ消す。写している間に、別の端末・BOT が履歴を足していたら、消さずに、足された分だけ
        // 新しいキーへ写して、もう一度試す（確認と削除はサーバーで 1 つの処理なので、その間に取りこぼしは起きない）
        var expected = copied;
        for (var attempt = 0; attempt < MaxRotateRetries; attempt++)
        {
            try
            {
                await old.DeleteIfHistoryUnchangedAsync(expected);
                if (trimmed > 0) LastNotice = TrimmedNotice(trimmed);
                return true;
            }
            catch (HistoryChangedException changed)
            {
                try
                {
                    trimmed += await CopyNewMatchesAsync(old, remote, expected, changed.Current);
                }
                catch (Exception ex) when (ex is RosterUnavailableException or HistoryLimitException)
                {
                    break;
                }
                expected = changed.Current;
            }
            catch (RosterUnavailableException)
            {
                break;
            }
        }
        if (trimmed > 0) LastNotice = TrimmedNotice(trimmed);
        return false;
    }

    /// <summary>キーの作り直しで、古いキーを消すのを試す回数（そのたびに、足された分を新しいキーへ写す）。</summary>
    private const int MaxRotateRetries = 5;

    /// <summary>
    /// データ共有をやめる。このアクセスキーのクラウドのデータ（ポケモン・チーム・相手チーム・試合の履歴）をすべて削除し、キーは使えなくなる。
    /// 他の端末・配信用BOTでも使えなくなる。削除できなかったとき（回線断など）は、キーを忘れずに例外にする（もう一度試せるように）。
    /// このブラウザの登録データは空になる。
    /// </summary>
    public async Task StopAsync()
    {
        await EnsureInitializedAsync();
        if (_remote is not null)
        {
            try
            {
                await _remote.DeleteAsync();
            }
            catch (RosterUnavailableException ex) when (ex.Reason == RosterUnavailableReason.KeyRetired)
            {
                // すでに作り直し・解除で消えているキーは、そのままやめられる
            }
        }
        await _js.InvokeVoidAsync("localStorage.removeItem", KeyStorageKey);
        _remote = null;
        _key = null;
        LastSyncedAt = null;
        LastError = null;
        LastNotice = null;
        AfterSwitch();
    }

    /// <summary>登録データの読み書きで出た例外を、画面に出す文に直す。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        RosterConflictException => "他の場所（配信用BOTや別の端末）で同時に更新されたため保存できませんでした。最新の内容を読み込み直したので、もう一度操作してください",
        RosterUnavailableException u => u.Message,
        HttpRequestException => "クラウドに接続できませんでした",
        _ => ex.Message,
    };

    // ---- 内部

    /// <summary>
    /// 新しいキーの空の置き場（版 0）にデータを移す（ポケモン → チームの順に 2 回 PUT）。
    /// 新しいキーはまだ誰も使っていないので、移し終わってから切り替えれば途中の状態は見えない。
    /// 途中で失敗したら新しいキーは使わずに捨てる（置き場も消しておく）。
    /// </summary>
    /// <summary>このブラウザの登録データ（JSON）を読む。localStorage を読めなかったときは、データ共有の開始・参加でブラウザのデータを失わないよう、例外にする。</summary>
    private async Task<string?> ReadLocalAsync()
    {
        var json = await _local.LoadAsync();
        if (_local.LoadFailed)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "ブラウザの保存領域を読み込めないため、データ共有を始められません。ページを再読み込みしてください（直らないときは、ブラウザがサイトデータの保存を許可しているか確認してください）");
        return json;
    }

    /// <param name="json">移す登録データ。null（登録データが空）ならつながるかだけ確かめる。</param>
    /// <param name="copyHistoryFrom">キーの作り直しのとき、古いキーの置き場（履歴を新しいキーへ写す。<paramref name="json"/> には履歴が入っていないため）。データ共有の開始では null（履歴は <paramref name="json"/> の中）。</param>
    /// <returns>履歴のうち、サーバーの上限（1 試合 64 KB・メモ 500 文字）に収めるため、内容を省いた試合の数と、写した時点の古いキーの履歴の状態。</returns>
    private static async Task<(int Trimmed, IReadOnlyDictionary<string, HistoryTotal> Totals)> MoveToNewKeyAsync(RemoteRosterStore remote, string? json, RemoteRosterStore? copyHistoryFrom = null)
    {
        try
        {
            var trimmed = 0;
            IReadOnlyDictionary<string, HistoryTotal> totals = new Dictionary<string, HistoryTotal>();
            if (json is not null)
            {
                var source = RosterRepository.Deserialize(json);
                await remote.SaveAsync(json);
                trimmed += remote.HistoryTrimmed;
                // 送った内容が、クラウドに同じ件数で入っているかを、読み直して確かめる（違えば、ブラウザ・古いキーを消す前に中止する）
                VerifyCopied(source, RosterRepository.Deserialize(await remote.LoadAsync()));
            }
            else await remote.LoadAsync(); // つながるか確かめる
            if (copyHistoryFrom is not null)
            {
                var copy = await CopyHistoryAsync(copyHistoryFrom, remote);
                trimmed += copy.Trimmed;
                totals = copy.Totals;
            }
            return (trimmed, totals);
        }
        catch
        {
            try { await remote.DeleteAsync(); } catch (Exception) { /* 消せなくても新しいキーは使わない */ }
            throw;
        }
    }

    /// <summary>
    /// 送った登録データ（<paramref name="source"/>・送る前の内容）と、クラウドから読み直した内容（<paramref name="copied"/>）の件数を、ゲームごとに比べる。
    /// サーバーの規則（登録に無いポケモンを指す枠・同じポケモンの重複・同じチーム番号の重複・枠の上限）で削られたものがあれば、
    /// 例外にする。呼び出し側は新しいキーを捨て、ブラウザの登録データも古いキーも消さない。0 体のチームは対象外（ポケモンを失わない）。
    /// </summary>
    private static void VerifyCopied(RosterState source, RosterState? copied)
    {
        var problems = new List<string>();
        foreach (var (key, roster) in source.DataSets)
        {
            var sent = CountRoster(roster);
            var got = (Pokemon: 0, Teams: 0, Slots: 0);
            if (copied is not null && copied.DataSets.TryGetValue(key, out var c)) got = CountRoster(c);
            if (sent != got)
                problems.Add($"{key}: ポケモン {sent.Pokemon}→{got.Pokemon}、チーム {sent.Teams}→{got.Teams}、枠 {sent.Slots}→{got.Slots}");
        }
        if (problems.Count > 0)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "クラウドに写せない内容（登録に無いポケモンを指す枠、同じポケモンの重複、同じ番号のチーム、枠が 7 体以上のチーム など）があったため中止しました。" +
                "このブラウザのデータと今のアクセスキーは変わっていません。（" + string.Join("・", problems) + "）");
    }

    private static (int Pokemon, int Teams, int Slots) CountRoster(DataSetRoster roster)
    {
        var teams = roster.Teams.Where(t => t.Slots.Count > 0).ToList();
        return (roster.Pokemon.Count, teams.Count, teams.Sum(t => t.Slots.Count));
    }

    /// <summary>
    /// 古いキーの履歴を、全部のゲーム分、新しいキーへ写す。新しいキーに写した件数がそろっていることを確かめる。
    /// 写している間の変化は、古いキーを消すとき（<see cref="RotateAsync"/>）に、サーバーが写した時点の状態と見比べて検出する。
    /// </summary>
    private static async Task<(int Trimmed, IReadOnlyDictionary<string, HistoryTotal> Totals)> CopyHistoryAsync(RemoteRosterStore source, RemoteRosterStore target)
    {
        var before = await source.DataSetTotalsAsync();
        var trimmed = 0;
        foreach (var (dataSetKey, state) in before)
        {
            var matches = await ReadAllHistoryOldestFirstAsync(source, dataSetKey, state.Total, state.NextId);
            trimmed += await target.AppendManyAsync(dataSetKey, matches);
        }
        var copied = await target.DataSetTotalsAsync();
        if (copied.Count != before.Count || before.Any(x => !copied.TryGetValue(x.Key, out var other) || other.Total != x.Value.Total))
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "履歴を新しいアクセスキーへ写せなかったため、中止しました。もう一度試してください");
        return (trimmed, before);
    }

    /// <summary>
    /// 写し終えたあとに、古いキーへ足された試合（写した時点の次の Id 以降）だけを、新しいキーへ足す。足した試合のうち、内容を省いた数を返す。
    /// </summary>
    private static async Task<int> CopyNewMatchesAsync(RemoteRosterStore source, RemoteRosterStore target,
        IReadOnlyDictionary<string, HistoryTotal> previous, IReadOnlyDictionary<string, HistoryTotal> current)
    {
        var trimmed = 0;
        foreach (var (dataSetKey, state) in current)
        {
            var from = previous.TryGetValue(dataSetKey, out var was) ? was.NextId : 1;
            if (state.NextId <= from) continue;   // 足されていない（消されただけ）
            var added = new List<RivalMatch>();
            int? before = state.NextId;   // 今回の状態の次の Id より前だけ（それ以降は、次の回で写す。同じ試合を 2 度写さない）
            var finished = false;
            for (var pages = 0; pages <= (state.NextId - from) / 200 + 2 && !finished; pages++)
            {
                var page = await source.ListAsync(dataSetKey, 200, before);
                added.AddRange(page.Items.TakeWhile(m => m.Id >= from));
                finished = !page.HasMore || page.Items.Count == 0 || page.Items[^1].Id < from;
                if (!finished) before = page.Items[^1].Id;
            }
            if (!finished)   // 読み切れていないまま写すと、残りを失うので、中止する
                throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null, "履歴を最後まで読めなかったため、キーを作り直せません。もう一度試してください");
            added.Reverse();
            if (added.Count > 0) trimmed += await target.AppendManyAsync(dataSetKey, added);
        }
        return trimmed;
    }

    private static string TrimmedNotice(int count) =>
        $"試合の履歴のうち {count} 件は、サーバーの上限（1 試合 64 KB・メモ 500 文字）に収めるため、推定の根拠（スキャンの記録）を省くか、メモを切って移しました";

    /// <summary>1 つのゲームの履歴を（<paramref name="nextId"/> より前の分だけ）全部、古い順に読む（1 回に 200 件ずつ）。サーバーの応答がおかしくて終わらない・途中で切れるときは失敗にする。</summary>
    private static async Task<List<RivalMatch>> ReadAllHistoryOldestFirstAsync(IMatchHistory source, string dataSetKey, int expectedTotal, int nextId)
    {
        var all = new List<RivalMatch>();
        int? before = nextId;   // 一覧を取った時点の次の Id より前だけ読む（読んでいる間に足された試合は、あとで足された分として写す）
        for (var pages = 0; pages <= expectedTotal / 200 + 2; pages++)
        {
            var page = await source.ListAsync(dataSetKey, 200, before);
            // 次のページの頼み方（before）は、必ず前より小さくなる（ListAsync が確かめている）。空なのに続きがあるのはおかしい
            if (page.Items.Count == 0 && page.HasMore) break;
            all.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0)
            {
                all.Reverse();
                return all.Count == expectedTotal
                    ? all
                    : throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null, "履歴を最後まで読めなかったため、キーを作り直せません。もう一度試してください");
            }
            before = page.Items[^1].Id;
        }
        throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null, "履歴を最後まで読めなかったため、キーを作り直せません。もう一度試してください");
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RefreshKeyFromStorageAsync();
    }

    /// <summary>
    /// 今の保存先。データ共有していないときは毎回 localStorage のキーを確かめ、別のタブでデータ共有を始めていたらクラウドに切り替える。
    /// </summary>
    private async Task<RemoteRosterStore?> CurrentRemoteAsync()
    {
        if (!_initialized || _remote is null || _keyReadFailed)
        {
            _initialized = true;
            await RefreshKeyFromStorageAsync();
        }
        // 共有の設定を読めなかったときは、ブラウザへ黙って保存先を切り替えない（共有中のデータがブラウザに分かれて書かれるのを防ぐ）
        if (_keyReadFailed)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "データ共有の設定をブラウザから読めないため、保存・読み込みを止めています。ページを再読み込みしてください");
        return _remote;
    }

    /// <summary>localStorage からデータ共有のキーを読めなかったことがあるか（読み直して成功するまで true）。</summary>
    private bool _keyReadFailed;

    private async Task RefreshKeyFromStorageAsync()
    {
        string? key;
        try
        {
            key = await _js.InvokeAsync<string?>("localStorage.getItem", KeyStorageKey);
            _keyReadFailed = false;
        }
        catch (JSException ex)
        {
            // 失敗を利用者に見せ、読み直すまで保存先を切り替えない（CurrentRemoteAsync が止める）
            _keyReadFailed = true;
            RecordError($"データ共有の設定を読めませんでした（{ex.Message}）。ページを再読み込みしてください");
            return;
        }
        if (key is not null && RemoteRosterStore.IsValidKey(key) && key != _key)
        {
            // http の URL（LAN の IP で開いているときなど）では、アクセスキーを平文で送らないよう、RemoteRosterStore が受け付けない
            try
            {
                var remote = new RemoteRosterStore(_http, _baseUri, key);
                _key = key;
                _remote = remote;
            }
            catch (ArgumentException)
            {
                return;
            }
            Changed?.Invoke();
        }
    }

    private async Task SwitchToAsync(string key, RemoteRosterStore remote)
    {
        // キーを保存できなかったら（localStorage が使えないなど）切り替えない
        await _js.InvokeVoidAsync("localStorage.setItem", KeyStorageKey, key);
        _key = key;
        _remote = remote;
        LastSyncedAt = DateTimeOffset.Now;
        LastError = null;
        LastNotice = null;
        AfterSwitch();
    }

    private void AfterSwitch()
    {
        _services.GetRequiredService<RosterRepository>().Invalidate();
        Changed?.Invoke();
    }

    private void RecordSuccess()
    {
        LastSyncedAt = DateTimeOffset.Now;
        LastError = null;
        Changed?.Invoke();
    }

    private void RecordError(string message)
    {
        LastError = message;
        Changed?.Invoke();
    }

    private static bool HasData(RosterState state) =>
        state.DataSets.Values.Any(r => r.Pokemon.Count > 0 || r.Teams.Count > 0 || r.Rivals.Count > 0 || r.History.Count > 0);
}

/// <summary>使用中チームを読む（計算機・素早さ比較用）。クラウドに届かなくてもページは動くように、エラーは文で返す。</summary>
public static class RosterReading
{
    /// <summary>「登録チームから選ぶ」用に、登録済みの構成を、そのバトル形式のチームごとに読む。</summary>
    public static async Task<(IReadOnlyList<RosterChoiceGroup> Groups, string Error)> ChoiceGroupsAsync(RosterRepository repo, string dataSetKey, PokemonDataSet data, BattleFormat format)
    {
        try
        {
            return (await repo.ChoiceGroupsAsync(dataSetKey, data, format), "");
        }
        catch (Exception ex) when (ex is RosterUnavailableException or HttpRequestException)
        {
            return (Array.Empty<RosterChoiceGroup>(), $"登録を読み込めませんでした（{CloudSync.Describe(ex)}）。登録なしで計算します");
        }
    }

    public static async Task<(IReadOnlyList<(RegisteredPokemon Entry, Item? Item)> Members, string Error)> ActiveTeamMembersAsync(
        RosterRepository repo, string dataSetKey, PokemonDataSet data, BattleFormat format)
    {
        try
        {
            return (await repo.ActiveTeamMembersAsync(dataSetKey, data, format), "");
        }
        catch (Exception ex) when (ex is RosterUnavailableException or HttpRequestException)
        {
            return (Array.Empty<(RegisteredPokemon, Item?)>(), $"使用チームを読み込めませんでした（{CloudSync.Describe(ex)}）。チームなしで計算します");
        }
    }
}

/// <summary>アクセスキーで参加した結果。</summary>
public enum JoinResult
{
    /// <summary>クラウドのデータで共有を始めた。</summary>
    Joined,
    /// <summary>クラウドが空のまま共有を始めた（打ち間違いかもしれない）。</summary>
    JoinedEmpty,
    /// <summary>クラウドが空で、このブラウザには登録データがあるため、何も変えなかった（打ち間違いかもしれない）。</summary>
    CloudEmpty,
}
