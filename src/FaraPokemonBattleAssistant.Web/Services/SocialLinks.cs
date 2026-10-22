namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// フッターに出す SNS のリンク。GitHub・Twitter・Twitch の URL・表示名・サービス色は、ポートフォリオ（fara-labs.com）の
/// wwwroot/data/profile.json と Components/Icon.razor・app.css の .service-* に合わせている（片方を変えたら、もう片方も変える）。
/// Discord は、ポートフォリオに無いため、作者が指定した URL と Discord の公式の色。
/// </summary>
public static class SocialLinks
{
    public sealed record Social(string Service, string Label, string Url, string Color);

    public static readonly IReadOnlyList<Social> All =
    [
        new("github", "GitHub", "https://github.com/fara1991", "#6e40c9"),
        new("twitter", "Twitter", "https://twitter.com/game_fara", "#1d9bf0"),
        new("twitch", "Twitch", "https://twitch.tv/game_fara", "#9146ff"),
        new("discord", "Discord", "https://discord.com/invite/B38eCSYyk5", "#5865f2"),
    ];
}
