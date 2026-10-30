namespace FaraPokemonBattleAssistant.Core.Roster;

/// <summary>試合の履歴の 1 ページ分（新しい順）。</summary>
/// <param name="Total">そのゲームの履歴の全件数。</param>
/// <param name="Items">このページの試合（新しい順）。</param>
/// <param name="HasMore">さらに古い試合があるか。</param>
public sealed record MatchPage(int Total, IReadOnlyList<RivalMatch> Items, bool HasMore);

/// <summary>
/// 試合の履歴の窓口。クラウド（<see cref="RemoteRosterStore"/>）では、履歴は登録データ（ポケモン・チーム・相手チーム）とは別に
/// サーバーが 1 試合 1 行で持つので、全件を読まずに、必要なページだけを読み書きする。
/// （ブラウザ・ファイルに保存しているときは、履歴は登録データの中にあり、<see cref="RosterRepository"/> が直接扱う。）
/// </summary>
public interface IMatchHistory
{
    /// <summary>履歴のあるゲームと、その件数・次に振る Id（ゲームのキーは小文字）。キーの作り直しで、履歴を写し忘れないために使う。</summary>
    Task<IReadOnlyDictionary<string, HistoryTotal>> DataSetTotalsAsync(CancellationToken ct = default);

    /// <summary>試合の一覧（新しい順）。<paramref name="beforeId"/> を付けると、その Id より前の試合から。</summary>
    Task<MatchPage> ListAsync(string dataSetKey, int limit, int? beforeId = null, CancellationToken ct = default);

    /// <summary>1 試合を読む。無ければ null。</summary>
    Task<RivalMatch?> GetAsync(string dataSetKey, int id, CancellationToken ct = default);

    /// <summary>試合を 1 つ足す。Id はサーバーが割り当てる（戻り値の <see cref="RivalMatch.Id"/>）。上限を超えるときは <see cref="HistoryLimitException"/>。</summary>
    Task<RivalMatch> AppendAsync(string dataSetKey, RivalMatch match, CancellationToken ct = default);

    /// <summary>1 試合を消す。無ければ false。</summary>
    Task<bool> RemoveAsync(string dataSetKey, int id, CancellationToken ct = default);

    /// <summary>新しい <paramref name="keepLatest"/> 件を残して、古い試合を消す。消した件数を返す。</summary>
    Task<int> TrimAsync(string dataSetKey, int keepLatest, CancellationToken ct = default);
}

/// <summary>1 つのゲームの履歴の件数と、次に振る Id（増える一方。足した・消したことを見分けられる）。</summary>
public sealed record HistoryTotal(int Total, int NextId);

/// <summary>
/// キーの作り直しで古いキーを消そうとしたら、写している間に履歴か登録データが変わっていた（別の端末・BOT が足した・消した）。何も消されていない。
/// <see cref="Current"/> は、今の履歴の状態。<see cref="CurrentRevision"/> は、今の登録データの版（サーバーが返さなかったときは null）。
/// </summary>
public sealed class HistoryChangedException : Exception
{
    public HistoryChangedException(IReadOnlyDictionary<string, HistoryTotal> current) : this(current, null)
    {
    }

    public HistoryChangedException(IReadOnlyDictionary<string, HistoryTotal> current, long? currentRevision) : base("履歴か登録データが変わったため、古いアクセスキーは消しませんでした")
    {
        Current = current;
        CurrentRevision = currentRevision;
    }

    public IReadOnlyDictionary<string, HistoryTotal> Current { get; }

    /// <summary>今の登録データの版（サーバーが返したとき）。登録データが写したあとに変わったかを見る。</summary>
    public long? CurrentRevision { get; }
}

/// <summary>試合の履歴が上限に達していて、足せない。メッセージはそのまま利用者に見せられる。</summary>
public sealed class HistoryLimitException : Exception
{
    public HistoryLimitException(string message) : base(message)
    {
    }
}
