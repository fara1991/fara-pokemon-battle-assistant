using System.Net.Http;

namespace FaraPokemonBattleAssistant.Core.Roster;

/// <summary>
/// アクセスキーの作り直し（公開サイトと配信用BOTで同じ処理を使う）。古いキーの登録データ・相手チーム・試合の履歴を、
/// 新しいキーの空の置き場へ写し、新しいキーを保存したあとに古いキーを消す。写している間に別の端末・BOT が書いた分は、
/// 古いキーを消す直前に確かめ、登録データは全部、履歴は足された分だけを新しいキーへ写し直す。
/// </summary>
/// <remarks>
/// <para>手順: <see cref="PrepareAsync"/>（写す。古いキーは消さない）→ 呼び出し側が新しいキーを保存する → <see cref="FinishAsync"/>（古いキーを消す）。</para>
/// <para>新しいキーを保存できなかったら、<see cref="DiscardAsync(HttpClient, string, string, CancellationToken)"/> で新しいキーの置き場を消す。</para>
/// <para>キーは <see cref="RosterRotationPlan"/> の中にだけあり、ログや例外のメッセージには出さない。</para>
/// </remarks>
public static class RosterRotation
{
    /// <summary>古いキーを消すのを試す回数（そのたびに、写している間に足された分を新しいキーへ写す）。</summary>
    public const int MaxRetries = 5;

    /// <summary>
    /// 作り直しの前半。今のクラウドの登録データ・相手チーム・試合の履歴を、新しいキーの置き場に写す。古いキーは消さない。
    /// 写せなかったときは、新しいキーの置き場を消して例外にする（古いキーのまま）。
    /// </summary>
    /// <param name="http">通信に使う HttpClient（BaseAddress は使わない）</param>
    /// <param name="apiBaseUrl">サイトの URL（例: <see cref="RemoteRosterStore.ProductionBaseUrl"/>）</param>
    /// <param name="oldKey">今のアクセスキー</param>
    /// <exception cref="RosterUnavailableException">データを移せなかった（このときは古いキーのまま）</exception>
    public static async Task<RosterRotationPlan> PrepareAsync(HttpClient http, string apiBaseUrl, string oldKey, CancellationToken ct = default)
    {
        var old = new RemoteRosterStore(http, apiBaseUrl, oldKey);
        var json = await old.LoadAsync(ct);
        // 相手チームを読めていないと、新しいキーに相手チームと履歴が移らないまま、古いキーを消してしまう
        if (old.RivalUnreadable)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "クラウドの相手チームを読み込めなかったため、キーを作り直せません。時間をおいて、もう一度試してください");
        // 写した登録データの版。古いキーを消すとき、この版のままかを確かめる（読んでから書かれていたら、読み直して写し直す）
        var copiedRevision = RevisionOf(old);
        var rosterKeys = json is null ? new List<string>() : RosterRepository.Deserialize(json).DataSets.Keys.ToList();

