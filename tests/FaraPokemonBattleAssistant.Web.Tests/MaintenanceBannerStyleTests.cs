using System.Text.RegularExpressions;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>メンテナンスのお知らせの帯（.maintenance-notice）が、スクロールしてもヘッダーの下に残るための CSS の約束。</summary>
public class MaintenanceBannerStyleTests
{
    private static readonly string Css = File.ReadAllText(FindUp("src/FaraPokemonBattleAssistant.Web/wwwroot/css/app.css"));

    [Fact]
    public void The_banner_is_sticky_just_below_the_header()
    {
        // スマホは window がスクロールするので、sticky で、ヘッダー（MudBlazor の --mud-appbar-height。MudProviders.razor で 56px）の真下に残す
        var rule = Rule(".maintenance-notice");
        Assert.Matches(@"position:\s*sticky\s*;", rule);
        Assert.Matches(@"top:\s*var\(--mud-appbar-height,\s*3\.5rem\)\s*;", rule);
    }

    [Fact]
    public void The_banner_sits_under_the_header_and_over_the_page()
    {
        var header = int.Parse(Regex.Match(Rule(".app-header.mud-appbar"), @"z-index:\s*(\d+)").Groups[1].Value);
        var banner = int.Parse(Regex.Match(Rule(".maintenance-notice"), @"z-index:\s*(\d+)").Groups[1].Value);
        Assert.True(banner < header, "ヘッダー（上に固定）の下に入る");
        Assert.True(banner > 5, "ページ内の固定要素（見出しの行など、数字は小さい）より上に出る");
    }

    [Fact]
    public void The_banner_is_shifted_where_MudBlazor_tucks_the_content_under_the_header()
    {
        // MudBlazor の .mud-main-content は、600px 未満で padding-top を減らし、ヘッダーに少し重ねる（縦は 1/8、横は 1/4）。その分の余白を帯に足す
        Assert.Matches(@"@media\s*\(max-width:\s*599\.98px\)\s*\{\s*\.maintenance-notice\s*\{\s*margin-top:\s*calc\(var\(--mud-appbar-height,\s*3\.5rem\)\s*/\s*8\)", Css);
        Assert.Matches(@"@media\s*\(max-width:\s*599\.98px\)\s+and\s+\(orientation:\s*landscape\)\s*\{\s*\.maintenance-notice\s*\{\s*margin-top:\s*calc\(var\(--mud-appbar-height,\s*3\.5rem\)\s*/\s*4\)", Css);
    }

    [Fact]
    public void The_banner_stays_below_the_drawer_and_popups()
    {
        // ドロワー（MudBlazor の既定 1200 以上）・ポップアップ（1400 以上）は帯より前に出る。帯に、それより大きい z-index を付けない
        var banner = int.Parse(Regex.Match(Rule(".maintenance-notice"), @"z-index:\s*(\d+)").Groups[1].Value);
        Assert.True(banner < 1200, $"帯の z-index（{banner}）は MudBlazor のドロワー（1200 以上）より下");
    }

    private static string Rule(string selector)
    {
        var match = Regex.Match(Css, Regex.Escape(selector) + @"\s*\{[^}]*\}");
        Assert.True(match.Success, $"{selector} の規則が無い");
        return match.Value;
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
