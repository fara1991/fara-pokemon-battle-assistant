using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>
/// 文化依存（ICU）の StartsWith / EndsWith と、本番で使っている序数（StringComparison.Ordinal）の結果が同じことを確かめる。
/// 本番の呼び出しごとに、実際に比べる文字列を集める（グループ）:
///   speed_items … Speed.razor: 持ち物の名前（そのまま） × 「パワー」の前方一致
///   tera_words  … DamageCommand.TryParseTera: 全ての名前を NameNormalizer で正規化した形 × 「てらす」「tera」の前方・後方一致
///   tera_inputs … 同じ関数に入りうる、入力の例（正規化後） × 同じ検査語
/// 各グループの結果（ビット列）の SHA-256 を Data/culture_probe.txt に固定する（ICU ありで CULTURE_PROBE_WRITE=1 で作る）。
/// 両モード（DOTNET_SYSTEM_GLOBALIZATION_INVARIANT 0/1）で、序数の結果が記録と一致すること。
/// ICU ありで走るときは、その場で文化依存の結果と序数の結果が 1 件も違わないことも確かめる。
/// 比べる語が増えたら、ここと記録を作り直す。
/// </summary>
public class CultureProbeTests
{
    private static readonly string[] TeraWords = { "てらす", "tera" };
    private static readonly string[] TeraInputs =
    {
        "てらす", "てらすほのお", "ほのおてらす", "てらすみず", "tera", "teraほのお", "tera:fire",
        "てらーす", "てらすー", "てらぁす", "てらす：ほのお", "てらす ほのお", "てらすた", "てらほのお",
    };

    [Fact]
    public async Task Culture_and_ordinal_results_match_the_recorded_snapshot_per_call_site()
    {
        var data = new List<PokemonDataSet>();
        foreach (var key in new[] { "Gen9", "Champions" }) data.Add(await TestData.Catalog().GetDataSetAsync(key));

        var rawNames = data.SelectMany(d => d.Pokemon.Select(p => p.Name).Concat(d.Moves.Select(m => m.Name))
            .Concat(d.Items.Select(i => i.Name)).Concat(d.Abilities.Select(a => a.Name)))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var itemNames = data.SelectMany(d => d.Items.Select(i => i.Name)).Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var normalizedNames = rawNames.Select(NameNormalizer.Normalize).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var groups = new List<Group>
        {
            Group.Of("speed_items", itemNames, new[] { "パワー" }, startsOnly: true),
            Group.Of("tera_words", normalizedNames, TeraWords, startsOnly: false),
            Group.Of("tera_inputs", TeraInputs.Select(NameNormalizer.Normalize).ToList(), TeraWords, startsOnly: false),
        };

        var cultureOn = !string.IsNullOrEmpty(CultureInfo.CurrentCulture.Name);
        var lines = new List<string>();
        var mismatchLines = new List<string>();
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalMismatches = 0;
        foreach (var g in groups)
        {
            var ord = new StringBuilder();
            var cul = new StringBuilder();
            var mismatches = 0;
            foreach (var text in g.Texts)
            foreach (var probe in g.Probes)
            {
                var o1 = text.StartsWith(probe, StringComparison.Ordinal);
                var c1 = text.StartsWith(probe);
                ord.Append(o1 ? '1' : '0');
                cul.Append(c1 ? '1' : '0');
                if (!g.StartsOnly)
                {
                    var o2 = text.EndsWith(probe, StringComparison.Ordinal);
                    var c2 = text.EndsWith(probe);
                    ord.Append(o2 ? '1' : '0');
                    cul.Append(c2 ? '1' : '0');
                    if (o2 != c2) { mismatches++; if (mismatchLines.Count < 300) mismatchLines.Add($"{g.Name} EndsWith   [{text}] [{probe}] ordinal={o2} culture={c2}"); }
                }
                if (o1 != c1) { mismatches++; if (mismatchLines.Count < 300) mismatchLines.Add($"{g.Name} StartsWith [{text}] [{probe}] ordinal={o1} culture={c1}"); }
            }
            totalMismatches += mismatches;
            recorded[g.Name] = Digest(cul.ToString());
            actual[g.Name] = Digest(ord.ToString());
            lines.Add($"group={g.Name} texts={g.Texts.Count} probes={g.Probes.Count} mismatches={mismatches} culture_sha256={recorded[g.Name]} ordinal_sha256={actual[g.Name]}");
        }

        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("CULTURE_PROBE_WRITE") == "1")
        {
            // 記録は ICU ありの CurrentCulture の結果で作る。序数と違った例も残す（レビュー用）
            var all = new List<string> { $"culture_on={cultureOn}" };
            all.AddRange(lines);
            all.Add($"mismatch_examples (first {mismatchLines.Count}):");
            all.AddRange(mismatchLines);
            await File.WriteAllTextAsync(path, string.Join("\n", all) + "\n");
            Assert.Fail("文化依存の検査の記録を作り直しました。Data/culture_probe.txt を確かめて、CULTURE_PROBE_WRITE を外して再実行してください");
        }

        // 両モードで: 序数の結果が、ICU ありで記録した文化依存の結果と同じこと（グループごと）
        var recordedLines = await File.ReadAllLinesAsync(path);
        foreach (var g in groups)
        {
            var line = recordedLines.First(l => l.StartsWith($"group={g.Name} ", StringComparison.Ordinal));
            var expected = line.Split(' ').First(s => s.StartsWith("culture_sha256=", StringComparison.Ordinal)).Split('=')[1];
            Assert.True(expected == actual[g.Name], $"{g.Name}: 序数の結果が、ICU ありの記録と違います");
        }

        // ICU ありで走るときは、その場で文化依存の結果と序数の結果が 1 件も違わないこと
        if (cultureOn) Assert.True(totalMismatches == 0, string.Join("\n", mismatchLines));
    }

    private sealed record Group(string Name, IReadOnlyList<string> Texts, IReadOnlyList<string> Probes, bool StartsOnly)
    {
        public static Group Of(string name, IReadOnlyList<string> texts, IReadOnlyList<string> probes, bool startsOnly) =>
            new(name, texts, probes, startsOnly);
    }

    private static string Digest(string bits) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(bits))).ToLowerInvariant();

    private static string SnapshotPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonBattleAssistant.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "FaraPokemonBattleAssistant.Core.Tests", "Data", "culture_probe.txt");
    }
}
