using System.Text.Json;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>メンテナンスの状態（通常・お知らせ・メンテナンス中）。</summary>
public enum MaintenanceState
{
    Off,
    /// <summary>通常のページの上に「更新予定」のお知らせを出す。</summary>
    Notice,
    /// <summary>メンテナンス画面だけを出す。</summary>
    On,
}

/// <summary>
/// Cloudflare の Worker の変数 MAINTENANCE / MAINTENANCE_MESSAGE（worker/maintenance.js）の状態。
/// アプリの起動時に GET /api/maintenance で 1 回だけ読む。読めなかったとき（通信できない・Worker が無い開発中など）は通常として扱う。
/// </summary>
public sealed record MaintenanceInfo(MaintenanceState State, string Message)
{
    public static readonly MaintenanceInfo Off = new(MaintenanceState.Off, "");

    /// <summary>お知らせに出す文（空なら既定の文）。</summary>
    public string NoticeText => string.IsNullOrWhiteSpace(Message) ? "まもなくメンテナンスを行います。" : Message;

    /// <summary>応答の JSON（{"state": "off" | "notice" | "on", "message": "..."}）を読む。読めなければ通常。</summary>
    public static MaintenanceInfo Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Off;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Off;
            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()?.Trim().ToLowerInvariant() switch
                {
                    "on" => MaintenanceState.On,
                    "notice" => MaintenanceState.Notice,
                    _ => MaintenanceState.Off,
                }
                : MaintenanceState.Off;
            var message = state != MaintenanceState.Off && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? (m.GetString() ?? "").Trim()
                : "";
            return new MaintenanceInfo(state, message);
        }
        catch (JsonException)
        {
            // 開発中などで、Worker が無く、index.html が返ってきたとき
            return Off;
        }
    }

    /// <summary>アプリの起動時に状態を読む。時間がかかる・失敗するときは、通常として扱う（メンテナンスの確認のために、起動を待たせない）。</summary>
    public static async Task<MaintenanceInfo> LoadAsync(HttpClient http, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync("api/maintenance", cts.Token);
            return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(cts.Token)) : Off;
        }
        catch (Exception)
        {
            // 通信の失敗・時間切れ・想定外の例外のどれでも、起動を止めずに通常として扱う
            return Off;
        }
    }
}
