using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>ヘッダーの下端の線（スクロール量の表示。kit の ScrollProgress）が、CSS・ヘッダー・スクリプトの読み込みに入っていること。動き自体は kit の tests/js/kit-js.test.mjs（npm run test:kit-js）で確かめる。</summary>
public class ScrollProgressTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");
    private static readonly string Kit = FindUpDirectory("external/fara-blazor-component/src/FaraBlazorComponent.Razor");
    private static readonly string Css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));

    [Fact]
    public void Header_has_the_progress_line_as_a_hidden_decoration_inside_the_app_bar()
    {
        var layout = Read(Path.Combine(Kit, "Components", "SiteLayout.razor"));
        var start = layout.IndexOf("<MudAppBar", StringComparison.Ordinal);
        var end = layout.IndexOf("</MudAppBar>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "MudAppBar が見つからない");

        var header = layout[start..end];
        Assert.Contains("<ScrollProgress />", header);
        var line = Read(Path.Combine(Kit, "Components", "ScrollProgress.razor"));
        Assert.Contains("class=\"fbc-scroll-progress\"", line);
        Assert.Contains("aria-hidden=\"true\"", line);
    }

    [Fact]
    public void Progress_line_is_a_2px_bar_on_the_header_bottom_edge_that_ignores_the_pointer()
    {
        var rule = ReadRule(".fbc-scroll-progress");
        Assert.Contains("position: absolute;", rule);
        Assert.Contains("bottom: 0;", rule);
        Assert.Contains("height: 2px;", rule);
        Assert.Contains("pointer-events: none;", rule);

        var fill = ReadRule(".fbc-scroll-progress::before");
        Assert.Contains("transform: scaleX(var(--scroll-progress, 0));", fill);   // 値は線自身の --scroll-progress
        Assert.Contains("transform-origin: left;", fill);
    }

    [Fact]
    public void Header_bottom_border_is_replaced_by_the_progress_line()
    {
        Assert.DoesNotContain("border-bottom", ReadRule(".fbc-header.mud-appbar"));
    }

    [Fact]
    public void Progress_line_uses_a_header_accent_token_defined_on_root()
    {
        Assert.Contains("background: var(--app-header-accent);", ReadRule(".fbc-scroll-progress::before"));
        Assert.Contains("background: var(--app-header-track);", ReadRule(".fbc-scroll-progress"));
        Assert.Matches(@":root,\s*\[data-bs-theme=""light""\]\s*\{[^}]*--app-header-accent: linear-gradient\(", Css);
    }

    [Fact]
    public void Reduced_motion_keeps_the_line_but_only_drops_the_transition()
    {
        const string header = "@media (prefers-reduced-motion: no-preference) {";
        var start = Css.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "動きを許すときだけ transition を付けるメディアクエリが無い");
        var end = Css.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, "メディアクエリの終わりが見つからない");
        var block = Css[start..end];
        Assert.Contains(".fbc-scroll-progress::before", block);
        Assert.Contains("transition: transform", block);
        Assert.DoesNotContain("display: none", ReadRule(".fbc-scroll-progress::before"));
    }

    [Fact]
    public void Scroll_progress_script_is_loaded_from_index_html()
    {
        var index = Read(Path.Combine(Web, "wwwroot", "index.html"));
        Assert.Contains("<script src=\"_content/FaraBlazorComponent.Razor/js/scroll-progress.js\"></script>", index);
        Assert.False(File.Exists(Path.Combine(Web, "wwwroot", "js", "scroll-progress.js")), "kit の scroll-progress.js に一本化した");

        var js = Read(Path.Combine(Kit, "wwwroot", "js", "scroll-progress.js"));
        Assert.Contains("--scroll-progress", js);                         // 値は線自身の style に入れる（html の style は書かない）
        Assert.Contains("main > article.fbc-content", js);                // PC で動く本文の要素
        Assert.Contains("document.scrollingElement", js);                 // スマホは window
        // 対戦画面（.fbc-page.battle-active）では、線は伸ばさない（kit の JS は対戦画面を知らないので、このサイトの app.css が 0 にする）
        Assert.Contains("transform: scaleX(0);", ReadRule(".fbc-page.battle-active .fbc-scroll-progress::before"));
    }

    [Fact]
    public void Motion_script_does_not_rescan_when_only_the_progress_line_changes()
    {
        var motion = Read(Path.Combine(Web, "wwwroot", "js", "motion.js"));
        Assert.Contains("closest(\".fbc-scroll-progress\")", motion);
    }

    [Fact]
    public void Kit_js_test_runs_in_ci()
    {
        var workflow = Read(FindUpFile(".github/workflows/build.yml"));
        Assert.Contains("run: npm run test:kit-js", workflow);   // kit の JS（スクロール量の線・読み込み表示・検索欄・報告）のテスト
    }

    /// <summary>読んだ文字列の改行を \n にそろえる（app.css などは CRLF のことがあるため、検索は \n で書く）。</summary>
    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string ReadRule(string selector)
    {
        var start = Css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} のルールが無い");
        return Css[start..Css.IndexOf('}', start)];
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

    private static string FindUpFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
