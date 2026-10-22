using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>ヘッダーの上端の線（スクロール量の表示）が、CSS・ヘッダー・スクリプトの読み込みに入っていること。動き自体は tests/js/scroll-progress.test.mjs で確かめる。</summary>
public class ScrollProgressTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");

    [Fact]
    public void Header_has_the_progress_line_as_a_hidden_decoration_inside_the_app_bar()
    {
        var layout = File.ReadAllText(Path.Combine(Web, "Shared", "MainLayout.razor"));
        var start = layout.IndexOf("<MudAppBar", StringComparison.Ordinal);
        var end = layout.IndexOf("</MudAppBar>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "MudAppBar が見つからない");

        var header = layout[start..end];
        Assert.Contains("class=\"scroll-progress\"", header);
        Assert.Contains("aria-hidden=\"true\"", header);
    }

    [Fact]
    public void Progress_line_is_a_2px_scaleX_bar_on_the_header_edge_that_ignores_the_pointer()
    {
        var css = ReadRule(".scroll-progress");
        Assert.Contains("position: absolute;", css);
        Assert.Contains("top: 0;", css);
        Assert.Contains("height: 2px;", css);
        Assert.Contains("transform: scaleX(var(--scroll-progress, 0));", css);
        Assert.Contains("pointer-events: none;", css);
    }

    [Fact]
    public void Progress_line_uses_a_header_accent_token_defined_on_root()
    {
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.Contains("background: var(--app-header-accent);", ReadRule(".scroll-progress"));
        Assert.Matches(@":root,\s*\[data-bs-theme=""light""\]\s*\{[^}]*--app-header-accent: linear-gradient\(", css);
    }

    [Fact]
    public void Reduced_motion_keeps_the_line_but_only_drops_the_transition()
    {
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "app.css"));
        var start = css.IndexOf("@media (prefers-reduced-motion: no-preference)", StringComparison.Ordinal);
        Assert.True(start >= 0, "動きを許すときだけ transition を付けるメディアクエリが無い");
        var block = css[start..css.IndexOf("}\n}", start, StringComparison.Ordinal)];
        Assert.Contains(".scroll-progress", block);
        Assert.Contains("transition: transform", block);
        Assert.DoesNotContain("display: none", ReadRule(".scroll-progress"));
    }

    [Fact]
    public void Scroll_progress_script_is_loaded_from_index_html()
    {
        var index = File.ReadAllText(Path.Combine(Web, "wwwroot", "index.html"));
        Assert.Contains("<script src=\"js/scroll-progress.js\"></script>", index);

        var js = File.ReadAllText(Path.Combine(Web, "wwwroot", "js", "scroll-progress.js"));
        Assert.Contains("--scroll-progress", js);                         // 値は html に入れる（#app の style は書かない）
        Assert.Contains("main > article.content", js);                    // PC でスクロールする要素
        Assert.Contains("document.scrollingElement", js);                 // スマホは window
        Assert.Contains(".page.battle-active", js);                       // 対戦画面は 0
    }

    [Fact]
    public void Scroll_progress_test_runs_in_ci()
    {
        var workflow = File.ReadAllText(FindUpFile(".github/workflows/build.yml"));
        Assert.Contains("run: npm run test:scroll", workflow);
    }

    private static string FindUpFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    private string ReadRule(string selector)
    {
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "app.css"));
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} のルールが無い");
        return css[start..css.IndexOf('}', start)];
    }

    private static string FindUpDirectory(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException(relative);
    }
}
