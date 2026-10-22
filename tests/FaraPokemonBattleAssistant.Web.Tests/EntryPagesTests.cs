using System.Text.Json;
using System.Text.RegularExpressions;
using FaraPokemonBattleAssistant.Core;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 検索の入口ページ（tools/entry-pages.json。静的 HTML は tools/build-entry-pages.mjs が作る）と、Blazor のページ・旧 URL の対応表がずれていないか確かめる。
/// </summary>
public class EntryPagesTests
{
    private static readonly string Tools = FindUpDirectory("tools");
    private static readonly string Web = Path.Combine(FindUpDirectory("src"), "FaraPokemonBattleAssistant.Web");

    private static readonly Regex PageRoute = new("^@page \"/([^\"]*)\"", RegexOptions.Multiline);
    private static readonly Regex PageTitle = new("<PageTitle>(.*?)</PageTitle>");

    /// <summary>ルート（先頭の / なし。ホームは空）→ 同じルートのページの &lt;PageTitle&gt;。</summary>
    private static Dictionary<string, string> RazorTitles()
    {
        var result = new Dictionary<string, string>();
        var pagesDir = Path.Combine(Web, "Components", "Pages");
        foreach (var file in Directory.GetFiles(pagesDir, "*.razor"))
        {
            var text = File.ReadAllText(file);
            var route = PageRoute.Match(text);
            var title = PageTitle.Match(text);
            if (!route.Success || !title.Success) continue;
            result[route.Groups[1].Value] = title.Groups[1].Value;
        }
        return result;
    }

