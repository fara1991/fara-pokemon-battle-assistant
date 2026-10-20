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
    /// <summary>履歴のあるゲームと、その件数（ゲームのキーは小文字）。キーの作り直しで、履歴を写し忘れないために使う。</summary>
    Task<IReadOnlyDictionary<string, int>> DataSetTotalsAsync(CancellationToken ct = default);

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

/// <summary>試合の履歴が上限に達していて、足せない。メッセージはそのまま利用者に見せられる。</summary>
public sealed class HistoryLimitException : Exception
{
    public HistoryLimitException(string message) : base(message)
    {
    }
}
