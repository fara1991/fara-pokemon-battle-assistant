using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.AspNetCore.Components;

namespace FaraPokemonBattleAssistant.Web.Shared;

/// <summary>
/// 「育成ポケモン」「自分チーム」ページの共通部分。ゲームのデータと登録データを読み込み、変更の結果をトーストで知らせる。
/// </summary>
public abstract class RosterPageBase : AppPageBase
{
    [Inject] protected DataCatalog Catalog { get; set; } = default!;
    [Inject] protected RosterRepository Repo { get; set; } = default!;
    [Inject] protected CloudSync Sync { get; set; } = default!;
    [Inject] protected ToastService Toast { get; set; } = default!;

    private int loadVersion;

    protected DataSetInfo? dataSet;
    protected PokemonDataSet? data;
    protected DataSetRoster? roster;

    /// <summary>ページの入力状態を置くキー（PageStateStore）。</summary>
    protected abstract string StateKey { get; }

    /// <summary>ゲームのデータと登録データを読み込んだあとに呼ぶ。ゲームが変わったときの入力の初期化などを行う。</summary>
    protected virtual void OnDataLoaded() { }

    protected override async Task OnAppReadyAsync()
    {
        Sync.Changed += OnSyncChanged;
        await LoadAsync();
    }

    private void OnSyncChanged() => _ = InvokeAsync(StateHasChanged);

    public override void Dispose()
    {
        Sync.Changed -= OnSyncChanged;
        base.Dispose();
    }

    protected override Task OnAppChangedAsync() => LoadAsync();

    protected async Task LoadAsync()
    {
        // ゲームを素早く切り替えたとき、古い読み込みが後から終わって別のゲームのデータで上書きしないよう、
        // 読み込み中の値は変数に持ち、最新の読み込みのときだけ、まとめて画面の値にする
        var version = ++loadVersion;
        try
        {
            var key = App.DataSetKey;
            var info = App.DataSet;
            var loaded = await Catalog.GetDataSetAsync(key);
            var loadedRoster = (await Repo.GetAsync()).For(key);
            if (version != loadVersion || IsDisposed) return;
            dataSet = info;
            data = loaded;
            roster = loadedRoster;
            OnDataLoaded();
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;   // 古い読み込みの失敗は、最新の読み込みに任せる
            // 前のゲームのデータで描画（と、削除などの操作）が続かないようにする
            data = null;
            roster = null;
            Show($"読み込みに失敗しました: {CloudSync.Describe(ex)}", true);
        }
    }

    /// <summary>登録データだけ読み直す（変更のあと。クラウドでは他の場所の変更も反映される）。</summary>
    protected async Task RefreshRosterAsync()
    {
        try
        {
            roster = (await Repo.GetAsync()).For(App.DataSetKey);
        }
        catch (Exception ex)
        {
            Show($"読み込みに失敗しました: {CloudSync.Describe(ex)}", true);
        }
    }

    /// <summary>登録データを変更する操作を実行する。失敗したら理由を出し、最新の内容を読み直す。</summary>
    protected async Task MutateAsync(Func<Task> action)
    {
        // 実行中に同じ操作がもう一度押されても、二重に実行しない
        if (mutating) return;
        mutating = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Show(CloudSync.Describe(ex), true);
        }
        finally
        {
            await RefreshRosterAsync();
            mutating = false;
        }
    }

    private bool mutating;

    /// <summary>登録データを変更する操作を実行中か。</summary>
    protected bool Mutating => mutating;

    /// <summary>処理の結果は画面右上のトーストで知らせる（Services/ToastService.cs）。</summary>
    protected void Show(string text, bool isError) => Toast.Show(text, isError);

    protected static int ParseId(object? value) => int.TryParse(value?.ToString(), out var v) ? v : 0;
    protected static string FormatStats(StatSet st) => $"H{st.HP}-A{st.Attack}-B{st.Defense}-C{st.SpAttack}-D{st.SpDefense}-S{st.Speed}";

    /// <summary>性格の表示（補正つき。例: いじっぱり（A↑C↓））。</summary>
    protected string NatureText(string name) => data?.FindNature(name)?.Describe() ?? name;
}
