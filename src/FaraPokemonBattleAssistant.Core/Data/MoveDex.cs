using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Text;

namespace FaraPokemonBattleAssistant.Core.Data;

/// <summary>技一覧の並び順。</summary>
public enum MoveSort
{
    /// <summary>技名（五十音順）。</summary>
    Name,
    Type,
    Power,
    Accuracy,
    PP,
}

/// <summary>技一覧の絞り込み条件。すべて AND で組み合わせる。</summary>
public sealed class MoveFilter
{
    /// <summary>技名（ひらがな・カタカナ・ローマ字）。空なら絞り込まない。</summary>
    public string Name { get; set; } = "";
    /// <summary>タイプ（英語名。空なら絞り込まない）。</summary>
    public string Type { get; set; } = "";
    public MoveCategory? Category { get; set; }
    /// <summary>この技だけ（0 なら絞り込まない）。</summary>
    public int MoveId { get; set; }
    public MoveSort Sort { get; set; } = MoveSort.Name;
    public bool Descending { get; set; }
}

/// <summary>技一覧（技の絞り込み・並べ替えと、その技を覚えるポケモン）。</summary>
public static class MoveDex
{
    /// <summary>条件に合う技を、指定の順に並べて返す。同じ値どうしは技名の順。</summary>
    public static IReadOnlyList<Move> Query(PokemonDataSet data, MoveFilter filter)
    {
        IEnumerable<Move> list = data.Moves;
        if (filter.MoveId > 0) list = list.Where(m => m.Id == filter.MoveId);
        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            var match = KanaSearch.Create(filter.Name);
            list = list.Where(m => match(m.Name));
        }
        if (!string.IsNullOrEmpty(filter.Type)) list = list.Where(m => m.Type == filter.Type);
        if (filter.Category is { } category) list = list.Where(m => m.Category == category);

        // 技名は五十音順（ひらがなとカタカナを区別せず並べる）
        var ordered = list.OrderBy(m => KanaSearch.Normalize(m.Name), StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
        if (filter.Sort == MoveSort.Name) return filter.Descending ? ordered.AsEnumerable().Reverse().ToList() : ordered;

        var typeOrder = TypeNames.All.ToList();
        Func<Move, int> key = filter.Sort switch
        {
            MoveSort.Type => m => typeOrder.IndexOf(m.Type) is var i and >= 0 ? i : int.MaxValue,   // 「???」などは最後
            MoveSort.Power => m => m.Power,
            MoveSort.Accuracy => m => m.Accuracy,
            _ => m => m.PP,
        };
        // OrderBy は安定ソートなので、同じ値どうしは技名の順のまま
        return (filter.Descending ? ordered.OrderByDescending(key) : ordered.OrderBy(key)).ToList();
    }

    /// <summary>その技を覚えるポケモン（全国図鑑順。未収録のポケモンは除く）。</summary>
    public static IReadOnlyList<Pokemon> Learners(PokemonDataSet data, int moveId) =>
        data.Pokemon.Where(p => !p.IsProvisional && data.CanLearn(p.Id, moveId))
            .OrderBy(p => p, Comparer<Pokemon>.Create(Pokedex.CompareDex)).ToList();
}
