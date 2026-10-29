using FaraPokemonBattleAssistant.Web.Services;
using FaraBlazorComponent.Core;
using FaraBlazorComponent.Razor;
using FaraBlazorComponent.Razor.Components;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>フッター（問い合わせの文言・SNS のリンク）、サイドメニューの項目、問い合わせ・プライバシーの文言の確認。</summary>
public class FooterAndNavTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");
    /// <summary>共通部品（fara-blazor-component の Razor）のソース。フッターなどの中身は、ここにある。</summary>
    private static readonly string Kit = FindUpDirectory("external/fara-blazor-component/src/FaraBlazorComponent.Razor");

    [Fact]
    public void Footer_and_pages_use_the_contact_label_not_the_old_one()
    {
        foreach (var file in new[]
        {
            Path.Combine("Shared", "NavMenu.razor"),
            Path.Combine("Components", "Pages", "Report.razor"),
            Path.Combine("Components", "Pages", "Privacy.razor"),
        })
        {
            Assert.DoesNotContain("作者へ報告", Read(Path.Combine(Web, file)));
        }
        Assert.DoesNotContain("作者へ報告", Read(Path.Combine(Kit, "Components", "SiteFooter.razor")));   // フッターは kit の部品
    }

    // プライバシーポリシーの日付（本文を改定したら、ここと Privacy.razor を同じ日にそろえる）
    private const string PrivacyEnactedDate = "2026 年 10 月 22 日";
    private const string PrivacyRevisedDate = "2026 年 10 月 29 日";

    [Fact]
    public void Privacy_policy_matches_the_implementation_and_makes_no_operator_promises()
    {
        var privacy = Read(Path.Combine(Web, "Components", "Pages", "Privacy.razor"));
        // 実装に合わせた記載（アクセスキーのブラウザ保存・暗号化なし・削除・外部サービス・利用目的・制定日と最終改定日）
        foreach (var required in new[] { "アクセスキー", "暗号化", "データ共有をやめる", "アクセスキーを作り直す", "Google Fonts", "Turnstile", "GitHub", "パスワードやアクセスキーなど", "元に戻せない識別子", "利用目的", "Cookie", PrivacyEnactedDate, PrivacyRevisedDate })
        {
            Assert.Contains(required, privacy);
        }
        // 運営者の運用の約束・法的な適用の注記・古い記載・表記ゆれは書かない
        // 閲覧者に要らない技術の詳細（ホスト名・保存のキー名・ハッシュ・トークン・保存の仕組みの名前）は出さない
        foreach (var jargon in new[] { "localStorage", "syncKey", "fara-pokemon-battle-assistant.", "fara-labs.com", "raw.githubusercontent.com", "googleapis", "ハッシュ", "トークン", "秘密の鍵", "SHA", "HMAC", "R2", "Durable", "キャッシュ" })
        {
            Assert.DoesNotContain(jargon, privacy);
        }
        foreach (var forbidden in new[] { "見ません", "閲覧しません", "必要以上に保管", "サーバーには送りません", "個人を特定する情報は集めません", "個人を特定する情報を受け取りません", "匿名", "BOT", "お問い合わせ", "掃除", "追記します" })
        {
            Assert.DoesNotContain(forbidden, privacy);
        }
    }

    [Fact]
    public void Privacy_policy_headings_follow_the_page_title_and_links_use_the_absolute_route()
    {
        var privacy = Read(Path.Combine(Web, "Components", "Pages", "Privacy.razor"));
        // ページの題名（h1）の次の見出しは h2（見た目は .policy-heading で従来の大きさを保つ）。h5 から始めない
        Assert.Contains("<h1 class=\"h3\">", privacy);
        Assert.DoesNotContain("<h5", privacy);
        Assert.Contains("<h2 class=\"policy-heading\">", privacy);
        var css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.Contains(".policy-card .policy-heading", css);
        // 問い合わせへのリンクは、どの URL の下でも同じ場所を指す絶対パス
        Assert.Contains("<a href=\"/report\">", privacy);
        Assert.DoesNotContain("href=\"report\"", privacy);
    }

    [Fact]
    public void Footer_links_to_the_contact_page_under_the_same_route()
    {
        // フッターは kit の SiteFooter（SiteOptions.ReportPath の既定 "report"・PrivacyPath の既定 "privacy"。このサイトは変えない）
        var footer = Read(Path.Combine(Kit, "Components", "SiteFooter.razor"));
        Assert.Contains("問い合わせ</a>", footer);
        Assert.Equal("report", new SiteOptions().ReportPath);
        Assert.Equal("privacy", new SiteOptions().PrivacyPath);
        var options = new SiteOptions();
        SiteInfo.Configure(options);
        Assert.Equal("report", options.ReportPath);
        Assert.Equal("privacy", options.PrivacyPath);
        var report = Read(Path.Combine(Web, "Components", "Pages", "Report.razor"));
        Assert.Contains("@page \"/report\"", report);                          // ルートは維持
        // 画面の本体は kit の ReportFormView（題名の既定は「問い合わせ」。ページの題名は「題名 | サイト名」）
        Assert.Contains("<ReportFormView From=\"@From\"", report);
        var view = Read(Path.Combine(Kit, "Components", "ReportFormView.razor"));
        Assert.Contains("[Parameter] public string Title { get; set; } = \"問い合わせ\";", view);
        Assert.Contains("<PageTitle>@Title | @Site.Name</PageTitle>", view);
        Assert.Contains("@TitleHeading", view);                       // kit v0.5.1: 題名の見出しは 1 か所（既定はページの h1。HeadingLevel で変えられる）
        Assert.Contains("\"fbc-page-title\"", view);
    }

    [Fact]
    public void Footer_social_icons_open_in_a_new_tab_safely_with_a_label()
    {
        // フッターの SNS は kit の SocialLinks（SiteOptions.Social の URL が https:// のものだけを描く）
        var links = Read(Path.Combine(Kit, "Components", "SocialLinks.razor"));
        Assert.Contains("target=\"_blank\"", links);
        Assert.Contains("rel=\"noopener noreferrer\"", links);
        Assert.Contains("aria-label=\"@service.Label\"", links);
        Assert.Contains("<SocialIcon Name=\"@social.Service\"", links);
        Assert.Contains("Site.VisibleSocial", links);
        Assert.All(SiteInfo.Social, social => Assert.True(SocialLinks.IsSafeUrl(social.Url), $"https の URL ではない: {social.Url}"));
    }

    [Fact]
    public void Social_icon_color_wins_over_the_generic_footer_link_rules()
    {
        // フッターの SNS のアイコン。.fbc-footer a の規則（文字色 inherit・下線・hover の文字色）より後ろに書いて、白い図形を保つ
        var css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.Contains("a.fbc-footer-social {", css);
        Assert.Contains("a.fbc-footer-social:hover {", css);
        Assert.Contains("a.fbc-footer-social:focus-visible {", css);
        // 当たり判定は 44px（見た目の 22px は変えない）
        Assert.Contains("a.fbc-footer-social::after {", css);
        Assert.Contains("width: 44px; height: 44px;", RuleBodyAt(css, "a.fbc-footer-social::after {"));
        Assert.True(css.IndexOf(".fbc-footer a {", StringComparison.Ordinal) < css.IndexOf("a.fbc-footer-social {", StringComparison.Ordinal),
            "a.fbc-footer-social は .fbc-footer a より後ろに書く（同じ詳細度のため）");
        var baseRule = css[css.IndexOf("a.fbc-footer-social {", StringComparison.Ordinal)..];
        Assert.Contains("color: #fff;", baseRule[..baseRule.IndexOf('}')]);
        var hoverRule = css[css.IndexOf("a.fbc-footer-social:hover {", StringComparison.Ordinal)..];
        Assert.Contains("color: #fff;", hoverRule[..hoverRule.IndexOf('}')]);
    }

    [Fact]
    public void Contact_privacy_and_copyright_pages_use_the_centered_column()
    {
        // 「作者」「ほかの作品」の項目は削除済み（Twitter のリンクも、その中にあった）。
        // 内容の列（見出し・カード）は page-column で、横の中央に置く（問い合わせ・プライバシー・権利表記の 3 ページ。ほかのページには効かせない）
        var report = Read(Path.Combine(Web, "Components", "Pages", "Report.razor"));
        Assert.DoesNotContain("ほかの作品", report);
        Assert.DoesNotContain("非公式", report);
        Assert.DoesNotContain("service-twitter", report);
        var view = Read(Path.Combine(Kit, "Components", "ReportFormView.razor"));
        Assert.Contains("<div class=\"fbc-page-header fbc-page-column\">", view);
        Assert.Contains("<div class=\"fbc-pane-scroll fbc-page-pane\">", view);
        Assert.Contains("fbc-report-card fbc-page-card", view);
        Assert.DoesNotContain("service-twitter", view);
        var privacy = Read(Path.Combine(Web, "Components", "Pages", "Privacy.razor"));
        // 長文のページ（プライバシー・権利表記）は、PC で横幅を広げる（page-column--wide・policy-card--wide）
        Assert.Contains("<div class=\"page-header page-column page-column--wide\">", privacy);
        Assert.Contains("<div class=\"pane-scroll page-pane\">", privacy);
        Assert.Contains("policy-card policy-card--wide page-card", privacy);
        var copyright = Read(Path.Combine(Web, "Components", "Pages", "Copyright.razor"));
        Assert.Contains("<div class=\"page-header page-column page-column--wide\">", copyright);
        Assert.Contains("<div class=\"pane-scroll page-pane\">", copyright);
        Assert.Contains("policy-card policy-card--wide page-card", copyright);
        var css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.Contains("margin-left: auto; margin-right: auto;", RuleBodyAt(css, ".page-column {"));
        Assert.Contains("scrollbar-gutter: stable both-edges;", RuleBodyAt(css, ".page-pane {"));   // 枠の中心と見出しの中心をそろえる（左右の余白が同じ）
    }

    [Fact]
    public void Unknown_urls_show_the_shared_not_found_view_with_noindex()
    {
        // 存在しない URL は、ポートフォリオの 404 と同じ形（Shared/NotFoundView.razor）。検索に載せないよう noindex を付ける
        var app = Read(Path.Combine(Web, "App.razor"));
        Assert.Contains("<NotFoundView HomeHref=\"/\" />", app);
        Assert.DoesNotContain("Sorry, there's nothing at this address.", app);
        // 部品は kit の NotFoundView（このサイトの NotFoundView は持たない）
        Assert.False(File.Exists(Path.Combine(Web, "Shared", "NotFoundView.razor")));
        var view = Read(Path.Combine(Kit, "Components", "NotFoundView.razor"));
        Assert.Contains("<meta name=\"robots\" content=\"noindex\" />", view);
        Assert.Contains("[Parameter] public bool NoIndex { get; set; } = true;", view);
        Assert.Contains("ページが見つかりません", view);
        Assert.Contains("URL が間違っているか、ページが移動した可能性があります。", view);
        Assert.Contains("ホームへ戻る", view);
        Assert.Contains("class=\"fbc-not-found-code\"", view);
    }

    [Fact]
    public void Maintenance_screen_is_the_kit_screen_with_the_twitter_contact()
    {
        // メンテナンス中（MAINTENANCE_MODE=true）は、アプリの代わりにこの画面だけを動かす（Program.cs）。画面は kit の MaintenanceScreen、連絡先だけがこのサイトのもの
        Assert.False(File.Exists(Path.Combine(Web, "MaintenanceScreen.razor")));
        var page = Read(Path.Combine(Web, "MaintenancePage.razor"));
        Assert.Contains("<MaintenanceScreen Info=\"Info\">", page);
        Assert.Contains("お知らせは Twitter で", page);
        Assert.Contains("class=\"fbc-maintenance-link\" href=\"https://twitter.com/game_fara\"", page);
        Assert.Contains("@@game_fara（Twitter）", page);
        var program = Read(Path.Combine(Web, "Program.cs"));
        Assert.Contains("builder.RootComponents.Add<MaintenancePage>(\"#app\");", program);
        // SiteOptions（kit の画面が読む）は、メンテナンスの分岐より前に登録する
        Assert.True(program.IndexOf("AddFaraBlazorComponent(", StringComparison.Ordinal) < program.IndexOf("MaintenanceState.On", StringComparison.Ordinal));
        // 画面の見た目は kit の CSS（.fbc-maintenance）。このサイトの CSS には、旧い .maintenance-screen を残さない
        var css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));
        Assert.DoesNotContain(".maintenance-screen", css);
        Assert.DoesNotContain(".maintenance-twitter", css);
    }

    [Fact]
    public void Side_menu_has_the_copyright_link_between_contact_and_privacy_order()
    {
        // 権利表記のページは、画面内のリンクがここにしか無い（フッターには無い）。問い合わせ・プライバシーと同じ並び
        var nav = Read(Path.Combine(Web, "Shared", "NavMenu.razor"));
        var report = nav.IndexOf("(\"report\", \"問い合わせ\"", StringComparison.Ordinal);
        var privacy = nav.IndexOf("(\"privacy\", \"プライバシーポリシー\"", StringComparison.Ordinal);
        var copyright = nav.IndexOf("(\"copyright\", \"権利表記\", Icons.Material.Filled.Copyright", StringComparison.Ordinal);
        Assert.True(report >= 0 && privacy > report && copyright > privacy, "サイドメニューに権利表記が無い、または並びが違う");
    }

    [Fact]
    public void Social_links_match_the_portfolio_urls_and_brand_colors()
    {
        // ポートフォリオ（fara-labs.com）の wwwroot/data/profile.json と、Components/Icon.razor・app.css の .service-* に合わせる
        // URL はこのサイト（SiteInfo.Social）、名前と色は kit の SocialServices
        var expected = new[]
        {
            ("github", "GitHub", "https://github.com/fara1991", "#6e40c9"),
            ("twitter", "Twitter", "https://twitter.com/game_fara", "#1d9bf0"),
            ("twitch", "Twitch", "https://twitch.tv/game_fara", "#9146ff"),
            ("discord", "Discord", "https://discord.com/invite/B38eCSYyk5", "#5865f2"),
        };
        var options = new SiteOptions();
        SiteInfo.Configure(options);
        Assert.Equal(expected, options.VisibleSocial.Select(s => (s.Service, SocialServices.Find(s.Service).Label, s.Url, SocialServices.Find(s.Service).Color)));
    }

    [Fact]
    public void Social_icons_are_the_portfolio_brand_paths()
    {
        var icon = Read(Path.Combine(Kit, "Components", "SocialIcon.razor"));
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
        var footer = Read(Path.Combine(Kit, "Components", "SiteFooter.razor"));
        Assert.DoesNotContain("非公式", footer);
        Assert.DoesNotContain("各権利者", footer);
        Assert.DoesNotContain("footer-note", footer);
        Assert.DoesNotContain("footer-note", Read(Path.Combine(Web, "wwwroot", "css", "app.css")));   // 未使用の CSS も残さない
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
    public void Footer_hides_the_separators_on_narrow_screens_and_spaces_the_items_with_gap()
    {
        var css = Read(Path.Combine(Web, "wwwroot", "css", "app.css"));
        var start = css.IndexOf("@media (max-width: 767.98px) {\n    .fbc-footer-line", StringComparison.Ordinal);
        Assert.True(start >= 0, "狭い画面のフッターの規則が無い");
        var block = css[start..css.IndexOf("\n}\n", start, StringComparison.Ordinal)];
        Assert.Contains(".fbc-footer-sep { display: none; }", block);            // 折り返しても、行頭・行末に | が出ない
        Assert.Contains(".fbc-footer-line > * { white-space: nowrap; }", block);   // 「プライバシーポリシー」などを途中で改行しない（折り返しは項目の間だけ）
        Assert.Contains("column-gap: 0.9rem;", block);                          // 行間は kit の 22px（row-gap）に任せ、項目の間だけ空ける
        Assert.Contains(".fbc-footer-line { display: flex; flex-wrap: wrap; justify-content: center;", css);   // PC の区切りは残す（規則は変えていない）
        Assert.DoesNotContain(".fbc-footer-sep { display: none; }", css[..start]);
    }

    private static string RuleBodyAt(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"CSS の規則が無い: {selector}");
        return css[start..css.IndexOf('}', start)];
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
