using FaraPokemonBattleAssistant.Core;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>フッターなどに出す、サイトの名前・公開年・作者。3 サイト（pokemon・avatar・bot）で、フッターの形を同じにする。</summary>
public static class SiteInfo
{
    public const string Name = "Pokémon Battle Assistant";
    /// <summary>サイトを公開した年（© の年）。</summary>
    public const int PublishedYear = 2026;
    public const string Author = "Fara";
    public const string PortfolioUrl = "https://fara-labs.com/";

    /// <summary>© の表示（例: © 2026 Fara）。</summary>
    public static string Copyright => $"© {PublishedYear} {Author}";

    /// <summary>作者のホームページ（確認用サイトで開いているときは、確認用の URL に置き換える）。</summary>
    public static string AuthorHomeUrl => SiteLinks.Localize(PortfolioUrl);
}
