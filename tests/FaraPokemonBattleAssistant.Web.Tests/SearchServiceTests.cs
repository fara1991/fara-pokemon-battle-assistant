using System.Text.Json;
using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>サイト内検索（Services/SearchService.cs）の、探し方・強調・抜き出しと、検索の索引（wwwroot/search-index.json）。</summary>
public class SearchServiceTests
{
    [Theory]
    [InlineData("ＡＢＣ　ｄｅｆ", "abc def")]            // 全角の英数字・スペースを半角に、大文字を小文字に
    [InlineData("ガブリアス", "がぶりあす")]              // カタカナをひらがなに（どちらで打っても探せる）
    [InlineData("ダメージ計算", "だめーじ計算")]
    [InlineData("0～31", "0〜31")]                          // Windows の IME の ～（全角）も、データの 〜 と同じに探せる
    public void Fold_makes_width_kana_and_case_insensitive(string input, string expected) =>
        Assert.Equal(expected, SearchService.Fold(input));

    [Fact]
    public void Fold_keeps_the_length_so_positions_map_back_to_the_original_text()
    {
        const string text = "ＨＰ ガブリアス（ﾒｶﾞ）";
        Assert.Equal(text.Length, SearchService.Fold(text).Length);
    }

    [Fact]
    public void Terms_splits_on_spaces_and_drops_duplicates()
    {
        Assert.Equal(new[] { "dmg", "がぶりあす" }, SearchService.Terms("  DMG　ガブリアス dmg "));
        Assert.Empty(SearchService.Terms("   "));
        Assert.Empty(SearchService.Terms(null));
    }

    [Fact]
    public void Split_marks_every_match_in_the_original_text()
    {
        var parts = SearchService.Split("メガガブリアスZ", SearchService.Terms("がぶりあす")).ToList();
        Assert.Equal(new[] { "メガ", "ガブリアス", "Z" }, parts.Select(p => p.Text));
        Assert.Equal(new[] { false, true, false }, parts.Select(p => p.Match));
        // 一致しないとき・語が無いときは、全部そのまま
        Assert.Equal(new[] { ("abc", false) }, SearchService.Split("abc", SearchService.Terms("x")).ToArray());
        Assert.Equal(new[] { ("abc", false) }, SearchService.Split("abc", Array.Empty<string>()).ToArray());
        // 2 つの語が重なっても、1 つの強調にまとまる
        Assert.Equal(new[] { ("a", false), ("bcd", true), ("e", false) }, SearchService.Split("abcde", SearchService.Terms("bc cd")).ToArray());
    }

    [Fact]
    public void Snippet_cuts_around_the_first_match_with_ellipses()
    {
        var text = new string('あ', 100) + "努力値" + new string('い', 100);
        var snippet = SearchService.Snippet(text, SearchService.Fold(text), SearchService.Terms("努力値"), 60);
        Assert.Contains("努力値", snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.True(snippet.Length <= 62);
        // 一致が無ければ先頭から。短い文章はそのまま
        Assert.Equal("短い文章", SearchService.Snippet("短い文章", SearchService.Fold("短い文章"), SearchService.Terms("x"), 60));
        Assert.Equal("", SearchService.Snippet("", "", SearchService.Terms("x"), 60));
    }

    [Fact]
    public void The_search_index_is_well_formed()
    {
        var path = Path.Combine(RepoRoot(), "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "search-index.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        var types = new[] { "page", "section", "term", "command" };
        foreach (var row in rows)
        {
            Assert.Contains(row.GetProperty("type").GetString(), types);
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("page").GetString()));
            Assert.StartsWith("/", row.GetProperty("url").GetString());
            Assert.NotNull(row.GetProperty("text").GetString());
        }
        // 主なページが入っている（ページの追加・削除で索引を作り直し忘れていないかの目安。CI の `--check` が厳密に確かめる）
        var pages = rows.Where(r => r.GetProperty("type").GetString() == "page").Select(r => r.GetProperty("url").GetString()).ToHashSet();
        Assert.Contains("/damage-calculator", pages);
        Assert.Contains("/chat-commands", pages);
        Assert.Contains("/battle-terms", pages);
        // チャットコマンドの一覧が、取りこぼさず入っている（説明が条件式の add -m や、埋め込みのある faster も）
        var commands = rows.Where(r => r.GetProperty("type").GetString() == "command").Select(r => r.GetProperty("title").GetString()!).ToList();
        Assert.True(commands.Count >= 25, $"コマンドが {commands.Count} 件しかありません");
        Assert.Contains(commands, c => c.Contains(" add -m "));
        Assert.Contains(commands, c => c.Contains(" faster "));
        // 検索結果に出る文字に、C# の断片や、中身のない括弧が混ざっていない
        var leak = new System.Text.RegularExpressions.Regex(@"(var|if|new|foreach)\s|=>|=""|[（(]\s*[)）]");
        foreach (var row in rows)
            Assert.False(leak.IsMatch(row.GetProperty("title").GetString()! + " " + row.GetProperty("text").GetString()), row.GetProperty("title").GetString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonBattleAssistant.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("リポジトリのルートが見つかりません");
    }
}
