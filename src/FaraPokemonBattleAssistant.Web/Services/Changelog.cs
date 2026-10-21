using System.Globalization;
using System.Text.Json;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>更新履歴の 1 件（日付・短い見出し・細かい内容）。</summary>
public sealed record ChangelogEntry(DateOnly Date, string Title, IReadOnlyList<string> Items);

/// <summary>更新履歴（wwwroot/changelog.json）の読み込み。ホームは新しい数件の見出しだけ、更新履歴ページは細かい内容まで出す。</summary>
public sealed class Changelog
{
    private readonly HttpClient http;
    private IReadOnlyList<ChangelogEntry>? cache;

    public Changelog(HttpClient http) => this.http = http;

    /// <summary>JSON（{ "entries": [ { "date": "2026-10-22", "title": "...", "items": ["..."] } ] }）を、新しい順の一覧にする。形が違えば FormatException。</summary>
    public static IReadOnlyList<ChangelogEntry> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new FormatException("entries の配列がありません");
            var list = new List<ChangelogEntry>();
            foreach (var e in entries.EnumerateArray())
            {
                var dateText = e.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new FormatException($"date が yyyy-MM-dd ではありません: {dateText}");
                var title = e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(title)) throw new FormatException($"{dateText} の title が空です");
                var items = new List<string>();
                if (e.TryGetProperty("items", out var arr))
                {
                    if (arr.ValueKind != JsonValueKind.Array) throw new FormatException($"{dateText} の items が配列ではありません");
                    foreach (var it in arr.EnumerateArray())
                    {
                        var s = it.ValueKind == JsonValueKind.String ? it.GetString()?.Trim() : null;
                        if (string.IsNullOrEmpty(s)) throw new FormatException($"{dateText} の items に空の項目があります");
                        items.Add(s);
                    }
                }
                list.Add(new ChangelogEntry(date, title, items));
            }
            // 同じ日付は、書いた順のまま（OrderByDescending は安定）
            return list.OrderByDescending(x => x.Date).ToList();
        }
        catch (JsonException ex)
        {
            throw new FormatException("JSON として読めません", ex);
        }
    }

    /// <summary>更新履歴を読む（1 回だけ。失敗したら例外を投げ、次の呼び出しでもう一度読む）。</summary>
    public async Task<IReadOnlyList<ChangelogEntry>> LoadAsync()
    {
        if (cache is not null) return cache;
        // ファイルが無いと SPA のフォールバックで index.html が 200 で返るので、JSON として読めるかで確かめる
        var text = await http.GetStringAsync("changelog.json");
        return cache = Parse(text);
    }
}
