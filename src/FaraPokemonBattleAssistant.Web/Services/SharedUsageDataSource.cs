using FaraPokemonBattleAssistant.Core.Data;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 使用率のファイル（<c>usage_*.csv</c>）の読み込みを、同じパスについて 1 回にまとめる（Web 専用）。
/// 代用の仕組み（<c>UsageFallback</c>）で、別のゲームの使用率のファイルを読むときも、Gen9 自身の読み込みと二重に取らないように
/// 読み込み中のものも含めて、同じパスの読み込みを共有する（いまは Champions の代用は空で、使わない）。
/// 失敗・キャンセルしたものは残さず、次の呼び出しで取り直す。使用率以外のファイルは、そのまま内側のデータソースに渡す。
/// Core の <see cref="HttpDataSource"/> には入れない（CLI・ボットは読み直しや <see cref="DataCatalog.Invalidate"/> で、キャッシュを捨てる必要があるため）。
/// </summary>
public sealed class SharedUsageDataSource : IDataSource
{
    private readonly IDataSource _inner;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<string?>> _usage = new(StringComparer.Ordinal);

    public SharedUsageDataSource(IDataSource inner)
    {
        _inner = inner;
    }

    public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (!IsUsageFile(relativePath))
            return _inner.ReadTextAsync(relativePath, cancellationToken);

        Task<string?> task;
        lock (_gate)
        {
            // 失敗・キャンセルした読み込みは残さない（取り除く処理が後から走るので、ここでも見て取り直す）
            if (!_usage.TryGetValue(relativePath, out var cached) || cached.IsFaulted || cached.IsCanceled)
            {
                // 読み込み自体は、最初に呼んだ側のキャンセルに巻き込まない（同じパスを待つ別の側と共有するため）。待つだけを、呼んだ側ごとにキャンセルする
                cached = _inner.ReadTextAsync(relativePath, CancellationToken.None);
                _usage[relativePath] = cached;
                var loading = cached;
                _ = loading.ContinueWith(t =>
                {
                    _ = t.Exception;   // 待っている人がいなくても、例外を観測済みにする
                    lock (_gate)
                    {
                        if (_usage.TryGetValue(relativePath, out var current) && ReferenceEquals(current, loading)) _usage.Remove(relativePath);
                    }
                }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }
            task = cached;
        }
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>使用率のファイルか（<c>&lt;データセット&gt;/usage_*.csv</c>）。</summary>
    public static bool IsUsageFile(string relativePath)
    {
        var name = relativePath.Substring(relativePath.LastIndexOf('/') + 1);
        return name.StartsWith("usage_", StringComparison.Ordinal) && name.EndsWith(".csv", StringComparison.Ordinal);
    }
}
