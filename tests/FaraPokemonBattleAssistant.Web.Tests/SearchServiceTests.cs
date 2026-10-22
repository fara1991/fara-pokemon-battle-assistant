using System.Text.Json;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>サイト内検索（Services/SearchService.cs）の、探し方・強調・抜き出しと、検索の索引（wwwroot/search-index.json）。</summary>
public class SearchServiceTests
{
    [Theory]
    [InlineData("ＡＢＣ　ｄｅｆ", "abc def")]            // 全角の英数字・スペースを半角に、大文字を小文字に
    [InlineData("ガブリアス", "がぶりあす")]              // カタカナをひらがなに（どちらで打っても探せる）
    [InlineData("ダメージ計算", "だめーじ計算")]
    [InlineData("0～31", "0〜31")]                          // Windows の IME の ～（全角）も、データの 〜 と同じに探せる
    public void Fold_makes_width_kana_and_case_insensitive(string input, string expected) =>
        Assert.Equal(expected, SearchService.Fold(input));

    [Fact]
    public void Fold_keeps_the_length_so_positions_map_back_to_the_original_text()
    {
        const string text = "ＨＰ ガブリアス（ﾒｶﾞ）";
        Assert.Equal(text.Length, SearchService.Fold(text).Length);
    }

    [Fact]
    public void Terms_splits_on_spaces_and_drops_duplicates()
    {
        Assert.Equal(new[] { "dmg", "がぶりあす" }, SearchService.Terms("  DMG　ガブリアス dmg "));
        Assert.Empty(SearchService.Terms("   "));
        Assert.Empty(SearchService.Terms(null));
    }

    [Fact]
    public void Split_marks_every_match_in_the_original_text()
    {
        var parts = SearchService.Split("メガガブリアスZ", SearchService.Terms("がぶりあす")).ToList();
        Assert.Equal(new[] { "メガ", "ガブリアス", "Z" }, parts.Select(p => p.Text));
        Assert.Equal(new[] { false, true, false }, parts.Select(p => p.Match));
        // 一致しないとき・語が無いときは、全部そのまま
        Assert.Equal(new[] { ("abc", false) }, SearchService.Split("abc", SearchService.Terms("x")).ToArray());
        Assert.Equal(new[] { ("abc", false) }, SearchService.Split("abc", Array.Empty<string>()).ToArray());
        // 2 つの語が重なっても、1 つの強調にまとまる
        Assert.Equal(new[] { ("a", false), ("bcd", true), ("e", false) }, SearchService.Split("abcde", SearchService.Terms("bc cd")).ToArray());
    }

    [Fact]
    public void Snippet_cuts_around_the_first_match_with_ellipses()
    {
        var text = new string('あ', 100) + "努力値" + new string('い', 100);
        var snippet = SearchService.Snippet(text, SearchService.Fold(text), SearchService.Terms("努力値"), 60);
        Assert.Contains("努力値", snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.True(snippet.Length <= 62);
        // 一致が無ければ先頭から。短い文章はそのまま
        Assert.Equal("短い文章", SearchService.Snippet("短い文章", SearchService.Fold("短い文章"), SearchService.Terms("x"), 60));
        Assert.Equal("", SearchService.Snippet("", "", SearchService.Terms("x"), 60));
    }

    [Fact]
    public void The_search_index_is_well_formed()
    {
        var path = Path.Combine(RepoRoot(), "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "search-index.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        var types = new[] { "page", "section", "term", "command" };
        foreach (var row in rows)
        {
            Assert.Contains(row.GetProperty("type").GetString(), types);
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("page").GetString()));
            Assert.StartsWith("/", row.GetProperty("url").GetString());
            Assert.NotNull(row.GetProperty("text").GetString());
        }
        // 主なページが入っている（ページの追加・削除で索引を作り直し忘れていないかの目安。CI の `--check` が厳密に確かめる）
        var pages = rows.Where(r => r.GetProperty("type").GetString() == "page").Select(r => r.GetProperty("url").GetString()).ToHashSet();
        Assert.Contains("/damage-calculator", pages);
        Assert.Contains("/chat-commands", pages);
        Assert.Contains("/battle-terms", pages);
        // チャットコマンドの一覧が、取りこぼさず入っている（説明が条件式の add -m や、埋め込みのある faster も）
        var commands = rows.Where(r => r.GetProperty("type").GetString() == "command").Select(r => r.GetProperty("title").GetString()!).ToList();
        Assert.True(commands.Count >= 25, $"コマンドが {commands.Count} 件しかありません");
        Assert.Contains(commands, c => c.Contains(" add -m "));
        Assert.Contains(commands, c => c.Contains(" faster "));
        // 検索結果に出る文字に、C# の断片や、中身のない括弧が混ざっていない
        var leak = new System.Text.RegularExpressions.Regex(@"\b(var|if|else|new|foreach|case)\b\s|\bbreak;|=>|=""|[（(]\s*[)）]");
        foreach (var row in rows)
            Assert.False(leak.IsMatch(row.GetProperty("title").GetString()! + " " + row.GetProperty("text").GetString()), row.GetProperty("title").GetString());
        // ページには読み（ひらがな）が付いている（権利表記 → けんりひょうき。ローマ字で探すため）
        foreach (var row in rows.Where(r => r.GetProperty("type").GetString() == "page"))
            Assert.Matches(@"^[ぁ-ゖー]+$", row.GetProperty("reading").GetString()!);
    }

