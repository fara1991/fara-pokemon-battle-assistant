using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.AspNetCore.Components;

namespace FaraPokemonBattleAssistant.Web.Shared;

/// <summary>
/// サイドバーのデータセット・バトル形式の変更を受け取るページの基底クラス。
/// </summary>
public abstract class AppPageBase : ComponentBase, IDisposable
{
    [Inject] protected AppState App { get; set; } = default!;
    [Inject] protected PageStateStore PageStates { get; set; } = default!;

    /// <summary>入力欄の値（文字列）を ID にする。数字でなければ 0。</summary>
    protected static int ParseId(object? value) => int.TryParse(value?.ToString(), out var id) ? id : 0;

    private bool disposed;

    /// <summary>ページが閉じられたか（読み込みの待ち時間のあいだに別のページへ移ったときの後始末の判定に使う）。</summary>
    protected bool IsDisposed => disposed;

    /// <summary>データの読み込みに失敗したときのメッセージ（空なら失敗していない）。</summary>
    protected string LoadError { get; private set; } = "";

    protected override async Task OnInitializedAsync()
    {
        await App.InitializeAsync();
        // 初期化を待つあいだにページを離れていたら、購読しない（Dispose の解除が先に済んでいて、購読だけが残るため）
        if (disposed) return;
        App.Changed += HandleAppChangedAsync;
        await OnAppReadyAsync();
    }

    /// <summary>
    /// データを読み込む。失敗したら画面全体をエラー画面にせず、<see cref="LoadError"/> にメッセージを入れる。
    /// <paramref name="onFailure"/> では、前のゲームのデータで描画が続かないよう、読み込み済みのデータを消す。
    /// </summary>
    protected async Task LoadSafelyAsync(Func<Task> load, Action onFailure)
    {
        try
        {
            LoadError = "";
            await load();
        }
        catch (Exception ex)
        {
            onFailure();
            LoadError = $"データの読み込みに失敗しました: {ex.Message}";
        }
    }

    /// <summary>読み込んだデータが、いま選んでいるゲームのものか。ゲームを切り替えた直後は、読み込みが終わるまで違う（前のゲームのデータを操作・表示させないために使う）。</summary>
    protected bool IsCurrentGame(FaraPokemonBattleAssistant.Core.Data.PokemonDataSet? loaded) =>
        loaded is null || loaded.Info.Key == App.DataSetKey;

    /// <summary>AppState の初期化後に 1 回呼ばれる。</summary>
    protected virtual Task OnAppReadyAsync() => Task.CompletedTask;

    /// <summary>データセットまたはバトル形式が変わったときに呼ばれる。</summary>
    protected virtual Task OnAppChangedAsync() => Task.CompletedTask;

    private async Task HandleAppChangedAsync()
    {
        // 接頭辞やゲーム名など、データの読み込みを待たずに出せるものは先に描き直す
        await InvokeAsync(StateHasChanged);
        await OnAppChangedAsync();
        await InvokeAsync(StateHasChanged);
    }

    public virtual void Dispose()
    {
        disposed = true;
        App.Changed -= HandleAppChangedAsync;
    }
}
