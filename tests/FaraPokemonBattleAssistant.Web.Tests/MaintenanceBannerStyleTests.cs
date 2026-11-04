using System.Text.RegularExpressions;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>メンテナンスのお知らせの帯（.fbc-notice。kit の MaintenanceBanner が描く）が、スクロールしてもヘッダーの下に残るための CSS の約束。</summary>
public class MaintenanceBannerStyleTests
{
    private static readonly string Css = File.ReadAllText(FindUp("src/FaraPokemonBattleAssistant.Web/wwwroot/css/app.css"));

    [Fact]
    public void The_banner_is_sticky_just_below_the_header()
    {
        // スマホは window がスクロールするので、sticky で、ヘッダー（MudBlazor の --mud-appbar-height。MudProviders.razor で 56px）の真下に残す
        var rule = Rule(".fbc-notice");
        Assert.Matches(@"position:\s*sticky\s*;", rule);
        Assert.Matches(@"top:\s*var\(--mud-appbar-height,\s*3\.5rem\)\s*;", rule);
    }

    [Fact]
    public void The_banner_sits_under_the_header_and_over_the_page()
    {
        var header = int.Parse(Regex.Match(Rule(".fbc-header.mud-appbar"), @"z-index:\s*(\d+)").Groups[1].Value);
        var banner = int.Parse(Regex.Match(Rule(".fbc-notice"), @"z-index:\s*(\d+)").Groups[1].Value);
        Assert.True(banner < header, "ヘッダー（上に固定）の下に入る");
        Assert.True(banner > 5, "ページ内の固定要素（見出しの行など、数字は小さい）より上に出る");
    }

    [Fact]
    public void The_banner_is_shifted_where_MudBlazor_tucks_the_content_under_the_header()
    {
        // MudBlazor の .mud-main-content は、600px 未満で padding-top を減らし、ヘッダーに少し重ねる（縦は 1/8、横は 1/4）。その分の余白を帯に足す
        Assert.Matches(@"@media\s*\(max-width:\s*599\.98px\)\s*\{\s*\.fbc-notice\s*\{\s*margin-top:\s*calc\(var\(--mud-appbar-height,\s*3\.5rem\)\s*/\s*8\)", Css);
        Assert.Matches(@"@media\s*\(max-width:\s*599\.98px\)\s+and\s+\(orientation:\s*landscape\)\s*\{\s*\.fbc-notice\s*\{\s*margin-top:\s*calc\(var\(--mud-appbar-height,\s*3\.5rem\)\s*/\s*4\)", Css);
    }

    [Fact]
    public void The_banner_stays_below_the_drawer_and_popups()
    {
        // ドロワー（MudBlazor の既定 1200 以上）・ポップアップ（1400 以上）は帯より前に出る。帯に、それより大きい z-index を付けない
        var banner = int.Parse(Regex.Match(Rule(".fbc-notice"), @"z-index:\s*(\d+)").Groups[1].Value);
        Assert.True(banner < 1200, $"帯の z-index（{banner}）は MudBlazor のドロワー（1200 以上）より下");
    }

    [Fact]
    public void The_notice_heading_is_on_its_own_line_and_the_text_follows()
    {
        // 1 行目に見出し「【お知らせ】」、本文は次の行（見出しを block にする）。太字は帯のまま
        Assert.Matches(@"\.fbc-notice-title\s*\{\s*display:\s*block;\s*\}", Css);
        Assert.Matches(@"\.fbc-notice\s*\{[^}]*font-weight:\s*700;", Css);
    }

    [Fact]
    public void The_banner_is_drawn_by_the_shared_component_with_the_heading_on_its_own_line()
    {
        // 帯は kit の MaintenanceBanner（SiteLayout の中）。このサイト独自の MaintenanceBanner は持たない
        Assert.Null(FindUp("src/FaraPokemonBattleAssistant.Web/Shared/MaintenanceBanner.razor", optional: true));
        var razor = File.ReadAllText(FindUp("external/fara-blazor-component/src/FaraBlazorComponent.Razor/Components/MaintenanceBanner.razor"));
        Assert.Contains("<strong class=\"fbc-notice-title\">【お知らせ】</strong>", razor);
        Assert.Contains("<span class=\"fbc-notice-text\">@Info.NoticeText</span>", razor);
        Assert.DoesNotContain("<strong>お知らせ</strong>", razor);
    }

    private static string Rule(string selector)
    {
        var match = Regex.Match(Css, Regex.Escape(selector) + @"\s*\{[^}]*\}");
        Assert.True(match.Success, $"{selector} の規則が無い");
        return match.Value;
    }

    private static string FindUp(string relative, bool optional = false)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return optional ? null! : throw new FileNotFoundException(relative);
    }
}
