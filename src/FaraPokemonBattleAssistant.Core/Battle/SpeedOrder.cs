namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>順番に並べるポケモン 1 体（名前と、いまの素早さ）。<paramref name="Team"/> は側の名前（自分側・相手側など。色分けに使う）、<paramref name="Slot"/> は呼び出し側が元のポケモンを引き当てるための番号。</summary>
public sealed record SpeedEntry(string Label, int Speed, string Team = "", int Slot = -1);

/// <summary>同じ順位のポケモン（同速なら複数）。<paramref name="Rank"/> は 1 から（同速の次は飛ぶ: 1, 1, 3 位）。</summary>
public sealed record SpeedRank(int Rank, IReadOnlyList<SpeedEntry> Entries)
{
    /// <summary>同速で、どちらが先に動くかランダムなグループか。</summary>
    public bool IsTie => Entries.Count > 1;
}

/// <summary>ダブルバトルの素早さ比較（4 体の動く順番）。</summary>
public static class SpeedOrder
{
    /// <summary>
    /// 素早さの順（先に動く方が先）に並べる。同じ素早さは 1 つのグループ（順位は同じ）にまとめる。
    /// トリックルーム中は、素早さが遅い方が先に動く。
    /// </summary>
    public static IReadOnlyList<SpeedRank> Rank(IEnumerable<SpeedEntry> entries, bool trickRoom)
    {
        var ordered = trickRoom ? entries.OrderBy(e => e.Speed) : entries.OrderByDescending(e => e.Speed);
        var result = new List<SpeedRank>();
        var position = 0;
        foreach (var group in ordered.GroupBy(e => e.Speed))
        {
            var members = group.ToList();
            result.Add(new SpeedRank(position + 1, members));
            position += members.Count;
        }
        return result;
    }

    /// <summary>順位を「1 位 → 2 位 → …」のような 1 行にする（同速は「A = B」）。</summary>
    public static string Describe(IReadOnlyList<SpeedRank> ranks) =>
        string.Join(" → ", ranks.Select(r => string.Join(" = ", r.Entries.Select(e => e.Label))));
}
