using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>
/// 日本語の並び順・検索結果が変わっていないことを確かめる。
/// ポケモン一覧・技一覧のすべての並べ替え（昇順・降順）と、検索（かな・ローマ字・漢字・記号）の結果を
/// Data/order_snapshot.txt と比べる。グローバリゼーション（ICU）の設定を変えても同じ結果になることを確かめる
/// （通常の実行と、環境変数 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 の実行の両方で通す）。
/// 並びを意図して変えたときだけ、環境変数 ORDER_SNAPSHOT_WRITE=1 で作り直す。
/// </summary>
public class OrderSnapshotTests
{
    private static readonly string[] SearchQueries =
    {
        "が", "ガ", "がぶ", "ガブ", "gabu", "gaburiasu", "ガブリアス",
        "なみ", "ナミ", "nami", "namino", "なみのり",
        "けんり", "ケンリ", "kenri", "ケンリ",
        "ぼるとちぇんじ", "ボルト", "ピカ", "pika", "ぴかちゅう",
        "メガ", "mega", "リザードン", "x", "Ｘ", "ー", "チェ", "ぱ", "パ",
        "ひかり", "ヒカリ", "ダブル", "ｶﾞ",
    };

    [Fact]
    public async Task Japanese_sort_orders_and_search_results_match_snapshot()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var dex = new Pokedex(data);
        var lines = new List<string>();

        foreach (var sort in Enum.GetValues<PokedexSort>())
        foreach (var desc in new[] { false, true })
        {
            var list = dex.Query(new PokedexFilter { Sort = sort, Descending = desc });
            lines.Add($"dex {sort} desc={desc}: " + string.Join(",", list.Select(p => p.Id)));
        }

        foreach (var sort in Enum.GetValues<MoveSort>())
        foreach (var desc in new[] { false, true })
        {
            var list = MoveDex.Query(data, new MoveFilter { Sort = sort, Descending = desc });
            lines.Add($"move {sort} desc={desc}: " + string.Join(",", list.Select(m => m.Id)));
        }

        foreach (var query in SearchQueries)
        {
            var match = KanaSearch.Create(query);
            lines.Add($"search pokemon [{query}]: " + string.Join(",", data.Pokemon.Where(p => match(p.Name)).Select(p => p.Id)));
            lines.Add($"search move [{query}]: " + string.Join(",", data.Moves.Where(m => match(m.Name)).Select(m => m.Id)));
        }

        var actual = string.Join("\n", lines) + "\n";
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("ORDER_SNAPSHOT_WRITE") == "1")
        {
            await File.WriteAllTextAsync(path, actual);
            Assert.Fail("並びのスナップショットを作り直しました。git diff で変化を確かめてから、ORDER_SNAPSHOT_WRITE を外して再実行してください");
        }

        var expected = await File.ReadAllTextAsync(path);
        Assert.Equal(expected.Replace("\r\n", "\n"), actual.Replace("\r\n", "\n"));
    }

    private static string SnapshotPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonBattleAssistant.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "FaraPokemonBattleAssistant.Core.Tests", "Data", "order_snapshot.txt");
    }
}
