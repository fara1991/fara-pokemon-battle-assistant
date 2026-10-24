using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// サイドメニューの初期状態（スマホで開いたまま一瞬見えない）と、保存のキーの確認。
/// レイアウト（ヘッダー・サイドバー・検索欄）は共通部品（fara-site-kit の SiteLayout）が持つ。
/// 保存済みの開閉と、スマホかどうかは、最初の描画の前（OnInitialized の同期の部分）で決める（非同期で取ると、開いた状態で描かれてから閉じてしまう）。
/// SiteLayout 自体の確認は kit のテスト（tests/FaraSiteKit.Razor.Tests）。ここでは、このサイトが正しく使っていることを確かめる。
/// </summary>
public class MainLayoutStartupTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");
    private static readonly string Kit = FindUpDirectory("external/fara-site-kit/src/FaraSiteKit.Razor");

    [Fact]
    public void Main_layout_uses_the_shared_site_layout_and_passes_the_site_contents()
    {
        var razor = Read(Path.Combine(Web, "Shared", "MainLayout.razor"));
        Assert.Contains("<SiteLayout Body=\"@Body\" Maintenance=\"Maintenance\"", razor);
        Assert.Contains("PageClass=\"@(Stage.BattleActive ? \"battle-active\" : null)\"", razor);   // 対戦画面の間は app.css が main を隠す
        Assert.Contains("<SearchBox>", razor);
        Assert.Contains("<HeaderEnd>", razor);
        Assert.Contains("<MobileHeaderActions>", razor);
        Assert.Contains("<SearchSheet Context=\"closeSearch\">", razor);
        Assert.Contains("<NavMenu OnNavigate=\"closeOnNavigate\" />", razor);
        // 背景のバトルフィールドと対戦画面は、これまで通り、レイアウトの外（前）に置く
        Assert.True(razor.IndexOf("<BattleStageBackground />", StringComparison.Ordinal) < razor.IndexOf("<SiteLayout", StringComparison.Ordinal));
        Assert.True(razor.IndexOf("<BattleOverlay />", StringComparison.Ordinal) < razor.IndexOf("<SiteLayout", StringComparison.Ordinal));
    }

    [Fact]
    public void Layout_reads_mobile_and_saved_sidebar_state_synchronously_before_the_first_render()
    {
        // kit の SiteLayout の約束（pokemon の MainLayout にあった実装の移し先）
        var razor = Read(Path.Combine(Kit, "Components", "SiteLayout.razor"));
        var sync = Section(razor, "protected override void OnInitialized()", "protected override async Task OnAfterRenderAsync(");
        Assert.Contains("JS is IJSInProcessRuntime", sync);
        Assert.Contains("\"faraSiteKit.watchMobile\"", sync);
        Assert.Contains("\"localStorage.getItem\"", sync);
        Assert.DoesNotContain("await", sync);
        Assert.Contains("private bool mobileOpen;", razor);
        Assert.Contains("private bool DrawerOpen => Mobile ? mobileOpen : desktopOpen;", razor);
    }

    [Fact]
    public void Nav_menu_reads_the_mobile_state_synchronously_before_the_first_render()
    {
        // サイドバーの中身（スマホのときだけ、上のほうに共通の設定を出す）も、最初の描画の前にスマホかどうかを決める
        var razor = Read(Path.Combine(Web, "Shared", "NavMenu.razor"));
        var sync = Section(razor, "protected override void OnInitialized()", "[JSInvokable]");
        Assert.Contains("JS is not IJSInProcessRuntime", sync);
        Assert.Contains("\"faraSiteKit.watchMobile\"", sync);
        Assert.DoesNotContain("await", sync);
        var asyncInit = Section(razor, "protected override async Task OnInitializedAsync()", "private Task OnChanged()");
        Assert.DoesNotContain("watchMobile", asyncInit);
    }

    [Fact]
    public void Storage_keys_are_the_same_in_site_options_and_the_index_html_data_attributes()
    {
        // 違うと、描画の前の反映（theme-init.js）と保存（C#）が別の場所を見る。値は、これまでのキー（ユーザーの保存済みの設定を引き継ぐ）
        Assert.Equal("fara-pokemon-battle-assistant.settings", Services.SiteInfo.StorageKey);
        Assert.Equal("fara-pokemon-battle-assistant.sidebar-hidden", Services.SiteInfo.SidebarKey);
        var options = new FaraSiteKit.Core.SiteOptions();
        Services.SiteInfo.Configure(options);
        Assert.Equal(Services.SiteInfo.StorageKey, options.StorageKey);
        Assert.Equal(Services.SiteInfo.SidebarKey, options.SidebarKey);
        var html = Read(Path.Combine(Web, "wwwroot", "index.html"));
        Assert.Contains($"data-storage-key=\"{Services.SiteInfo.StorageKey}\"", html);
        Assert.Contains($"data-sidebar-key=\"{Services.SiteInfo.SidebarKey}\"", html);
        Assert.Contains("_content/FaraSiteKit.Razor/js/theme-init.js", html);
        Assert.Equal("/", options.HomeHref);
    }

    [Fact]
    public void Site_init_script_keeps_the_legacy_key_cleanup_and_the_focus_helpers()
    {
        // 旧い theme-init.js にあった、このサイト固有の処理は、js/site-init.js に残す（head で、描画の前に読む。インラインの script は CSP で使えない）
        var html = Read(Path.Combine(Web, "wwwroot", "index.html"));
        Assert.Contains("<script src=\"js/site-init.js\"></script>", html);
        Assert.True(html.IndexOf("_content/FaraSiteKit.Razor/js/theme-init.js", StringComparison.Ordinal) < html.IndexOf("js/site-init.js", StringComparison.Ordinal));
        var js = Read(Path.Combine(Web, "wwwroot", "js", "site-init.js"));
        Assert.Contains("localStorage.removeItem(\"fara-pokemon-assistance.settings\")", js);
        Assert.Contains("localStorage.removeItem(\"fara-pokemon-assistance.roster\")", js);
        Assert.Contains("window.faraFocus = ", js);
        Assert.Contains("window.faraScrollIntoView = ", js);
        Assert.False(File.Exists(Path.Combine(Web, "wwwroot", "js", "theme-init.js")), "kit の theme-init.js に一本化した（両方を読まない）");
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