    /// <summary>実際の索引（search-index.json）を、読み込んだ行にする。</summary>
    private static List<SearchIndexRow> RealIndex()
    {
        var path = Path.Combine(RepoRoot(), "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "search-index.json");
        return JsonSerializer.Deserialize<List<SearchIndexRow>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Theory]
    [InlineData("kenri", "権利表記")]                 // ローマ字（読み「けんりひょうき」の前半）
    [InlineData("kenrihyouki", "権利表記")]           // 読みの全部（ヘボン式）
    [InlineData("kenrihyoki", "権利表記")]            // 長音の「う」を書かない打ち方
    [InlineData("kennri", "権利表記")]                // 「nn」（ん）を 2 つ打つ
    [InlineData("けんり", "権利表記")]                 // かなで打っても探せる
    [InlineData("ケンリ", "権利表記")]                 // カタカナでも
    [InlineData("toiawase", "問い合わせ")]
    [InlineData("puraibasi-", "プライバシーポリシー")]  // 長音の「ー」
    [InlineData("homu", "ホーム")]                     // 読み「ほーむ」（長音の ー を無視）
    [InlineData("yougo", "用語・略語")]
    [InlineData("damejikeisanki", "ダメージ計算機")]
    public void Romaji_and_kana_readings_find_the_page(string query, string expectedTitle)
    {
        var top = SearchService.RankIndex(RealIndex(), query).FirstOrDefault();
        Assert.NotNull(top);
        Assert.Equal(("page", expectedTitle), (top.Type, top.Title));
    }

    [Fact]
    public void Romaji_that_is_not_a_reading_finds_no_page_by_reading()
    {
        // dmg はローマ字として成り立たないので、読みでは探さない（ページの文章にも無い）
        Assert.DoesNotContain(SearchService.RankIndex(RealIndex(), "dmg"), m => m.Type == "page");
        Assert.Empty(SearchService.RankIndex(RealIndex(), "   "));
        Assert.Empty(SearchService.RankIndex(RealIndex(), null));
    }

    [Theory]
    [InlineData("あい", "/api-docs")]                  // 2 文字は、読みの途中に現れても当てない（「えーぴーあい」の末尾）
    [InlineData("さん", "/damage-calculator")]         // 「けいさん」の途中
    [InlineData("しょう", "/type-chart")]              // 「たいぷあいしょうひょう」の途中
    [InlineData("きょう", "/data-share")]             // 「でーたきょうゆう」の途中
    public void Two_kana_do_not_match_inside_a_reading(string query, string url) =>
        Assert.DoesNotContain(SearchService.RankIndex(RealIndex(), query), m => m.Type == "page" && m.Url == url);

    [Fact]
    public void Two_kana_match_the_start_of_a_reading()
    {
        // 2 文字でも、読みの先頭（前方一致）には当てる（「いく」→ 育成ポケモン）
        Assert.Contains(SearchService.RankIndex(RealIndex(), "いく"), m => m.Type == "page" && m.Title == "育成ポケモン");
    }

    [Theory]
    [InlineData("ひょうき", "ひょき")]                 // 長音の書き方の揺れ（う を書く・書かない）
    [InlineData("ほーむ", "ほむ")]                     // 長音記号 ー は無視
    [InlineData("ほうき", "ほき")]
    [InlineData("おおきい", "おきい")]                 // 「おお」は長音として 1 つにする。きい の い は残す
    [InlineData("とうきょう", "ときょ")]               // 「う」は、お段（と）・ょ の後ならどちらも読まない
    public void LooseKana_treats_long_vowel_spellings_alike(string kana, string expected) =>
        Assert.Equal(expected, SearchService.LooseKana(kana));

    [Theory]
    [InlineData("ていき")]                             // え段＋い は二重母音（定期）。長音（せいじん）と区別できないので、そろえない
    [InlineData("けいさん")]                           // 計算 を「けさん」にしない
    [InlineData("せいじん")]
    [InlineData("うお")]                               // 魚。う の後の お は長音ではない
    public void LooseKana_keeps_words_that_only_look_like_long_vowels(string kana) =>
        Assert.Equal(kana, SearchService.LooseKana(kana));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonBattleAssistant.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("リポジトリのルートが見つかりません");
    }

