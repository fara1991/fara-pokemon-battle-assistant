using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.JSInterop;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 全ページで共有する設定（<see cref="AppState"/>）: 画面の更新の購読先は、1 つ失敗しても残りが呼ばれ、保存は通知より先に行われ、同時に呼ばれる。
/// </summary>
public class AppStateTests
{
    /// <summary>ゲーム一覧だけを返し、ほかのデータは無いとするデータソース（AppState の初期化に必要なもの）。</summary>
    private sealed class DataSetListSource : IDataSource
    {
        private const string Csv = "Key,Name,Generation,EvSystem,CommandPrefix,Gimmick,UsageFallback,Calc\n"
            + "Champions,ポケモンチャンピオンズ,9,Points,pokech,None,Gen9,Full\n"
            + "Gen9,第9世代,9,Classic,pokesv,Terastal,,Full\n";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(relativePath == "datasets.csv" ? Csv : null);
    }

    /// <summary>呼ばれた関数の名前と、保存された設定を記録する、テスト用の JS 実行環境。</summary>
    private sealed class FakeJs : IJSRuntime
    {
        public string? Stored;
        public List<string> Calls { get; } = new();
        /// <summary>保存（localStorage.setItem）のとき、その時点で保存されている内容を記録する（保存が通知より先かを確かめる）。</summary>
        public Action<string>? OnCall { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            OnCall?.Invoke(identifier);
            if (identifier == "localStorage.getItem") return new ValueTask<TValue>((TValue)(object?)Stored!);
            if (identifier == "localStorage.setItem") Stored = (string)args![1]!;
            return new ValueTask<TValue>(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private static async Task<(AppState App, FakeJs Js)> CreateAsync()
    {
        var js = new FakeJs();
        var app = new AppState(new DataCatalog(new DataSetListSource(), null), js);
        await app.InitializeAsync();
        return (app, js);
    }

    [Fact]
    public async Task One_failing_subscriber_does_not_stop_the_others()
    {
        var (app, _) = await CreateAsync();
        var calledAfterFailure = false;
        app.Changed += () => throw new InvalidOperationException("画面の読み込みに失敗");
        app.Changed += () => { calledAfterFailure = true; return Task.CompletedTask; };

        await app.SetDataSetAsync("Gen9");

        Assert.True(calledAfterFailure);
        Assert.Equal("Gen9", app.DataSetKey);
    }

    [Fact]
    public async Task Settings_are_saved_before_subscribers_are_notified()
    {
        var (app, js) = await CreateAsync();
        string? savedWhenNotified = null;
        app.Changed += () => { savedWhenNotified = js.Stored; return Task.CompletedTask; };

        await app.SetDataSetAsync("Gen9");

        Assert.NotNull(savedWhenNotified);
        Assert.Contains("\"DataSetKey\":\"Gen9\"", savedWhenNotified);
    }

    [Fact]
    public async Task Format_change_is_also_saved_before_notifying()
    {
        var (app, js) = await CreateAsync();
        string? savedWhenNotified = null;
        app.Changed += () => { savedWhenNotified = js.Stored; return Task.CompletedTask; };

        await app.SetFormatAsync(FaraPokemonBattleAssistant.Core.Models.BattleFormat.Doubles);

        Assert.NotNull(savedWhenNotified);
        Assert.Contains("Doubles", savedWhenNotified);
    }

    [Fact]
    public async Task Subscribers_are_all_started_before_any_of_them_finishes()
    {
        // 1 つ目の購読先が読み込みを待っていても、2 つ目（メニューなど）は待たずに呼ばれる
        var (app, _) = await CreateAsync();
        var gate = new TaskCompletionSource();
        var secondStarted = false;
        app.Changed += async () => await gate.Task;
        app.Changed += () => { secondStarted = true; return Task.CompletedTask; };

        var change = app.SetDataSetAsync("Gen9");
        Assert.True(secondStarted);
        Assert.False(change.IsCompleted);   // 1 つ目が終わるまで、切り替えの処理は終わらない
        gate.SetResult();
        await change;
    }

    [Fact]
    public void Unloaded_or_same_game_counts_as_current()
    {
        Assert.True(AppState.IsSameGame(null, "Gen9"));
        Assert.True(AppState.IsSameGame("Gen9", "Gen9"));
        Assert.False(AppState.IsSameGame("Champions", "Gen9"));
    }
}
