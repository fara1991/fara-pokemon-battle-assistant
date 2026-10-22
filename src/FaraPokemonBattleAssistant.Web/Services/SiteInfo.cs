using FaraSiteKit.Core;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// このサイトの名前・作者・保存のキー・SNS。共通部品（fara-site-kit の Razor。ヘッダー・フッター・報告フォームなど）が読む SiteOptions に、起動時に入れる（Program.cs）。
/// 3 サイト（pokemon・avatar・bot）で、フッターの形を同じにする。
/// </summary>
public static class SiteInfo
{
    public const string Name = "Pokémon Battle Assistant";
    /// <summary>サイトを公開した年（© の年）。</summary>
    public const int PublishedYear = 2026;
    public const string Author = "Fara";
    public const string PortfolioUrl = "https://fara-labs.com/";
    /// <summary>確認用サイトのホスト名（開いているホストがこれなら、fara-labs.com へのリンクを確認用に切り替える）。</summary>
    public const string DevelopHost = "pokemon-dev.fara-labs.com";

    /// <summary>設定（ゲーム・バトル形式・テーマ）を入れる localStorage のキー。index.html の theme-init.js の data-storage-key と同じ値。</summary>
    public const string StorageKey = "fara-pokemon-battle-assistant.settings";

    /// <summary>PC のサイドバーを隠しているか（"1" で隠す）の localStorage のキー。index.html の theme-init.js の data-sidebar-key と同じ値。</summary>
    public const string SidebarKey = "fara-pokemon-battle-assistant.sidebar-hidden";

    /// <summary>© の表示（例: © 2026 Fara）。</summary>
    public static string Copyright => $"© {PublishedYear} {Author}";

    /// <summary>
    /// フッターの SNS。URL は、ポートフォリオ（fara-labs.com）の wwwroot/data/profile.json に合わせている。
    /// 名前・色・アイコンは kit の SocialServices が持つ（ポートフォリオの Components/Icon.razor・app.css の .service-* と同じ）。
    /// Discord は、ポートフォリオに無いため、作者が指定した URL。
    /// </summary>
    public static readonly IReadOnlyList<SiteSocialLink> Social =
    [
        new("github", "https://github.com/fara1991"),
        new("twitter", "https://twitter.com/game_fara"),
        new("twitch", "https://twitch.tv/game_fara"),
        new("discord", "https://discord.com/invite/B38eCSYyk5"),
    ];

    /// <summary>共通部品の設定に、このサイトの値を入れる。</summary>
    public static void Configure(SiteOptions options)
    {
        options.Name = Name;
        options.Author = Author;
        options.PublishedYear = PublishedYear;
        options.AuthorHomeUrl = PortfolioUrl;
        options.HomeHref = "/";
        options.DevelopHost = DevelopHost;
        options.StorageKey = StorageKey;
        options.SidebarKey = SidebarKey;
        foreach (var social in Social) options.Social.Add(social);
    }
}
