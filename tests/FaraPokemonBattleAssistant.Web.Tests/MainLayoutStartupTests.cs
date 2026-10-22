using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// サイドメニューの初期状態（スマホで開いたまま一瞬見えない）の確認。
/// 保存済みの開閉と、スマホかどうかは、最初の描画の前（OnInitialized の同期の部分）で決める。非同期で取ると、開いた状態で描かれてから閉じてしまう。
/// </summary>
public class MainLayoutStartupTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");

    [Fact]
    public void Mobile_and_saved_sidebar_state_are_read_synchronously_before_the_first_render()
    {
        var razor = Read(Path.Combine(Web, "Shared", "MainLayout.razor"));
        var sync = Section(razor, "protected override void OnInitialized()", "protected override async Task OnInitializedAsync()");
        Assert.Contains("JS is IJSInProcessRuntime", sync);
        Assert.Contains("\"faraWatchMobile\"", sync);
        Assert.Contains("\"localStorage.getItem\"", sync);
        Assert.DoesNotContain("await", sync);
    }

    [Fact]
    public void Async_init_does_not_read_the_mobile_state_again()
    {
        // 非同期の部分（データの読み込み）では、開閉の初期値を取り直さない（描画の後に閉じる原因になる）
        var razor = Read(Path.Combine(Web, "Shared", "MainLayout.razor"));
        var asyncInit = Section(razor, "protected override async Task OnInitializedAsync()", "/// <summary>サイドバーの見出し");
        Assert.DoesNotContain("faraWatchMobile", asyncInit);
        Assert.DoesNotContain("localStorage.getItem", asyncInit);
    }

    [Fact]
    public void Mobile_drawer_starts_closed()
    {
        // スマホの開閉の初期値は false（DrawerOpen は Mobile のとき mobileOpen）
        var razor = Read(Path.Combine(Web, "Shared", "MainLayout.razor"));
        Assert.Contains("private bool mobileOpen;", razor);
        Assert.Contains("private bool DrawerOpen => Mobile ? mobileOpen : desktopOpen;", razor);
    }

    private static string Section(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"見つからない: {startMarker}");
        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"見つからない: {endMarker}");
        return text[start..end];
    }

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string FindUpDirectory(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"{relative} が見つかりません");
    }
}
