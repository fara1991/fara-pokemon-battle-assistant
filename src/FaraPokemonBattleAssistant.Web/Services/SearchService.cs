using System.Net.Http.Json;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>検索結果の 1 件。</summary>
/// <param name="Kind">page / section / term / command / pokemon / move / ability</param>
/// <param name="Title">見出し・名前</param>
/// <param name="Where">どこに一致したか（「ページ › 項目」など）</param>
/// <param name="Snippet">一致した部分の前後の文章</param>
/// <param name="Url">開く先</param>
/// <param name="Actions">ポケモンなど、別の開き方（ダメージ計算機の攻撃側に入れる、など）</param>
public sealed record SearchHit(string Kind, string Title, string Where, string Snippet, string Url, IReadOnlyList<SearchAction> Actions);

/// <summary>検索結果から開ける、別の行き先。</summary>
public sealed record SearchAction(string Label, string Url);

/// <summary>
/// サイト内検索。ページ・項目・用語・コマンドの索引（wwwroot/search-index.json。tools/gen-search-index.mjs が各ページから作る）と、
/// いま選んでいるゲームのポケモン・わざ・特性のデータを、1 つの検索欄で探す。
/// 日本語は単語区切りがないので、スペースで区切った語が「すべて含まれる」ものを一致とする。全角・半角、ひらがな・カタカナ、大文字・小文字は区別しない。
/// </summary>
public sealed class SearchService
{
    /// <summary>一度に出す件数。</summary>
    public const int MaxHits = 12;

    private readonly HttpClient _http;
    private readonly DataCatalog _catalog;
    private readonly AppState _app;
    private Task<List<Entry>>? _static;
    private (string Key, List<Entry> Entries)? _data;

    public SearchService(HttpClient http, DataCatalog catalog, AppState app)
    {
        _http = http;
        _catalog = catalog;
        _app = app;
    }

    /// <summary>検索の対象 1 件（探すための文字は、あらかじめ折りたたんで持つ）。</summary>
    private sealed class Entry
    {
        public string Kind = "";
        public string Title = "";
        public string Page = "";
        public string Url = "";
        public string Text = "";
        public IReadOnlyList<SearchAction> Actions = Array.Empty<SearchAction>();
        public double Bonus;
        public string FoldedTitle = "";
        public string FoldedPage = "";
        public string FoldedText = "";
    }

    private sealed record IndexRow(string Type, string Title, string Page, string Url, string Text);

