using System.Text.RegularExpressions;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// バトルフィールドの背景（.bg-scene）が、ページの地の色に隠れないための約束。
/// .bg-scene は z-index: -1 で、html に地の色があると body の色が伝播せず body の箱に塗られ、その上に出てしまう。
/// そのため html の地は透明にして、body の色を canvas に伝える（対戦画面のフィールドが見える条件）。
/// </summary>
public class BattleStageBackgroundTests
{
    private static readonly string Css = File.ReadAllText(FindUp("src/FaraPokemonBattleAssistant.Web/wwwroot/css/app.css"));

    [Fact]
    public void The_html_background_is_transparent_so_the_body_color_reaches_the_canvas()
    {
        Assert.Matches(@"(?m)^html,\s*\r?\nhtml\[data-bs-theme\]\s*\{\s*background:\s*transparent;\s*\}", Css);
    }

    [Fact]
    public void The_body_keeps_its_color_for_the_propagation()
    {
        Assert.Matches(@"(?m)^body\s*\{\s*background:\s*var\(--app-body-bg\);\s*\}", Css);
    }

    [Fact]
    public void The_scene_is_fixed_behind_the_page()
    {
        Assert.Matches(@"\.bg-scene\s*\{\s*position:\s*fixed;\s*inset:\s*0;\s*z-index:\s*-1;", Css);
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
