namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>検索語のローマ字（gaburiasu など）を、ひらがなにする。サイト内検索で、ローマ字でも探せるようにするためのもの。</summary>
public static class Romaji
{
    private static readonly Dictionary<string, string> Table = BuildTable();

    private static Dictionary<string, string> BuildTable()
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string keys, string kana)
        {
            var k = keys.Split(' ');
            var v = kana.Split(' ');
            for (var i = 0; i < k.Length; i++) t[k[i]] = v[i];
        }
        Add("a i u e o", "あ い う え お");
        Add("ka ki ku ke ko", "か き く け こ");
        Add("sa si shi su se so", "さ し し す せ そ");
        Add("ta ti chi tu tsu te to", "た ち ち つ つ て と");
        Add("na ni nu ne no", "な に ぬ ね の");
        Add("ha hi hu fu he ho", "は ひ ふ ふ へ ほ");
        Add("ma mi mu me mo", "ま み む め も");
        Add("ya yu yo", "や ゆ よ");
        Add("ra ri ru re ro", "ら り る れ ろ");
        Add("wa wo", "わ を");
        Add("ga gi gu ge go", "が ぎ ぐ げ ご");
        Add("za zi ji zu ze zo", "ざ じ じ ず ぜ ぞ");
        Add("da di du de do", "だ ぢ づ で ど");
        Add("ba bi bu be bo", "ば び ぶ べ ぼ");
        Add("pa pi pu pe po", "ぱ ぴ ぷ ぺ ぽ");
        Add("kya kyu kyo", "きゃ きゅ きょ");
        Add("sha shu sho sya syu syo", "しゃ しゅ しょ しゃ しゅ しょ");
        Add("cha chu cho tya tyu tyo cya cyu cyo", "ちゃ ちゅ ちょ ちゃ ちゅ ちょ ちゃ ちゅ ちょ");
        Add("nya nyu nyo hya hyu hyo mya myu myo rya ryu ryo", "にゃ にゅ にょ ひゃ ひゅ ひょ みゃ みゅ みょ りゃ りゅ りょ");
        Add("gya gyu gyo bya byu byo pya pyu pyo dya dyu dyo", "ぎゃ ぎゅ ぎょ びゃ びゅ びょ ぴゃ ぴゅ ぴょ ぢゃ ぢゅ ぢょ");
        Add("ja ju jo jya jyu jyo zya zyu zyo", "じゃ じゅ じょ じゃ じゅ じょ じゃ じゅ じょ");
        Add("fa fi fe fo va vi vu ve vo", "ふぁ ふぃ ふぇ ふぉ ゔぁ ゔぃ ゔ ゔぇ ゔぉ");
        Add("wi we thi dhi", "うぃ うぇ てぃ でぃ");
        Add("xa xi xu xe xo la li lu le lo", "ぁ ぃ ぅ ぇ ぉ ぁ ぃ ぅ ぇ ぉ");
        Add("xya xyu xyo lya lyu lyo xtu xtsu ltu ltsu", "ゃ ゅ ょ ゃ ゅ ょ っ っ っ っ");
        return t;
    }

    /// <summary>ローマ字をひらがなにする。英字・「-」・「'」以外が入っているときや、変換できない文字が残るときは null。</summary>
    public static string? ToHiragana(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text.ToLowerInvariant();
        foreach (var c in s)
            if (!(c is >= 'a' and <= 'z') && c != '-' && c != '\'') return null;

        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '-') { sb.Append('ー'); i++; continue; }
            if (c == '\'') { i++; continue; }
            // 「nn」は「ん」1 つ（kennri → けんり）。後ろが母音・y なら、最初の n だけが「ん」で、2 つ目は次の音（konna → こんな）
            if (c == 'n' && i + 1 < s.Length && s[i + 1] == 'n')
            {
                var after = i + 2 < s.Length ? s[i + 2] : '\0';
                sb.Append('ん');
                i += after is 'a' or 'i' or 'u' or 'e' or 'o' or 'y' ? 1 : 2;
                continue;
            }
            // 促音: 同じ子音が続く（n と母音は除く）
            if (i + 1 < s.Length && s[i + 1] == c && "aiueon".IndexOf(c) < 0) { sb.Append('っ'); i++; continue; }
            var hit = false;
            for (var len = 4; len >= 1; len--)
            {
                if (i + len > s.Length) continue;
                if (Table.TryGetValue(s.Substring(i, len), out var kana)) { sb.Append(kana); i += len; hit = true; break; }
            }
            if (hit) continue;
            if (c == 'n') { sb.Append('ん'); i++; if (i < s.Length && s[i] == '\'') i++; continue; }   // 母音・y の前以外の n
            return null;
        }
        return sb.ToString();
    }
}