    private static List<(string Route, string Title)> EntryPages()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Tools, "entry-pages.json")));
        return doc.RootElement.EnumerateArray()
            .Select(p => (Route: p.GetProperty("route").GetString()!, Title: p.GetProperty("title").GetString()!))
            .ToList();
    }

    [Fact]
    public void Every_entry_page_is_a_razor_page_with_the_same_title()
    {
        // 静的 HTML の title は、起動後に Blazor が PageTitle で置き換える。違うとタブの表示が一瞬ずれるので、同じ文字にしておく
        var razor = RazorTitles();
        foreach (var (route, title) in EntryPages())
        {
            Assert.True(razor.ContainsKey(route), $"@page \"/{route}\" のページがありません");
            Assert.Equal(razor[route], title);
        }
    }

    [Fact]
    public void Entry_pages_are_not_old_urls_and_not_personal_pages()
    {
        foreach (var (route, _) in EntryPages())
        {
            Assert.DoesNotContain(route, SitePaths.Legacy.Keys);   // 旧 URL は転送のまま（静的ファイルを作ると転送が効かなくなる）
            Assert.False(route.StartsWith("my-", StringComparison.Ordinal), route);
            Assert.NotEqual("data-share", route);
        }
    }

    [Fact]
    public void Entry_pages_include_the_home_page_and_the_static_content_pages()
    {
        var routes = EntryPages().Select(p => p.Route).ToHashSet();
        foreach (var required in new[] { "", "battle-terms", "chat-commands", "privacy", "copyright", "report" })
        {
            Assert.Contains(required, routes);
        }
    }

    [Fact]
    public void Template_has_each_marker_exactly_once()
    {
        // 公開用ビルドは、この 2 組の区切りの間だけを書き換える
        var html = File.ReadAllText(Path.Combine(Web, "wwwroot", "index.html"));
        foreach (var marker in new[] { "<!-- SEO-HEAD:START -->", "<!-- SEO-HEAD:END -->", "<!-- SEO-BODY:START -->", "<!-- SEO-BODY:END -->" })
        {
            Assert.Equal(1, CountOccurrences(html, marker));
        }
        Assert.True(html.IndexOf("<!-- SEO-BODY:START -->", StringComparison.Ordinal) > html.IndexOf("<div id=\"app\">", StringComparison.Ordinal));
    }

    [Fact]
    public void Fallback_index_is_generic_and_home_content_is_only_in_the_home_page()
    {
        // 存在しない URL（SPA のフォールバック）は index.html を返す。ホームの本文を出さないため、汎用の title と読み込み表示だけにする
        var html = File.ReadAllText(Path.Combine(Web, "wwwroot", "index.html"));
        Assert.Contains("<title>Pokémon Battle Assistant</title>", html);
        Assert.DoesNotContain("main class=\"entry-static\"", html);
        Assert.Contains("id=\"boot-indicator\"", html);
    }

    [Fact]
    public void Boot_indicator_is_outside_app_so_it_is_not_part_of_the_body_text()
    {
        // 「読み込み中…」は #app の外・本文の前に 1 つだけ。Blazor が #app を描いても残るので、js/boot-indicator.js が外す
        var html = File.ReadAllText(Path.Combine(Web, "wwwroot", "index.html"));
        Assert.Equal(1, CountOccurrences(html, "id=\"boot-indicator\""));
        var indicator = html.IndexOf("id=\"boot-indicator\"", StringComparison.Ordinal);
        Assert.True(indicator < html.IndexOf("<div id=\"app\">", StringComparison.Ordinal));
        Assert.Contains("role=\"status\" aria-live=\"polite\"", html);
        Assert.DoesNotContain("loading-screen", html);
        // 文言は CSS（:empty::before）で出す。生の HTML の本文テキストには「読み込み中」を入れない（JS を実行しない取得でも見えないように）
        Assert.DoesNotMatch(">[^<]*読み込み中", html[html.IndexOf("<body>", StringComparison.Ordinal)..]);
        Assert.Contains(".boot-indicator-label:empty::before", html);
        // 外部の js ファイルで外す（インライン script は CSP で使えない）。Blazor の起動より前に読む
        Assert.True(html.IndexOf("js/boot-indicator.js", StringComparison.Ordinal) < html.IndexOf("_framework/blazor.webassembly.js", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Web, "wwwroot", "js", "boot-indicator.js")));
        // JavaScript が無いときは隠す（noscript の style は _headers の style-src 'unsafe-inline' で許可）
        Assert.Contains("<noscript><style>#boot-indicator { display: none; }</style></noscript>", html);
    }

    [Fact]
    public void Boot_overlay_is_inline_before_the_stylesheets_and_follows_the_theme()
    {
        // 起動前のオーバーレイは CSS の読み込みを待たずに効くよう、head の最初のほうにインラインで書く（テーマは data-bs-theme で切り替える）
        var html = File.ReadAllText(Path.Combine(Web, "wwwroot", "index.html"));
        var style = html.IndexOf("<style>", StringComparison.Ordinal);
        Assert.True(style >= 0 && style < html.IndexOf("<link href=\"_content/MudBlazor/MudBlazor.min.css\"", StringComparison.Ordinal));
        var overlay = RuleBody(html, "#boot-indicator {");
        Assert.Contains("position: fixed; inset: 0;", overlay);
        Assert.Contains("background: #f7f4fd;", overlay);
        Assert.Contains("background: #0c0616;", RuleBody(html, "html[data-bs-theme=\"dark\"] #boot-indicator {"));
        Assert.Contains("border: 0.3rem solid #7c3aed;", RuleBody(html, ".boot-indicator-spinner {"));
        Assert.Contains("border-color: #a855f7;", RuleBody(html, "html[data-bs-theme=\"dark\"] .boot-indicator-spinner {"));
        Assert.Contains("content: \"読み込み中…\";", RuleBody(html, ".boot-indicator-label:empty::before {"));
        // 再読み込みの案内（slow）と、動きを減らす設定では回転を止める
        Assert.Contains("animation: none;", RuleBody(html, "#boot-indicator[data-state=\"slow\"] .boot-indicator-spinner {"));
        var reduced = html.IndexOf("@media (prefers-reduced-motion: reduce) {", StringComparison.Ordinal);
        Assert.True(reduced >= 0, "prefers-reduced-motion で回転を止める規則がありません");
        Assert.Contains("animation: none;", RuleBody(html[reduced..], ".boot-indicator-spinner {"));
    }

    [Fact]
    public void Entry_pages_css_is_defined_for_the_static_body()
    {
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.Contains(".entry-static {", css);
        Assert.Contains(".entry-static h1 {", css);
    }

    [Fact]
    public void Entry_pages_nav_has_no_bullets_and_no_default_blue_links()
    {
        // 起動前の見た目（素の青いリンク・箇条書きの記号）を出さない。リンクの色はアプリの primary（ライト・ダーク）に合わせる
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "app.css"));
        var nav = RuleBody(css, ".entry-static-nav ul {");
        Assert.Contains("list-style: none;", nav);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr));", nav);
        Assert.Contains("color: #7c3aed;", RuleBody(css, ".entry-static-nav a {"));
        Assert.Contains("text-decoration: none;", RuleBody(css, ".entry-static-nav a {"));
        Assert.Contains("color: #a855f7;", RuleBody(css, "[data-bs-theme=\"dark\"] .entry-static-nav a {"));
    }

    [Fact]
    public void Entry_pages_header_bar_is_the_same_height_as_the_app_header()
    {
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.Contains("height: 3.5rem;", RuleBody(css, ".entry-static-bar {"));
        Assert.Contains("height: 3.5rem;", RuleBody(css, ".app-header.mud-appbar {"));
    }

    [Fact]
    public void Report_entry_page_has_no_removed_author_text()
    {
        // 問い合わせの「作者」「ほかの作品」の項目は削除した。静的な入口ページ（検索・起動前の表示）にも、その文言を残さない
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Tools, "entry-pages.json")));
        var report = doc.RootElement.EnumerateArray().Single(p => p.GetProperty("route").GetString() == "report");
        var text = report.GetProperty("description").GetString() + "\n" + string.Join("\n", report.GetProperty("paragraphs").EnumerateArray().Select(p => p.GetString()));
        Assert.DoesNotContain("ほかの作品", text);
        Assert.DoesNotContain("作者の紹介", text);
        Assert.DoesNotContain("Twitter", text);
    }

    private static string RuleBody(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"CSS の規則が無い: {selector}");
        return css[start..css.IndexOf('}', start)];
    }

    private static int CountOccurrences(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string FindUpDirectory(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"{relative} が見つかりません");
    }
}
