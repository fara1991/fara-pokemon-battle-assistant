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

    private int loadVersion;

    /// <summary>
    /// データを読み込む。失敗したら画面全体をエラー画面にせず、<see cref="LoadError"/> にメッセージを入れる。
    /// <paramref name="load"/> には読み込みの番号を渡す。await の後で結果を画面に入れる前に <see cref="IsLatestLoad"/> で確かめる（ゲームを素早く切り替えると、古い読み込みが後で終わるため）。
    /// <paramref name="onFailure"/> では、前のゲームのデータで描画が続かないよう、読み込み済みのデータを消す。
    /// </summary>
    protected async Task LoadSafelyAsync(Func<int, Task> load, Action onFailure)
    {
        var version = ++loadVersion;
        try
        {
            LoadError = "";
            await load(version);
        }
#pragma warning disable CA1031 // 画面の読み込みの最上位。どんな失敗も画面のメッセージにして表示する
        catch (Exception ex)
        {
            // 古い読み込みの失敗は、最新の読み込みに任せる（新しい成功の結果を、古い失敗のメッセージで壊さない）
            if (!IsLatestLoad(version)) return;
            onFailure();
            LoadError = $"データの読み込みに失敗しました: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    /// <summary>この番号の読み込みが、いちばん新しいもの（後から別の読み込みが始まっていない）で、ページも閉じていないか。</summary>
    protected bool IsLatestLoad(int version) => version == loadVersion && !disposed;

    /// <summary>読み込んだデータが、いま選んでいるゲームのものか。ゲームを切り替えた直後は、読み込みが終わるまで違う（前のゲームのデータを操作・表示させないために使う）。</summary>
    protected bool IsCurrentGame(FaraPokemonBattleAssistant.Core.Data.PokemonDataSet? loaded) =>
        AppState.IsSameGame(loaded?.Info.Key, App.DataSetKey);

    /// <summary>AppState の初期化後に 1 回呼ばれる。</summary>
    protected virtual Task OnAppReadyAsync() => Task.CompletedTask;

    /// <summary>データセットまたはバトル形式が変わったときに呼ばれる。</summary>
    protected virtual Task OnAppChangedAsync() => Task.CompletedTask;

    private async Task HandleAppChangedAsync()
    {
        // OnAppChangedAsync は、読み込みを待つ前に、前のゲームのデータを外す（同期の部分）。その後で描き直すので、外した状態が画面に出る。
        // 読み込みを待たずに出せるもの（接頭辞やゲーム名など）は、待つ前に描き直す
        var loading = OnAppChangedAsync();
        await InvokeAsync(StateHasChanged);
        await loading;
        await InvokeAsync(StateHasChanged);
    }

    public virtual void Dispose()
    {
        disposed = true;
        App.Changed -= HandleAppChangedAsync;
        GC.SuppressFinalize(this);
    }
}
