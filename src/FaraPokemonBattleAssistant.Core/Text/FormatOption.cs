using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;

namespace FaraPokemonBattleAssistant.Core.Text;

/// <summary>
/// コマンドの形式オプション。<c>-f シングル</c> / <c>-f single</c> / <c>-f ダブル</c> / <c>-f double</c>、
/// <c>--format シングル</c> / <c>--format single</c> / <c>--format ダブル</c> / <c>--format double</c>（<c>singles</c>・<c>doubles</c> も可。大文字小文字は区別しない）。
/// 全角のスペースや英数字は、コマンド全体を読む前の正規化（<see cref="CommandText.Normalize"/>）で半角になっている。
/// <c>-f=ダブル</c> / <c>--format=ダブル</c> / <c>-fダブル</c> のように値を続けて書くこともできる。
/// </summary>
public static class FormatOption
{
    /// <summary>
    /// 言葉の並びから形式オプションを取り除く。形式の指定が無ければ Format は null。
    /// 値が無い・形式の言葉でない・別々の形式が 2 回書かれているときは、Error に案内を入れる。
    /// </summary>
    public static (List<string> Words, BattleFormat? Format, string? Error) Extract(IReadOnlyList<string> words)
    {
        var rest = new List<string>();
        BattleFormat? found = null;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var lower = word.ToLowerInvariant();
            string? value;
            if (lower is "-f" or "--format")
            {
                if (i + 1 >= words.Count) return (rest, null, $"「{word}」の後に、形式を書いてください（シングル / ダブル。例: -f ダブル）");
                value = words[++i];
            }
            else if (lower.StartsWith("--format=", StringComparison.Ordinal)) value = word["--format=".Length..];
            else if (lower.StartsWith("-f=", StringComparison.Ordinal)) value = word["-f=".Length..];
            else if (lower.StartsWith("-f", StringComparison.Ordinal) && !lower.StartsWith("--", StringComparison.Ordinal) && lower.Length > 2 && TeamFormats.TryParseWord(word[2..], out _))
                value = word[2..];
            else
            {
                rest.Add(word);
                continue;
            }
            if (!TeamFormats.TryParseWord(value, out var format))
                return (rest, null, $"形式「{value}」が分かりません。シングル / single、ダブル / double のどれかを書いてください（例: -f ダブル、--format single）");
            if (found is { } previous && previous != format)
                return (rest, null, "形式が 2 つ書かれています（シングルとダブルのどちらか 1 つにしてください）");
            found = format;
        }
        return (rest, found, null);
    }
}
