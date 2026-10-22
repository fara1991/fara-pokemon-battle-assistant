using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>フッター（問い合わせの文言・SNS のリンク）、サイドメニューの項目、問い合わせ・プライバシーの文言の確認。</summary>
public class FooterAndNavTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");

    [Fact]
    public void Footer_and_pages_use_the_contact_label_not_the_old_one()
    {
        foreach (var file in new[]
        {
            Path.Combine("Shared", "SiteFooter.razor"),
            Path.Combine("Shared", "NavMenu.razor"),
            Path.Combine("Components", "Pages", "Report.razor"),
            Path.Combine("Components", "Pages", "Privacy.razor"),
        })
        {
            Assert.DoesNotContain("作者へ報告", Read(Path.Combine(Web, file)));
        }
    }

    [Fact]
    public void Footer_links_to_the_contact_page_under_the_same_route()
    {
        var footer = Read(Path.Combine(Web, "Shared", "SiteFooter.razor"));
        Assert.Contains("問い合わせ</a>", footer);
        var report = Read(Path.Combine(Web, "Components", "Pages", "Report.razor"));
        Assert.Contains("@page \"/report\"", report);                          // ルートは維持
        Assert.Contains("<PageTitle>問い合わせ | Pokémon Battle Assistant</PageTitle>", report);
        Assert.Contains("<h3>問い合わせ</h3>", report);
    }

    [Fact]
    public void Footer_social_icons_open_in_a_new_tab_safely_with_a_label()
    {
        var footer = Read(Path.Combine(Web, "Shared", "SiteFooter.razor"));
        Assert.Contains("target=\"_blank\"", footer);
        Assert.Contains("rel=\"noopener noreferrer\"", footer);
        Assert.Contains("aria-label=\"@s.Label\"", footer);
        Assert.Contains("<SocialIcon Name=\"@s.Service\"", footer);
        Assert.Contains("SocialLinks.All", footer);
    }

    [Fact]
    public void Social_icon_color_wins_over_the_generic_footer_link_rules()
    {
        // .site-footer a（文字色 inherit・下線・hover の文字色）より詳細度の高いセレクタで、白い図形を保つ
        var css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.Contains(".site-footer a.site-footer-social {", css);
        Assert.Contains(".site-footer a.site-footer-social:hover {", css);
        Assert.Contains(".site-footer a.site-footer-social:focus-visible {", css);
        var baseRule = css[css.IndexOf(".site-footer a.site-footer-social {", StringComparison.Ordinal)..];
        Assert.Contains("color: #fff;", baseRule[..baseRule.IndexOf('}')]);
        var hoverRule = css[css.IndexOf(".site-footer a.site-footer-social:hover {", StringComparison.Ordinal)..];
        Assert.Contains("color: #fff;", hoverRule[..hoverRule.IndexOf('}')]);
    }

    [Fact]
    public void Social_links_match_the_portfolio_urls_and_brand_colors()
    {
        // ポートフォリオ（fara-labs.com）の wwwroot/data/profile.json と、Components/Icon.razor・app.css の .service-* に合わせる
        var expected = new[]
        {
            ("github", "GitHub", "https://github.com/fara1991", "#6e40c9"),
            ("twitter", "Twitter", "https://twitter.com/game_fara", "#1d9bf0"),
            ("twitch", "Twitch", "https://twitch.tv/game_fara", "#9146ff"),
            ("discord", "Discord", "https://discord.com/invite/B38eCSYyk5", "#5865f2"),
        };
        Assert.Equal(expected, SocialLinks.All.Select(s => (s.Service, s.Label, s.Url, s.Color)));
    }

    [Fact]
    public void Social_icons_are_the_portfolio_brand_paths()
    {
        var icon = Read(Path.Combine(Web, "Components", "SocialIcon.razor"));
        // 各 path の先頭（ポートフォリオの Icon.razor と同じ）
        Assert.Contains("M12 .3a12 12 0 0 0-3.8 23.38", icon);                 // GitHub
        Assert.Contains("M23.95 4.57a10 10 0 0 1-2.82.77", icon);             // Twitter
        Assert.Contains("M11.57 4.71h1.72v5.15h-1.72z", icon);                // Twitch
        Assert.Contains("M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152", icon);   // Discord（simple-icons）
        Assert.Contains("viewBox=\"0 0 24 24\"", icon);
        Assert.Contains("aria-hidden=\"true\"", icon);
    }

    [Fact]
    public void Side_menu_has_the_contact_and_privacy_links()
    {
        var nav = Read(Path.Combine(Web, "Shared", "NavMenu.razor"));
        Assert.Contains("(\"report\", \"問い合わせ\"", nav);
        Assert.Contains("(\"privacy\", \"プライバシーポリシー\"", nav);
    }

    [Fact]
    public void Footer_has_no_copyright_note()
    {
        var footer = Read(Path.Combine(Web, "Shared", "SiteFooter.razor"));
        Assert.DoesNotContain("非公式", footer);
        Assert.DoesNotContain("各権利者", footer);
        Assert.DoesNotContain("site-footer-note", footer);
        Assert.DoesNotContain(".site-footer-note", Read(Path.Combine(Web, "wwwroot", "css", "app.css")));   // 未使用の CSS も残さない
    }

    [Fact]
    public void Copyright_page_has_the_heading_route_and_the_notice()
    {
        var page = Read(Path.Combine(Web, "Components", "Pages", "Copyright.razor"));
        Assert.Contains("@page \"/copyright\"", page);
        Assert.Contains("<h3>権利表記</h3>", page);
        Assert.Contains("<PageTitle>権利表記 | Pokémon Battle Assistant</PageTitle>", page);
        Assert.Contains("非公式のファンサイトです", page);
        Assert.Contains("ポケットモンスター・ポケモンに関する名称・画像の権利は、各権利者に帰属します", page);
        Assert.Contains("登録商標です", page);
    }

    [Fact]
    public void About_page_links_to_the_copyright_page()
    {
        var about = Read(Path.Combine(Web, "Components", "Pages", "About.razor"));
        Assert.Contains("<a href=\"copyright\">権利表記</a>", about);
        Assert.DoesNotContain("登録商標です", about);                              // 文言は権利表記のページだけに置く
    }

    [Fact]
    public void Footer_hides_the_separators_on_narrow_screens_and_spaces_the_items_with_gap()
    {
        var css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));
        var start = css.IndexOf("@media (max-width: 767.98px) {\n    .site-footer-line", StringComparison.Ordinal);
        Assert.True(start >= 0, "狭い画面のフッターの規則が無い");
        var block = css[start..css.IndexOf("\n}\n", start, StringComparison.Ordinal)];
        Assert.Contains(".site-footer-sep { display: none; }", block);            // 折り返しても、行頭・行末に | が出ない
        Assert.Contains("gap: 0.35rem 0.9rem;", block);
        Assert.Contains(".site-footer-line { display: flex; flex-wrap: wrap; justify-content: center;", css);   // PC の区切りは残す（規則は変えていない）
        Assert.DoesNotContain(".site-footer-sep { display: none; }", css[..start]);
    }

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

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
