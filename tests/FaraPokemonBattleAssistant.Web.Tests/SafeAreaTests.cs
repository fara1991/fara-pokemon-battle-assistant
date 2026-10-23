using System.Text.RegularExpressions;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>横向きのノッチ端末で文字・ボタンが隠れないための約束（viewport-fit=cover と、左右・下の安全領域の余白）。</summary>
public class SafeAreaTests
{
    private static readonly string Css = File.ReadAllText(FindUp("src/FaraPokemonBattleAssistant.Web/wwwroot/css/app.css"));
    private static readonly string IndexHtml = File.ReadAllText(FindUp("src/FaraPokemonBattleAssistant.Web/wwwroot/index.html"));

    [Fact]
    public void The_viewport_extends_under_the_notch()
    {
        // viewport-fit=cover が無いと env(safe-area-inset-*) が 0 のまま（余白が効かない）。生成した各 HTML は tools/build-entry-pages.test.mjs が見る
        Assert.Matches(@"<meta name=""viewport"" content=""[^""]*viewport-fit=cover[^""]*""", IndexHtml);
    }

    [Theory]
    [InlineData("env(safe-area-inset-left")]
    [InlineData("env(safe-area-inset-right")]
    [InlineData("env(safe-area-inset-bottom")]
    public void The_css_uses_the_safe_area_insets(string expected)
    {
        Assert.Contains(expected, Css);
    }

    [Fact]
    public void The_insets_are_added_to_the_header_the_content_and_the_battle_screen()
    {
        // 値は :root の --safe-l / --safe-r / --safe-b に集め、各所はその変数を使う
        Assert.Matches(@"\.fsk-header \.mud-toolbar\s*\{[^}]*var\(--safe-l\)[^}]*var\(--safe-r\)", Css);
        Assert.Matches(@"\.fsk-content\s*\{\s*padding-right:\s*calc\(1\.5rem \+ var\(--safe-r\)\)", Css);
        Assert.Matches(@"\.battle-ui\s*\{\s*inset:\s*3\.6rem var\(--safe-r\) var\(--safe-b\) var\(--safe-l\)", Css);
        Assert.Matches(@"\.result-bar\s*\{\s*padding:\s*0 var\(--safe-r\) var\(--safe-b\) var\(--safe-l\)", Css);
        Assert.Matches(@"\.fsk-footer\s*\{\s*padding-bottom:\s*calc\(0\.55rem \+ var\(--safe-b\)\)", Css);
    }

    [Fact]
    public void The_sidebar_is_widened_by_the_left_inset()
    {
        // ドロワーの幅（本文の左の位置もこれに従う）に左の安全領域を足す。詳細度を上げて、MudBlazor がテーマから出す :root の値に勝つ
        Assert.Matches(@"html:root\s*\{\s*--mud-drawer-width-left:\s*calc\(230px \+ var\(--safe-l\)\);\s*--mud-drawer-width-mini-left:\s*calc\(56px \+ var\(--safe-l\)\);", Css);
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
