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

    /// <summary>
    /// データ共有中なら、クラウドの試合の履歴の窓口。共有していなければ null（履歴はブラウザの登録データの中）。
    /// 足す・消す（書く）ときは、保存先のキーを読み直してから行う。
    /// </summary>
    public async ValueTask<IMatchHistory?> GetRemoteHistoryAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        return _remote is null ? null : new RemoteHistory(this);
    }

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
        catch (RosterUnavailableException ex) when (ex.Reason == RosterUnavailableReason.KeyRetired)
        {
            // 別のタブ・端末でキーが作り直されていたら、保存されているキーを読み直して、新しいキーでもう一度だけ読む
            if (await RereadKeyChangedAsync(remote) is { } next) return await ReadWithAsync(next, ct);
            RecordError(ex.Message);
            throw;
        }
        catch (RosterUnavailableException ex)
        {
            RecordError(ex.Message);
            throw;
        }
    }

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        var remote = await CurrentRemoteAsync(forWrite: true);
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
        var remote = await CurrentRemoteAsync(forWrite: true);
        if (remote is null)
        {
            await _local.SavePartAsync(state, dataSetKey, part, ct);
            return;
        }
        await SaveRemoteAsync(() => remote.SavePartAsync(state, dataSetKey, part, ct));
    }

    private async Task<string?> ReadWithAsync(RemoteRosterStore remote, CancellationToken ct)
    {
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
        var localJson = await ReadLocalAsync();
        RosterState local;
        try
        {
            local = RosterRepository.Deserialize(localJson);
        }
        catch (JsonException ex)
        {
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "このブラウザの登録データが壊れているため、新しく共有を始められません。すでにアクセスキーがあるなら、アクセスキーで参加してください（このブラウザの登録データは削除されます）", ex);
        }
        var trimmed = 0;
        if (HasData(local))
            trimmed = (await RosterRotation.CopyToNewKeyAsync(remote, RosterRepository.Serialize(local))).Trimmed;
        else
            await remote.LoadAsync(); // つながるか確かめる
        await SwitchToNewKeyAsync(key);
        if (trimmed > 0) LastNotice = TrimmedNotice(trimmed);
        await ClearLocalIfUnchangedAsync(localJson);
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
        var localJson = await ReadLocalAsync();   // このブラウザの登録データを読めないまま、確認なしに消さない
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var json = await remote.LoadAsync();
        if (json is null && !allowEmptyCloud && await LocalHasDataAsync())
            return JoinResult.CloudEmpty;
        await SwitchToAsync(key, remote);
        await ClearLocalIfUnchangedAsync(localJson);
        return json is not null ? JoinResult.Joined : JoinResult.JoinedEmpty;
    }

    /// <summary>
    /// アクセスキーを作り直す。今のデータを新しいキーの置き場に移し、古いキーの置き場は消す（古いキーは使えなくなる）。
    /// 古い置き場を消せなかったときは false（新しいキーへの切り替えは済んでいる）。
    /// </summary>
    public async Task<bool> RotateAsync()
    {
        await EnsureInitializedAsync();
        if (_remote is null || _key is null) throw new InvalidOperationException("データ共有していません");
        // 写す（新しいキーの置き場へ）。古いキーは消さない
        var plan = await RosterRotation.PrepareAsync(_http, _baseUri, _key);
        await SwitchToNewKeyAsync(plan.NewKey);
        // 古いキーを消す（写している間の変更は、足された分を写し直してから）
        var finished = await RosterRotation.FinishAsync(_http, _baseUri, plan);
        if (finished.TrimmedMatches > 0) LastNotice = TrimmedNotice(finished.TrimmedMatches);
        return finished.OldKeyDeleted;
    }

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

    /// <summary>このブラウザの登録データ（JSON）を読む。localStorage を読めなかったときは、データ共有の開始・参加でブラウザのデータを失わないよう、例外にする。</summary>
    private async Task<string?> ReadLocalAsync()
    {
        var json = await _local.LoadAsync();
        if (_local.LoadFailed)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "ブラウザの保存領域を読み込めないため、データ共有を始められません。ページを再読み込みしてください（直らないときは、ブラウザがサイトデータの保存を許可しているか確認してください）");
        return json;
    }

    /// <summary>
    /// 新しいキーへ切り替える。切り替えが効かなかった（キーを保存できなかった）ときだけ、写し終えた新しいキーの置き場を消す。
    /// 効いていれば新しいキーは使われているので、消さずに例外をそのまま上げる。
    /// </summary>
    private async Task SwitchToNewKeyAsync(string key)
    {
        try
        {
            await SwitchToAsync(key, new RemoteRosterStore(_http, _baseUri, key));
        }
        catch
        {
            if (_key != key) await RosterRotation.DiscardAsync(_http, _baseUri, key);
            throw;
        }
    }

    /// <summary>このブラウザの登録データを消す。読んだあとに別のタブなどで書き換えられていたら、消さずに残し、そのことを知らせる（写していない登録データを失わないため）。</summary>
    /// <param name="readJson">共有の開始・参加のときに読んだ、このブラウザの登録データ（写した内容）。</param>
    private async Task ClearLocalIfUnchangedAsync(string? readJson)
    {
        string? now;
        try
        {
            now = await ReadLocalAsync();
        }
        catch (RosterUnavailableException)
        {
            AddNotice("このブラウザの登録データを確かめられなかったため、消さずに残しました（データ共有中は使われません）");
            return;
        }
        if (now == readJson)
        {
            await _local.ClearAsync();
            return;
        }
        AddNotice("データ共有を始めている間に、このブラウザに登録データが書き込まれたため、そのデータは消さずに残しました（データ共有中は使われません。内容を確かめて、必要なものは登録し直してください）");
    }

    private static string TrimmedNotice(int count) =>
        $"試合の履歴のうち {count} 件は、サーバーの上限（1 試合 64 KB・メモ 500 文字）に収めるため、推定の根拠（スキャンの記録）を省くか、メモを切って移しました";

    private void AddNotice(string text) => LastNotice = string.IsNullOrEmpty(LastNotice) ? text : LastNotice + " " + text;

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RefreshKeyFromStorageAsync();
    }

    /// <summary>
    /// 今の保存先（読むだけ）。読むときは、保存されているキーを読み直さない（書き込みの前に読み直す）。
    /// 書くときは、毎回 localStorage のキーを確かめ、別のタブでデータ共有を始めていたらクラウドに切り替える。
    /// </summary>
    /// <param name="forWrite">保存のとき true。共有の設定を読めなかったときは、ブラウザへ黙って保存先を切り替えず、例外にする。</param>
    private async Task<RemoteRosterStore?> CurrentRemoteAsync(bool forWrite = false)
    {
        if (!forWrite)
        {
            await EnsureInitializedAsync();
            return _remote;
        }
        // 毎回、保存されているキーを読み直す（別のタブでキーを作り直した・データ共有を止めたときに、このタブで気付くため）
        _initialized = true;
        await RefreshKeyFromStorageAsync();
        if (_keyReadFailed)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "データ共有の設定をブラウザから読めないため、保存を止めています。ページを再読み込みしてください");
        if (_keyLost)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "データ共有が別のタブ・端末で止められたため、保存を止めています。ページを再読み込みしてください");
        return _remote;
    }

    /// <summary>
    /// 保存されているキーを読み直し、今のキーと違う（作り直された）ら、その新しいキーの窓口を返す。同じなら null。
    /// </summary>
    private async Task<RemoteRosterStore?> RereadKeyChangedAsync(RemoteRosterStore stale)
    {
        await RefreshKeyFromStorageAsync();
        return _remote is { } next && !ReferenceEquals(next, stale) ? next : null;
    }

    /// <summary>localStorage からデータ共有のキーを読めなかったことがあるか（読み直して成功するまで true）。</summary>
    private bool _keyReadFailed;

    /// <summary>
    /// データ共有の途中でキーが消えた（別のタブ・端末で止められた）ことがあるか。true の間は、保存を止める（このブラウザの登録データに黙って保存しない）。
    /// 新しいキーで共有を始めると false に戻る。
    /// </summary>
    private bool _keyLost;

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
            // 失敗を利用者に見せ、読み直すまで保存先を切り替えない（保存は CurrentRemoteAsync が止める）。
            // 通知は、成功から失敗に変わったときだけ出す（読むたびに Changed を出さない）
            if (!_keyReadFailed)
            {
                _keyReadFailed = true;
                RecordError($"データ共有の設定を読めませんでした（{ex.Message}）。ページを再読み込みしてください");
            }
            return;
        }
        if (key is null)
        {
            // キーが消えた（別のタブ・端末でデータ共有が止められた）。使えなくなったキーを使い続けず、このタブの共有も止める
            if (_remote is null) return;
            _remote = null;
            _key = null;
            _keyLost = true;
            AfterSwitch();
            RecordError("データ共有が別のタブ・端末で止められたため、このタブでも止まりました。保存は止めています（ページを再読み込みしてください）");
            return;
        }
        if (RemoteRosterStore.IsValidKey(key) && key != _key)
        {
            // http の URL（LAN の IP で開いているときなど）では、アクセスキーを平文で送らないよう、RemoteRosterStore が受け付けない
            try
            {
                var remote = new RemoteRosterStore(_http, _baseUri, key);
                _key = key;
                _remote = remote;
                _keyLost = false;
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
        _keyLost = false;
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

    /// <summary>
    /// クラウドの試合の履歴の窓口。読みは今のキーへ（保存先の読み直しはしない）、書き（足す・消す）は、保存先を読み直してから行う
    /// （別のタブで作り直し・停止があれば、そのとおりに従う）。
    /// </summary>
    private sealed class RemoteHistory(CloudSync sync) : IMatchHistory
    {
        private RemoteRosterStore Reading() =>
            sync._remote ?? throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null, "データ共有が止まっているため、試合の履歴を読めません");

        private async Task<RemoteRosterStore> Writing() =>
            await sync.CurrentRemoteAsync(forWrite: true) ?? throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null, "データ共有が止まっているため、試合の履歴を書けません");

        public Task<IReadOnlyDictionary<string, HistoryTotal>> DataSetTotalsAsync(CancellationToken ct = default) => Reading().DataSetTotalsAsync(ct);

        public Task<MatchPage> ListAsync(string dataSetKey, int limit, int? beforeId = null, CancellationToken ct = default) =>
            Reading().ListAsync(dataSetKey, limit, beforeId, ct);

        public Task<RivalMatch?> GetAsync(string dataSetKey, int id, CancellationToken ct = default) => Reading().GetAsync(dataSetKey, id, ct);

        public async Task<RivalMatch> AppendAsync(string dataSetKey, RivalMatch match, CancellationToken ct = default) =>
            await (await Writing()).AppendAsync(dataSetKey, match, ct);

        public async Task<bool> RemoveAsync(string dataSetKey, int id, CancellationToken ct = default) =>
            await (await Writing()).RemoveAsync(dataSetKey, id, ct);

        public async Task<int> TrimAsync(string dataSetKey, int keepLatest, CancellationToken ct = default) =>
            await (await Writing()).TrimAsync(dataSetKey, keepLatest, ct);
    }
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