    [Theory]
    [InlineData("adoon", "あどおん")]
    [InlineData("addon", "あっどん")]
    [InlineData("gaburiasu", "がぶりあす")]
    [InlineData("kyou", "きょう")]
    [InlineData("konna", "こんな")]
    [InlineData("kanji", "かんじ")]
    [InlineData("shinkansen", "しんかんせん")]
    [InlineData("taima-", "たいまー")]
    [InlineData("chotto", "ちょっと")]
    [InlineData("ADOON", "あどおん")]
    [InlineData("kennri", "けんり")]                   // 「nn」は「ん」1 つ（けんんり にしない）
    [InlineData("konnichiha", "こんにちは")]
    [InlineData("konnnichiha", "こんにちは")]          // 3 つの n は「ん」（nn）＋「に」（n）。konnichiha と同じ
    [InlineData("kanni", "かんに")]                    // 「nn＋母音」は「ん」1 つで、2 つ目の n は次の音（に）になる（konna → こんな と同じ規則）
    [InlineData("kan'ni", "かんに")]                   // ' で区切っても同じ結果（ん＋に を明示したいときの書き方）
    [InlineData("n'", "ん")]
    [InlineData("toiawase", "といあわせ")]
    [InlineData("puraibasi-", "ぷらいばしー")]
    public void Romaji_converts_to_hiragana(string input, string expected) =>
        Assert.Equal(expected, Romaji.ToHiragana(input));

    [Theory]
    [InlineData("dmg")]        // 母音がなく、ローマ字として成り立たない
    [InlineData("speed")]
    [InlineData("hp252")]      // 数字が入る
    [InlineData("")]
    public void Romaji_returns_null_when_not_convertible(string input) =>
        Assert.Null(Romaji.ToHiragana(input));

    [Fact]
    public void Alternatives_add_the_hiragana_form_only_for_romaji_like_terms()
    {
        Assert.Equal(new[] { "adoon", "あどおん" }, SearchService.Alternatives("adoon"));
        Assert.Equal(new[] { "kenri", "けんり" }, SearchService.Alternatives("kenri"));
        Assert.Equal(new[] { "dmg" }, SearchService.Alternatives("dmg"));
        Assert.Equal(new[] { "ka" }, SearchService.Alternatives("ka"));              // 2 文字以下は、ひらがなにしない（a・ka などが、ほぼ全部に一致してしまうため）
        Assert.Equal(new[] { "がぶりあす" }, SearchService.Alternatives("がぶりあす"));
    }

    [Fact]
    public void Split_highlights_the_text_for_a_romaji_term()
    {
        var parts = SearchService.Split("メガガブリアスZ", SearchService.Terms("gaburiasu")).ToList();
        Assert.Equal(new[] { "メガ", "ガブリアス", "Z" }, parts.Select(p => p.Text));
        Assert.Equal(new[] { false, true, false }, parts.Select(p => p.Match));
    }

    /// <summary>
    /// 実際の索引に、Champions のポケモン・わざ・特性の名前を足す（検索の対象の名前。点数は SearchService と同じ判定。
    /// 実行時は、ポケモン・わざ・特性に少し加点するので、同点のときの並びだけ違うことがある）。
    /// </summary>
    private static async Task<List<SearchIndexRow>> RealIndexWithChampionsNames()
    {
        var rows = RealIndex();
        var root = Path.Combine(RepoRoot(), "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "api", "game");
        var data = await new DataCatalog(new FileDataSource(root)).GetDataSetAsync("Champions");
        rows.AddRange(data.Pokemon.Select(p => new SearchIndexRow("pokemon", p.Name, "ポケモン一覧", "pokemon-list", "")));
        rows.AddRange(data.Moves.Select(m => new SearchIndexRow("move", m.Name, "わざ一覧", "move-list", "")));
        rows.AddRange(data.Abilities.Where(a => a.Name.Length > 0).Select(a => new SearchIndexRow("ability", a.Name, "特性", "pokemon-list", "")));
        return rows;
    }

    [Theory]
    [InlineData("gaburiasu", "pokemon", "ガブリアス")]    // ローマ字でポケモン名（ガブリアス が先頭）
    [InlineData("ガブリアス", "pokemon", "ガブリアス")]
    [InlineData("がぶりあす", "pokemon", "ガブリアス")]
    [InlineData("じしん", "move", "じしん")]              // わざ（名前の完全一致）
    [InlineData("jishin", "move", "じしん")]
    [InlineData("いかく", "ability", "いかく")]           // 特性
    [InlineData("kenri", "page", "権利表記")]             // 既存の確認済みクエリ（ページ）
    [InlineData("toiawase", "page", "問い合わせ")]
    [InlineData("homu", "page", "ホーム")]
    [InlineData("yougo", "page", "用語・略語")]
    [InlineData("damejikeisanki", "page", "ダメージ計算機")]
    public async Task The_top_hit_for_known_queries_stays_put(string query, string type, string title)
    {
        var top = SearchService.RankIndex(await RealIndexWithChampionsNames(), query).FirstOrDefault();
        Assert.NotNull(top);
        Assert.Equal((type, title), (top.Type, top.Title));
    }
}
