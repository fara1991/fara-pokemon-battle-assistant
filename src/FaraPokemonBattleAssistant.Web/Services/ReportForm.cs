using System.Text.Json;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 「作者へ報告」の入力（Components/Pages/Report.razor）。送り先は同じサイトの POST /api/report（worker/report.js）。
/// 長さ・種類の制限は、サーバー側（worker/report.js）と同じにしておく（先にここで確かめて、無駄な通信をしない）。
/// </summary>
public sealed class ReportForm
{
    public const int MaxMessage = 2000;
    public const int MinMessage = 5;
    public const int MaxContact = 200;

    /// <summary>種類の ID（サーバーの CATEGORIES と同じ）と、画面に出す名前。</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> Categories = new[]
    {
        ("bug", "不具合"),
        ("request", "要望・提案"),
        ("other", "その他"),
    };

    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string Contact { get; set; } = "";
    /// <summary>人には見えない罠の欄。ボットが埋めたら、サーバーは何も送らずに成功を返す。</summary>
    public string Website { get; set; } = "";
    public string TurnstileToken { get; set; } = "";
    /// <summary>報告のきっかけになったページ（移動してくる前のページ。分からなければ空）。</summary>
    public string Page { get; set; } = "";

    /// <summary>入力の問題を、人が読む文で返す。問題がなければ null。</summary>
    public string? Validate()
    {
        if (!Categories.Any(c => c.Id == Category)) return "種類を選んでください";
        var message = Message.Trim();
        if (message.Length < MinMessage) return $"内容を {MinMessage} 文字以上で書いてください";
        if (message.Length > MaxMessage) return $"内容は {MaxMessage} 文字までにしてください（いま {message.Length} 文字）";
        if (Contact.Trim().Length > MaxContact) return $"連絡先は {MaxContact} 文字までにしてください";
        if (string.IsNullOrEmpty(TurnstileToken)) return "「ロボットではありません」の確認が終わっていません。表示された確認をしてから、もう一度押してください";
        return null;
    }

    /// <summary>サーバーへ送る JSON。</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        category = Category,
        message = Message.Trim(),
        contact = Contact.Trim(),
        page = Page,
        turnstileToken = TurnstileToken,
        website = Website,
    });

    /// <summary>?from= で渡されたページ（パス）を、送ってよい形にする。パスでなければ空（外部の URL・スクリプトなどは送らない）。</summary>
    public static string CleanPage(string? from)
    {
        var path = (from ?? "").Trim();
        if (path.Length == 0 || path.Length > 300) return "";
        // 同じサイトのパス（/ から始まり、英数字・- _ / . だけ）だけ
        if (path[0] != '/' || path.StartsWith("//")) return "";
        return path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.') ? path : "";
    }

    /// <summary>サーバーの応答から、画面に出す文を取り出す（{"message": "..."}。読めなければ状態コードに合わせた既定の文）。</summary>
    public static string DescribeFailure(int statusCode, string? responseBody)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                    return message.GetString()!;
            }
        }
        catch (JsonException)
        {
            // 読めない応答は、下の既定の文にする
        }
        return statusCode switch
        {
            413 => "内容が長すぎます。短くして、もう一度お試しください",
            429 => "短い間に何度も送信されました。少し待ってから、もう一度お試しください",
            503 => "いまは報告を受け付けていません。しばらくしてからお試しください",
            >= 500 => "送信に失敗しました。しばらくしてからもう一度お試しください",
            _ => "送信できませんでした。入力を確かめて、もう一度お試しください",
        };
    }
}
