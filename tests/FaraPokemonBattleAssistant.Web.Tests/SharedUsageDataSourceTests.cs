using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 使用率のファイルを、同じパスについて 1 回だけ読むデータソース（Champions の UsageFallback で Gen9 の使用率が二重に読まれていた問題）。
/// </summary>
public class SharedUsageDataSourceTests
{
    /// <summary>読み込み回数を数える、テスト用のデータソース。<see cref="Gate"/> を渡すと、完了を止めて並行の読み込みを再現できる。</summary>
    private sealed class CountingSource : IDataSource
    {
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource? Gate { get; set; }
        public int FailuresLeft { get; set; }
        /// <summary>ファイルが無い（null を返す）パス。</summary>
        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

        public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            Calls[relativePath] = Calls.GetValueOrDefault(relativePath) + 1;
#pragma warning disable VSTHRD003 // テスト用の関門。呼び出し側の TaskCompletionSource をわざと待つ
            if (Gate is not null) await Gate.Task;
#pragma warning restore VSTHRD003
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new HttpRequestException("一時的な失敗");
            }
            return Missing.Contains(relativePath) ? null : "Head\n" + relativePath;
        }
    }

    [Fact]
    public async Task Concurrent_reads_of_the_same_usage_file_hit_the_inner_source_once()
    {
        var inner = new CountingSource { Gate = new TaskCompletionSource() };
        var shared = new SharedUsageDataSource(inner);

        var first = shared.ReadTextAsync("Gen9/usage_moves_singles.csv");
        var second = shared.ReadTextAsync("Gen9/usage_moves_singles.csv");
        inner.Gate.SetResult();

        Assert.Equal(await first, await second);
        Assert.Equal(1, inner.Calls["Gen9/usage_moves_singles.csv"]);
    }

    [Fact]
    public async Task Cancelling_one_caller_does_not_cancel_the_shared_read()
    {
        var inner = new CountingSource { Gate = new TaskCompletionSource() };
        var shared = new SharedUsageDataSource(inner);
        using var cts = new CancellationTokenSource();

        var cancelled = shared.ReadTextAsync("Gen9/usage_moves_singles.csv", cts.Token);
        await cts.CancelAsync();
#pragma warning disable VSTHRD003 // 先に始めた読み込み（cancelled）の結果を、わざと待つ
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
#pragma warning restore VSTHRD003

        // 呼んだ側がキャンセルしても、読み込み自体は続き、後から同じパスを読んだ側はそれを使う（取り直さない）
        inner.Gate.SetResult();
        var text = await shared.ReadTextAsync("Gen9/usage_moves_singles.csv");

        Assert.Equal("Head\nGen9/usage_moves_singles.csv", text);
        Assert.Equal(1, inner.Calls["Gen9/usage_moves_singles.csv"]);
    }

    [Fact]
    public async Task Completed_usage_read_is_reused_later()
    {
        var inner = new CountingSource();
        var shared = new SharedUsageDataSource(inner);

        await shared.ReadTextAsync("Gen9/usage_items_doubles.csv");
        await shared.ReadTextAsync("Gen9/usage_items_doubles.csv");

        Assert.Equal(1, inner.Calls["Gen9/usage_items_doubles.csv"]);
    }

    [Fact]
    public async Task Missing_usage_file_is_shared_as_null()
    {
        var inner = new CountingSource();
        inner.Missing.Add("Champions/usage_pokemon_singles.csv");
        var shared = new SharedUsageDataSource(inner);

        Assert.Null(await shared.ReadTextAsync("Champions/usage_pokemon_singles.csv"));
        Assert.Null(await shared.ReadTextAsync("Champions/usage_pokemon_singles.csv"));
        Assert.Equal(1, inner.Calls["Champions/usage_pokemon_singles.csv"]);
    }

    [Fact]
    public async Task Failed_usage_read_is_not_kept_and_is_retried()
    {
        var inner = new CountingSource { FailuresLeft = 1 };
        var shared = new SharedUsageDataSource(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() => shared.ReadTextAsync("Gen9/usage_natures_singles.csv"));
        var text = await shared.ReadTextAsync("Gen9/usage_natures_singles.csv");

        Assert.Equal("Head\nGen9/usage_natures_singles.csv", text);
        Assert.Equal(2, inner.Calls["Gen9/usage_natures_singles.csv"]);
    }

    [Fact]
    public async Task Other_files_are_not_shared()
    {
        var inner = new CountingSource();
        var shared = new SharedUsageDataSource(inner);

        await shared.ReadTextAsync("Gen9/pokemon.csv");
        await shared.ReadTextAsync("Gen9/pokemon.csv");

        Assert.Equal(2, inner.Calls["Gen9/pokemon.csv"]);
    }

    [Theory]
    [InlineData("Gen9/usage_pokemon_singles.csv", true)]
    [InlineData("Champions/usage_abilities_doubles.csv", true)]
    [InlineData("Gen9/pokemon.csv", false)]
    [InlineData("usage_moves_singles.txt", false)]
    [InlineData("Gen9/usage_pokemon_singles.csv.bak", false)]
    public void Only_usage_csv_files_are_shared(string path, bool expected)
    {
        Assert.Equal(expected, SharedUsageDataSource.IsUsageFile(path));
    }
}
