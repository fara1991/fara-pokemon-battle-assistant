using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 768px 前後のサイドバーの食い違いと、スマホの検索（ヘッダーの検索ボタン → 下に開く検索欄）の並びを、ソースの文言で確かめる
/// （bUnit はこの repo に無い。描画の確認は、ブラウザ（ヘッドレス Chromium）で行う）。
/// </summary>
public class MobileLayoutTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");

    [Fact]
    public void Drawer_sets_breakpoint_Sm_and_leaves_the_mobile_variant_to_the_Mobile_switch()
    {
        // 既定の Md（960px）だと、768〜959px で Mini が Temporary に落ちて、サイドバーが画面の外に消える。
        // Sm にする。768px 未満は、Mobile が Variant を Temporary に明示するので、Breakpoint は結果に影響しない
        var razor = Read("Shared", "MainLayout.razor");
        var drawer = Section(razor, "<MudDrawer", "</MudDrawer>");
        Assert.Contains("Breakpoint=\"Breakpoint.Sm\"", drawer);
        Assert.Contains("Variant=\"@(Mobile ? DrawerVariant.Temporary : DrawerVariant.Mini)\"", drawer);
    }

    [Fact]
    public void Mobile_header_has_a_search_button_with_an_accessible_name_and_state()
    {
        var razor = Read("Shared", "MainLayout.razor");
        var mobile = Section(razor, "@if (Mobile)\n        {\n            @* スマホでは、メニューを開かずに切り替えられるよう", "else");
        Assert.Contains("Class=\"header-search-toggle\"", mobile);
        Assert.Contains("aria-label=\"検索\"", mobile);
        Assert.Contains("aria-controls=\"site-search-sheet\"", mobile);
        Assert.Contains("aria-expanded=\"@(searchOpen ? \"true\" : \"false\")\"", mobile);
        Assert.Contains("OnClick=\"OpenSearch\"", mobile);
    }

    [Fact]
    public void Mobile_search_sheet_is_mounted_only_in_the_mobile_branch_and_PC_search_is_not_duplicated()
    {
        var razor = Read("Shared", "MainLayout.razor");
        Assert.Contains("<SiteSearch Sheet=\"true\" OnDone=\"CloseSearch\" />", razor);
        // PC の検索欄（ヘッダーの中の SiteSearch）と、スマホの検索欄は、同時に描かれない（入力欄の id が重ならない）
        var pcBranch = Section(razor, "else\n        {\n            <SiteSearch />", "</MudAppBar>");
        Assert.Contains("<HeaderControls />", pcBranch);
        Assert.DoesNotContain("Sheet=", pcBranch);
    }

    [Fact]
    public void Search_button_controls_the_sheet_element_that_exists_in_the_markup()
    {
        var layout = Read("Shared", "MainLayout.razor");
        Assert.Contains("aria-controls=\"site-search-sheet\"", layout);
        var search = Read("Shared", "SiteSearch.razor");
        // 開閉の対象（sheet の領域）には、同じ id が付く（スマホの欄のときだけ）
        Assert.Contains("<div id=\"@(Sheet ? \"site-search-sheet\" : null)\"", search);
    }

    [Fact]
    public void Escape_is_handled_on_the_search_root_so_focus_anywhere_in_the_sheet_closes_it()
    {
        var razor = Read("Shared", "SiteSearch.razor");
        var root = Section(razor, "<div id=\"@(Sheet", "\n");
        Assert.Contains("@onkeydown=\"OnKeyDown\"", root);
        // 入力欄の keydown は、根の要素に移した（× ボタンや結果の上でも、Esc が届く）
        Assert.Contains("value=\"@query\" @oninput=\"OnInput\" />", razor);
        Assert.DoesNotContain("@onkeydown=\"OnKeyDown\" />", razor);
    }

    [Fact]
    public void Hamburger_closes_an_open_search_sheet_without_moving_focus_to_the_button()
    {
        var razor = Read("Shared", "MainLayout.razor");
        var toggle = Section(razor, "private void ToggleDrawer()", "private void SetDrawerOpen");
        Assert.Contains("if (searchOpen) HideSearch(focusButton: false);", toggle);
    }

    [Fact]
    public void Every_search_sheet_js_call_goes_through_a_guarded_helper()
    {
        var razor = Read("Shared", "MainLayout.razor");
        Assert.DoesNotContain("JS.InvokeVoidAsync(\"faraSearchSheet\"", razor.Replace("try { await JS.InvokeVoidAsync(\"faraSearchSheet\"", ""));
        var helper = Section(razor, "private async Task SetSearchSheetAsync(", "/// <summary>サイドバーの見出し");
        Assert.Contains("try { await JS.InvokeVoidAsync(\"faraSearchSheet\", open, focus); }", helper);
        Assert.Contains("catch { /* JS が使えないときは、反映しない */ }", helper);
    }

    [Fact]
    public void Closing_on_mobile_or_navigation_moves_focus_out_of_the_sheet_first()
    {
        var razor = Read("Shared", "MainLayout.razor");
        var mobileChanged = Section(razor, "public void OnMobileChanged(bool matches)", "private bool DrawerOpen");
        Assert.Contains("_ = SetSearchSheetAsync(false, true);", mobileChanged);   // 表示の切り替えでは、C# の状態に関係なく閉じる
        var location = Section(razor, "private void OnLocationChanged(", "var next = GroupOf");
        Assert.Contains("HideSearch(focusButton: true);", location);
    }

    [Fact]
    public void Opening_the_search_does_not_wait_for_render_to_focus_it()
    {
        // フォーカスは、押した流れの中で js/page.js が行う（iOS の対策）。C# 側の OpenSearch は、状態だけを変える
        var razor = Read("Shared", "MainLayout.razor");
        var open = Section(razor, "private void OpenSearch()", "/// <summary>スマホの検索欄を閉じる");
        Assert.DoesNotContain("faraFocus", open);
        Assert.DoesNotContain("JS.", open);
        Assert.Contains("searchOpen = true;", open);
    }

    [Fact]
    public void Search_component_has_a_sheet_mode_with_a_close_button_and_escape_closes_it()
    {
        var razor = Read("Shared", "SiteSearch.razor");
        Assert.Contains("[Parameter] public bool Sheet { get; set; }", razor);
        Assert.Contains("[Parameter] public EventCallback OnDone { get; set; }", razor);
        Assert.Contains("class=\"site-search @(Sheet ? \"site-search-sheet\" : null)\"", razor);
        Assert.Contains("aria-label=\"検索を閉じる\"", razor);
        Assert.Contains("case \"Escape\" when Sheet && !open:", razor);
        // PC の欄は、今まで通り（Ctrl+K の表示と、入力欄の id）
        Assert.Contains("<span class=\"site-search-hint\" aria-hidden=\"true\"><kbd>Ctrl</kbd><kbd>K</kbd></span>", razor);
        Assert.Contains("id=\"site-search-input\"", razor);
    }

    [Fact]
    public void Search_button_opens_the_sheet_synchronously_in_page_js()
    {
        var js = Read("wwwroot", "js", "page.js");
        Assert.Contains("e.target.closest(\".header-search-toggle\")) window.faraSearchSheet(true, true);", js);
        Assert.Contains("window.faraSearchSheet = function (open, focus)", js);
        Assert.Contains("classList.toggle(\"search-open\", !!open)", js);
    }

    [Fact]
    public void Sheet_is_hidden_unless_the_mobile_search_is_open()
    {
        var css = Read("wwwroot", "css", "app.css");
        Assert.Contains(".site-search-sheet {\n    display: none;\n}", css);
        Assert.Contains("html.search-open .site-search-sheet", css);
        // 16px 未満の入力欄は、iOS が入力のときに拡大するので、スマホの検索欄は 16px
        var sheet = Section(css, ".site-search-sheet .site-search-input {", "}");
        Assert.Contains("font-size: 1rem;", sheet);
    }

    [Fact]
    public void Search_button_meets_the_44px_tap_target()
    {
        var css = Read("wwwroot", "css", "app.css");
        var rule = Section(css, ".app-header .header-search-toggle {", "}");
        Assert.Contains("width: 44px;", rule);
        Assert.Contains("height: 44px;", rule);
    }

    private static string Section(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"見つからない: {startMarker}");
        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"見つからない: {endMarker}");
        return text[start..end];
    }

    /// <summary>Web プロジェクト（src/FaraPokemonBattleAssistant.Web）の中のファイルを読む（改行は LF にそろえる）</summary>
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Web }.Concat(parts).ToArray())).Replace("\r\n", "\n");

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
