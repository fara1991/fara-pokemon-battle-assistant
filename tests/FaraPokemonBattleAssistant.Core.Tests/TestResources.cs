namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>
/// テストの中で作った、破棄が必要なもの（HttpClient・偽サーバーなど）を持っておき、テストの終わりにまとめて破棄する。
/// 作る関数（テストの前準備）が、作ったものを呼び出し側へ渡すとき、破棄する人がいなくなるのを避けるために使う。
/// xUnit はテストごとにクラスを作り直し、IDisposable なら終わりに Dispose を呼ぶ。
/// </summary>
public sealed class TestResources : IDisposable
{
    private readonly List<IDisposable> _items = [];

    /// <summary>破棄を預かって、そのまま返す。</summary>
    public T Own<T>(T item) where T : IDisposable
    {
        _items.Add(item);
        return item;
    }

    public void Dispose()
    {
        for (var i = _items.Count - 1; i >= 0; i--) _items[i].Dispose();
        _items.Clear();
    }
}
