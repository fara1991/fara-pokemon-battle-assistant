using MudBlazor;

namespace FaraPokemonBattleAssistant.Web.Services;

public enum ToastKind { Success, Warning, Error }

/// <summary>
/// 画面の右上に重ねて出す通知（トースト）。サイト全体で使う（表示は MudBlazor の Snackbar。Shared/MudProviders.razor の MudSnackbarProvider）。
/// 登録・削除・更新・データ共有など、データを変える処理の結果を知らせるときに呼ぶ。
/// 数秒で自動で消え、複数出たら縦に積む。
/// </summary>
public sealed class ToastService
{
    private readonly ISnackbar snackbar;

    public ToastService(ISnackbar snackbar) => this.snackbar = snackbar;

    public void Success(string text) => Show(ToastKind.Success, text);
    public void Warning(string text) => Show(ToastKind.Warning, text);
    public void Error(string text) => Show(ToastKind.Error, text);

    /// <summary>成功 / 失敗をまとめて受ける。</summary>
    public void Show(string text, bool isError) => Show(isError ? ToastKind.Error : ToastKind.Success, text);

    public void Show(ToastKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var severity = kind switch { ToastKind.Error => Severity.Error, ToastKind.Warning => Severity.Warning, _ => Severity.Success };
        var duration = (int)Lifetime(kind).TotalMilliseconds;
        snackbar.Add(text, severity, config =>
        {
            config.VisibleStateDuration = duration;
            config.RequireInteraction = false;
        });
    }

    /// <summary>自動で消えるまでの時間。失敗・警告は読む時間を長めにする。</summary>
    public static TimeSpan Lifetime(ToastKind kind) => kind switch
    {
        ToastKind.Error => TimeSpan.FromSeconds(9),
        ToastKind.Warning => TimeSpan.FromSeconds(7),
        _ => TimeSpan.FromSeconds(4.5),
    };
}