    /// <summary>検索語を、探すための形にそろえる（全角→半角、カタカナ→ひらがな、小文字、空白の正規化。文字数は変えない）。</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);                       // 全角の英数字・記号 → 半角
            else if (c == '　') c = ' ';                                              // 全角スペース
            else if (c >= 'ァ' && c <= 'ヶ') c = (char)(c - 0x60);                    // カタカナ → ひらがな
            if (c == '~') c = '〜';                                                   // 半角のチルダ（全角の ～ を半角にしたもの）→ データの 〜 にそろえる
            chars[i] = char.ToLowerInvariant(c);
        }
        return new string(chars);
    }

    /// <summary>検索語を、空白で区切った語にする（折りたたみ済み）。</summary>
    public static string[] Terms(string? query) =>
        Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    private static Entry MakeEntry(string kind, string title, string page, string url, string text, double bonus = 0, IReadOnlyList<SearchAction>? actions = null) => new()
    {
        Kind = kind, Title = title, Page = page, Url = url, Text = text, Bonus = bonus, Actions = actions ?? Array.Empty<SearchAction>(),
        FoldedTitle = Fold(title), FoldedPage = Fold(page), FoldedText = Fold(text),
    };

    private async Task<List<Entry>> LoadStaticAsync()
    {
        var rows = await _http.GetFromJsonAsync<List<IndexRow>>("search-index.json") ?? new();
        return rows.Select(r => MakeEntry(r.Type, r.Title, r.Page, r.Url, r.Text, r.Type == "page" ? 0.5 : 0)).ToList();
    }

    /// <summary>いま選んでいるゲームのポケモン・わざ・特性（ゲームを切り替えたら作り直す）。</summary>
    private async Task<List<Entry>> LoadDataAsync()
    {
        await _app.InitializeAsync();
        var key = _app.DataSetKey;
        if (_data is { } cached && cached.Key == key) return cached.Entries;
        var data = await _catalog.GetDataSetAsync(key);
        var calc = data.Info.CalcSupported;
        var game = data.Info.Name;
        var entries = new List<Entry>();
        foreach (var p in data.Pokemon)
        {
            var abilities = string.Join("・", data.AbilitiesOf(p).Select(a => a.Name));
            var types = string.Join("・", p.Types);
            var s = p.BaseStats;
            var text = $"{types} / 種族値 H{s.HP} A{s.Attack} B{s.Defense} C{s.SpAttack} D{s.SpDefense} S{s.Speed}" + (abilities.Length > 0 ? $" / 特性: {abilities}" : "");
            var actions = new List<SearchAction>();
            if (calc)
            {
                actions.Add(new("ダメージ計算（攻撃側）", $"damage-calculator?attacker={p.Id}"));
                actions.Add(new("ダメージ計算（防御側）", $"damage-calculator?defender={p.Id}"));
                actions.Add(new("素早さ比較", $"speed-comparison?p1={p.Id}"));
            }
            actions.Add(new("タイプ相性", $"type-chart?pokemon={p.Id}"));
            entries.Add(MakeEntry("pokemon", p.Name, $"ポケモン一覧（{game}）", "pokemon-list", text, 0.4, actions));
        }
        foreach (var m in data.Moves)
        {
            var cat = m.Category switch { MoveCategory.Physical => "物理", MoveCategory.Special => "特殊", _ => "変化" };
            var power = m.Power > 0 ? $" / 威力 {m.Power}" : "";
            var text = $"{m.Type} / {cat}{power} / 命中 {(m.Accuracy > 0 ? m.Accuracy.ToString() : "-")} / PP {m.PP}" + (m.Description.Length > 0 ? $" / {m.Description}" : "");
            entries.Add(MakeEntry("move", m.Name, $"わざ一覧（{game}）", "move-list", text, 0.2));
        }
        foreach (var a in data.Abilities.Where(a => a.Name.Length > 0))
            entries.Add(MakeEntry("ability", a.Name, $"特性（{game}）", "pokemon-list", a.Description, 0.1));
        _data = (key, entries);
        return entries;
    }

    /// <summary>検索する。静的な索引の読み込みやデータの読み込みに失敗したときは、読み込めたものだけで探す。</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string? query)
    {
        var terms = Terms(query);
        if (terms.Length == 0) return Array.Empty<SearchHit>();
        var entries = new List<Entry>();
        try { entries.AddRange(await (_static ??= LoadStaticAsync())); }
        catch { _static = null; }
        try { entries.AddRange(await LoadDataAsync()); }
        catch { /* データを読めないときは、ページの索引だけで探す */ }
        return Rank(entries, terms).Take(MaxHits).Select(e => ToHit(e, terms)).ToList();
    }

    private static IEnumerable<Entry> Rank(IEnumerable<Entry> entries, string[] terms)
    {
        var scored = new List<(Entry Entry, double Score)>();
        foreach (var e in entries)
        {
            double score = 0;
            foreach (var t in terms)
            {
                double s;
                if (e.FoldedTitle == t) s = 30;
                else if (e.FoldedTitle.StartsWith(t, StringComparison.Ordinal)) s = 15;
                else if (e.FoldedTitle.Contains(t, StringComparison.Ordinal)) s = 10;
                else if (e.FoldedPage.Contains(t, StringComparison.Ordinal)) s = 4;
                else if (e.FoldedText.Contains(t, StringComparison.Ordinal)) s = 1;
                else { score = 0; break; }
                score += s;
            }
            if (score > 0) scored.Add((e, score + e.Bonus));
        }
        return scored.OrderByDescending(x => x.Score).Select(x => x.Entry);
    }

    private static SearchHit ToHit(Entry e, string[] terms)
    {
        var where = e.Kind switch
        {
            "page" => "ページ",
            "pokemon" or "move" or "ability" => e.Page,
            _ => e.Page == e.Title ? e.Page : e.Page + " › " + e.Title,
        };
        return new SearchHit(e.Kind, e.Title, where, Snippet(e.Text, e.FoldedText, terms, 110), e.Url, e.Actions);
    }

    /// <summary>本文から、最初に一致した語のまわりを切り出す（一致が無ければ先頭から）。</summary>
    public static string Snippet(string text, string foldedText, string[] terms, int width)
    {
        if (text.Length == 0) return "";
        var pos = -1;
        foreach (var t in terms)
        {
            pos = foldedText.IndexOf(t, StringComparison.Ordinal);
            if (pos >= 0) break;
        }
        if (pos < 0) return text.Length > width ? text[..width] + "…" : text;
        var start = Math.Max(0, pos - width / 3);
        var end = Math.Min(text.Length, start + width);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }

    /// <summary>
    /// 文字を、一致した部分と、そうでない部分に分ける（表示側で、一致した部分を強調する）。折りたたみは文字数を変えないので、位置はそのまま元の文字に当てられる。
    /// </summary>
    public static IEnumerable<(string Text, bool Match)> Split(string text, string[] terms)
    {
        if (terms.Length == 0 || text.Length == 0) { yield return (text, false); yield break; }
        var folded = Fold(text);
        var marks = new bool[text.Length];
        foreach (var t in terms)
        {
            for (var i = folded.IndexOf(t, StringComparison.Ordinal); i >= 0; i = folded.IndexOf(t, i + Math.Max(1, t.Length), StringComparison.Ordinal))
                for (var k = i; k < i + t.Length && k < marks.Length; k++) marks[k] = true;
        }
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || marks[i] != marks[start])
            {
                yield return (text[start..i], marks[start]);
                start = i;
            }
        }
    }
}