        var newKey = RemoteRosterStore.GenerateKey();
        var target = new RemoteRosterStore(http, apiBaseUrl, newKey);
        var (trimmed, totals) = await CopyToNewKeyAsync(target, json, old, ct);
        return new RosterRotationPlan(oldKey, newKey, totals, copiedRevision, rosterKeys, trimmed);
    }

    /// <summary>
    /// 登録データ（<paramref name="json"/>。null なら、つながるかだけ確かめる）を、新しいキーの空の置き場（版 0）へ移す。
    /// キーの作り直しなら、古いキーの履歴（<paramref name="historySource"/>）も写す。送った内容が同じ件数で入ったかは、読み直して確かめる。
    /// 途中で失敗したら、新しいキーの置き場を消して例外にする（新しいキーは使われない）。
    /// </summary>
    /// <returns>サーバーの上限（1 試合 64 KB・メモ 500 文字）に収めるため内容を省いた試合の数と、写した時点の古いキーの履歴の状態</returns>
    /// <exception cref="RosterUnavailableException">写せなかった・送った内容が同じ件数で入らなかった</exception>
    public static async Task<(int Trimmed, IReadOnlyDictionary<string, HistoryTotal> Totals)> CopyToNewKeyAsync(
        RemoteRosterStore target, string? json, RemoteRosterStore? historySource = null, CancellationToken ct = default)
    {
        try
        {
            var trimmed = 0;
            IReadOnlyDictionary<string, HistoryTotal> totals = new Dictionary<string, HistoryTotal>();
            if (json is not null)
            {
                var source = RosterRepository.Deserialize(json);
                await target.SaveAsync(json, ct);
                trimmed += target.HistoryTrimmed;
                // 送った内容が、クラウドに同じ件数で入っているかを、読み直して確かめる（違えば、ブラウザ・古いキーを消す前に中止する）
                VerifyCopied(source, RosterRepository.Deserialize(await target.LoadAsync(ct)));
            }
            else await target.LoadAsync(ct); // つながるか確かめる
            if (historySource is not null)
            {
                var copy = await CopyHistoryAsync(historySource, target, ct);
                trimmed += copy.Trimmed;
                totals = copy.Totals;
            }
            return (trimmed, totals);
        }
        catch
        {
            // 新しいキーは使わないので、置き場も消しておく（消せなくても、新しいキーは使われない）
            try { await target.DeleteAsync(CancellationToken.None); } catch (Exception) { /* 消せなくても使わない */ }
            throw;
        }
    }

    /// <summary>
    /// 作り直しの後半。新しいキーを保存したあとに呼ぶ。古いキーを使えなくし、その置き場を消す。
    /// 写している間に古いキーへ書かれた分（履歴・登録データ）は、消す直前に確かめて、足された分だけを新しいキーへ写す。
    /// 何度試しても変わり続けたとき、消す前の確認が通らないまま写し直せなかったときは、古いキーは消さずに止める（<see cref="RosterRotationResult.OldKeyDeleted"/> が false）。
    /// 古いキーがすでに使えなくなっている（410）ときは、消えているので削除済みとして扱う。
    /// </summary>
    /// <remarks>
    /// 古いキーの変更（409）を受けたあとの処理（読み直し・新しいキーへの書き直し・履歴の追加）で失敗したら <see cref="RosterRotationStop.RewriteFailed"/> を返す。
    /// この時点で新しいキーは古いキーの変更を欠いているので、呼び出し側は新しいキーを使わず、古いキーへ戻す（古いキーは消していないので、そのまま使える）。
    /// 戻したあとは、使われない新しいキーの置き場を <see cref="DiscardAsync(HttpClient, string, string, CancellationToken)"/> で消す。
    /// それ以外の想定外の例外（消す処理の応答の読み取りなど）は、例外を外へ抜けさせず <see cref="RosterRotationStop.Failed"/> にする。
    /// </remarks>
    public static async Task<RosterRotationResult> FinishAsync(HttpClient http, string apiBaseUrl, RosterRotationPlan plan, CancellationToken ct = default)
    {
        var old = new RemoteRosterStore(http, apiBaseUrl, plan.OldKey);
        var remote = new RemoteRosterStore(http, apiBaseUrl, plan.NewKey);
        var trimmed = plan.TrimmedMatches;
        var expectedHistory = plan.CopiedTotals;
        long expectedRevision = plan.CopiedRevision;
        var rosterKeys = plan.RosterKeys.ToList();
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                // 確認と削除はサーバーで 1 つの処理なので、その間に取りこぼしは起きない（履歴の状態と登録データの版の両方を見る）
                await old.DeleteIfUnchangedAsync(expectedHistory, expectedRevision, ct);
                return new RosterRotationResult(true, trimmed, null);
            }
            catch (RosterUnavailableException ex) when (ex.Reason == RosterUnavailableReason.KeyRetired)
            {
                // 古いキーは、すでに使えなくなっている（消えている）。消せたのと同じ
                return new RosterRotationResult(true, trimmed, null);
            }
            catch (HistoryChangedException changed)
            {
                // 古いキーは消えていない（409）。写し直しの途中で失敗すると、新しいキーは古いキーの変更を欠いたままになる
                try
                {
                    // 登録データの版が違う（または版が分からない）ときは、古いキーの登録データを読み直して、新しいキーへ全部写し直す
                    if (changed.CurrentRevision != expectedRevision)
                    {
                        var reread = await old.LoadAsync(ct);
                        // 相手チームを読めないまま写すと、新しいキーの相手チームと履歴を空にしてしまう
                        if (old.RivalUnreadable)
                            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                                "クラウドの相手チームを読み込めなかったため、写し直せません。時間をおいて、もう一度試してください");
                        var state = reread is null ? new RosterState() : RosterRepository.Deserialize(reread);
                        rosterKeys = rosterKeys.Union(state.DataSets.Keys).ToList();
                        expectedRevision = RevisionOf(old);   // 書く前に版を取る（読めていないときは、新しいキーへ書かない）
                        await RewriteRosterAsync(remote, state, rosterKeys, ct);
                    }
                    trimmed += await CopyNewMatchesAsync(old, remote, expectedHistory, changed.Current, ct);
                    expectedHistory = changed.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new RosterRotationResult(false, trimmed, RosterRotationStop.RewriteFailed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 消す処理の通信・応答の失敗（想定外の例外を含む）。例外を外へ抜けさせると、切り替え済みなのに結果が分からなくなる
                return new RosterRotationResult(false, trimmed, RosterRotationStop.Failed);
            }
        }
        return new RosterRotationResult(false, trimmed, RosterRotationStop.RetriesExhausted);
    }

    /// <summary>新しいキーの置き場を消す（新しいキーを保存できなかったときなど。使われないキーの置き場を残さないため）。消せたら true。</summary>
    public static async Task<bool> DiscardAsync(HttpClient http, string apiBaseUrl, string newKey, CancellationToken ct = default)
    {
        try
        {
            await new RemoteRosterStore(http, apiBaseUrl, newKey).DeleteAsync(ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 古いキーの登録データを、新しいキーへ全部書き直す（ポケモン → チーム → 相手チームの順。ゲームは <paramref name="keys"/> の全部）。
    /// 写したあとに古いキーへ書かれた分を反映する。古いキーで空になったゲームは空として書く。
    /// </summary>
    private static async Task RewriteRosterAsync(RemoteRosterStore target, RosterState state, IReadOnlyList<string> keys, CancellationToken ct)
    {
        // 書く前に新しいキーの版を読む（版 0 のまま書くと、409 になる）
        await target.LoadAsync(ct);
        foreach (var part in new[] { RosterPart.Pokemon, RosterPart.Teams, RosterPart.Rival })
            foreach (var key in keys)
                await target.SavePartAsync(state, key, part, ct);
        VerifyCopied(state, RosterRepository.Deserialize(await target.LoadAsync(ct)));
    }

    /// <summary>
    /// 送った登録データ（<paramref name="source"/>・送る前の内容）と、クラウドから読み直した内容（<paramref name="copied"/>）の件数を、ゲームごとに比べる。
    /// サーバーの規則（登録に無いポケモンを指す枠・同じポケモンの重複・同じチーム番号の重複・枠の上限）で削られたものがあれば、例外にする。
    /// 0 体のチームは対象外（ポケモンを失わない）。
    /// </summary>
    private static void VerifyCopied(RosterState source, RosterState? copied)
    {
        var problems = new List<string>();
        foreach (var (key, roster) in source.DataSets)
        {
            var sent = CountRoster(roster);
            var got = copied is not null && copied.DataSets.TryGetValue(key, out var c) ? CountRoster(c) : default;
            if (sent != got)
                problems.Add($"{key}: ポケモン {sent.Pokemon}→{got.Pokemon}、チーム {sent.Teams}→{got.Teams}、枠 {sent.Slots}→{got.Slots}、相手 {sent.Rivals}→{got.Rivals}");
        }
        if (problems.Count > 0)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "クラウドに写せない内容（登録に無いポケモンを指す枠、同じポケモンの重複、同じ番号のチーム、枠が 7 体以上のチーム など）があったため中止しました。" +
                "今のアクセスキーは変わっていません。（" + string.Join("・", problems) + "）");
    }

    /// <summary>ゲームの登録数（ポケモン・枠のあるチーム・チームの枠の合計・相手チーム）。</summary>
    private static (int Pokemon, int Teams, int Slots, int Rivals) CountRoster(DataSetRoster roster)
    {
        var teams = roster.Teams.Where(t => t.Slots.Count > 0).ToList();
        return (roster.Pokemon.Count, teams.Count, teams.Sum(t => t.Slots.Count), roster.Rivals.Count);
    }

    /// <summary>
    /// 古いキーの履歴を、全部のゲーム分、新しいキーへ写す。新しいキーに写した件数がそろっていることを確かめる。
    /// 写している間の変化は、古いキーを消すとき（<see cref="FinishAsync"/>）に、サーバーが写した時点の状態と見比べて検出する。
    /// </summary>
    private static async Task<(int Trimmed, IReadOnlyDictionary<string, HistoryTotal> Totals)> CopyHistoryAsync(RemoteRosterStore source, RemoteRosterStore target, CancellationToken ct)
    {
        var before = await source.DataSetTotalsAsync(ct);
        var trimmed = 0;
        foreach (var (dataSetKey, state) in before)
        {
            var matches = await ReadAllHistoryOldestFirstAsync(source, dataSetKey, state.Total, state.NextId, ct);
            trimmed += await target.AppendManyAsync(dataSetKey, matches, ct);
        }
        var copied = await target.DataSetTotalsAsync(ct);
        if (copied.Count != before.Count || before.Any(x => !copied.TryGetValue(x.Key, out var other) || other.Total != x.Value.Total))
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "履歴を新しいアクセスキーへ写せなかったため、中止しました。もう一度試してください");
        return (trimmed, before);
    }

    /// <summary>
    /// 写し終えたあとに、古いキーへ足された試合（写した時点の次の Id 以降）だけを、新しいキーへ足す。足した試合のうち、内容を省いた数を返す。
    /// </summary>
    private static async Task<int> CopyNewMatchesAsync(RemoteRosterStore source, RemoteRosterStore target,
        IReadOnlyDictionary<string, HistoryTotal> previous, IReadOnlyDictionary<string, HistoryTotal> current, CancellationToken ct)
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
                var page = await source.ListAsync(dataSetKey, 200, before, ct);
                added.AddRange(page.Items.TakeWhile(m => m.Id >= from));
                finished = !page.HasMore || page.Items.Count == 0 || page.Items[^1].Id < from;
                if (!finished) before = page.Items[^1].Id;
            }
            if (!finished)   // 読み切れていないまま写すと、残りを失うので、中止する
                throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null, "履歴を最後まで読めなかったため、キーを作り直せません。もう一度試してください");
            added.Reverse();
            if (added.Count > 0) trimmed += await target.AppendManyAsync(dataSetKey, added, ct);
        }
        return trimmed;
    }

    /// <summary>1 つのゲームの履歴を（<paramref name="nextId"/> より前の分だけ）全部、古い順に読む（1 回に 200 件ずつ）。サーバーの応答がおかしくて終わらない・途中で切れるときは失敗にする。</summary>
    private static async Task<List<RivalMatch>> ReadAllHistoryOldestFirstAsync(RemoteRosterStore source, string dataSetKey, int expectedTotal, int nextId, CancellationToken ct)
    {
        var all = new List<RivalMatch>();
        int? before = nextId;   // 一覧を取った時点の次の Id より前だけ読む（読んでいる間に足された試合は、あとで足された分として写す）
        for (var pages = 0; pages <= expectedTotal / 200 + 2; pages++)
        {
            var page = await source.ListAsync(dataSetKey, 200, before, ct);
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

    /// <summary>直近の読み込みで分かった登録データの版。</summary>
    private static long RevisionOf(RemoteRosterStore remote) =>
        remote.Revision ?? throw new InvalidOperationException("登録データの版を読めていません");
}

/// <summary>
/// キーの作り直しの前半の結果。登録データと履歴は新しいキーへ写し終えているが、古いキーはまだ消していない。
/// キーを含むので、<see cref="ToString"/> は中身を出さない。
/// </summary>
public sealed class RosterRotationPlan
{
    internal RosterRotationPlan(string oldKey, string newKey, IReadOnlyDictionary<string, HistoryTotal> copiedTotals, long copiedRevision,
        IReadOnlyList<string> rosterKeys, int trimmedMatches)
    {
        OldKey = oldKey;
        NewKey = newKey;
        CopiedTotals = copiedTotals;
        CopiedRevision = copiedRevision;
        RosterKeys = rosterKeys;
        TrimmedMatches = trimmedMatches;
    }

    /// <summary>今のアクセスキー（まだ使える）。</summary>
    public string OldKey { get; }

    /// <summary>新しいアクセスキー（まだ保存していない）。</summary>
    public string NewKey { get; }

    /// <summary>写した時点の、古いキーの履歴の状態（古いキーを消すときの見比べに使う）。</summary>
    public IReadOnlyDictionary<string, HistoryTotal> CopiedTotals { get; }

    /// <summary>写した登録データの版。古いキーへ後から登録が書かれていたら、消さずに写し直す。</summary>
    public long CopiedRevision { get; }

    /// <summary>写した登録データのゲーム（写し直すときに、古いキーで空になったゲームも空として書くため）。</summary>
    public IReadOnlyList<string> RosterKeys { get; }

    /// <summary>サーバーの上限に収めるため、内容を省いて写した試合の数（前半まで）。</summary>
    public int TrimmedMatches { get; }

    public override string ToString() => "RosterRotationPlan（キーは表示しません）";
}

/// <summary>キーの作り直しの後半の結果。</summary>
/// <param name="OldKeyDeleted">古いキーを消せたか（false のときは古いキーは残っている）</param>
/// <param name="TrimmedMatches">前半から後半までに、内容を省いて写した試合の数</param>
/// <param name="Stop">消せなかった理由（消せたら null）</param>
public sealed record RosterRotationResult(bool OldKeyDeleted, int TrimmedMatches, RosterRotationStop? Stop);

/// <summary>古いキーを消せなかった理由。</summary>
public enum RosterRotationStop
{
    /// <summary>写している間の変更が続き、<see cref="RosterRotation.MaxRetries"/> 回試しても消せなかった。</summary>
    RetriesExhausted,
    /// <summary>消す処理の通信・応答などで失敗した（古いキーの変更の写し直しには、まだ進んでいない）。</summary>
    Failed,
    /// <summary>
    /// 古いキーの変更を新しいキーへ写している途中で失敗した。新しいキーは古いキーの登録データ・履歴を欠いたままになっている
    /// （古いキーは消していない。新しいキーを使う前に、古いキーのデータを確かめる必要がある）。
    /// </summary>
    RewriteFailed,
}
