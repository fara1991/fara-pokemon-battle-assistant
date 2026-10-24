using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 768px 前後のサイドバーの食い違いと、スマホの検索（ヘッダーの検索ボタン → 下に開く検索欄）の並びを、ソースの文言で確かめる
/// （描画の確認は、ブラウザ（ヘッドレス Chromium）で行う）。
/// ヘッダー・サイドバー・検索欄の枠は共通部品（fara-site-kit の SiteLayout。確認は kit の tests/FaraSiteKit.Razor.Tests と tests/js/kit-js.test.mjs）。
/// ここでは、このサイトの検索部品（SiteSearch）と CSS が、その約束に合っていることを確かめる。
/// </summary>
public class MobileLayoutTests
{
    private static readonly string Web = FindUpDirectory("src/FaraPokemonBattleAssistant.Web");
    private static readonly string Kit = FindUpDirectory("external/fara-site-kit/src/FaraSiteKit.Razor");

    [Fact]
    public void Drawer_sets_breakpoint_Sm_and_leaves_the_mobile_variant_to_the_Mobile_switch()
    {
        // 既定の Md（960px）だと、768〜959px で Mini が Temporary に落ちて、サイドバーが画面の外に消える。kit の SiteLayout は Sm にしている
        var razor = Read(Kit, "Components", "SiteLayout.razor");
        var drawer = Section(razor, "<MudDrawer", "</MudDrawer>");
        Assert.Contains("Breakpoint=\"Breakpoint.Sm\"", drawer);
        Assert.Contains("Variant=\"@(Mobile ? DrawerVariant.Temporary : DrawerVariant.Mini)\"", drawer);
    }

    [Fact]
    public void Mobile_header_has_a_search_button_with_an_accessible_name_and_state()
    {
        var razor = Read(Kit, "Components", "SiteLayout.razor");
        var mobile = Section(razor, "@if (Mobile)\n        {\n            @MobileHeaderActions", "else");
        Assert.Contains("Class=\"fsk-search-toggle\"", mobile);
        Assert.Contains("aria-label=\"検索\"", mobile);
        Assert.Contains("aria-controls=\"fsk-search-sheet\"", mobile);
        Assert.Contains("aria-expanded=\"@(searchOpen ? \"true\" : \"false\")\"", mobile);
        Assert.Contains("OnClick=\"OpenSearch\"", mobile);
    }

    [Fact]
    public void Mobile_search_sheet_is_mounted_only_in_the_mobile_branch_and_PC_search_is_not_duplicated()
    {
        var razor = Read(Web, "Shared", "MainLayout.razor");
        Assert.Contains("<SiteSearch Sheet=\"true\" OnDone=\"closeSearch\" />", razor);
        // PC の検索欄（ヘッダーの中の SiteSearch）と、スマホの検索欄は、同時に描かれない（入力欄の id が重ならない）。kit は、スマホのときだけ SearchSheet を描く
        var layout = Read(Kit, "Components", "SiteLayout.razor");
        Assert.Contains("@if (Mobile && SearchSheet is not null)", layout);
        var pcBranch = Section(layout, "else\n        {\n            @SearchBox", "</MudAppBar>");
        Assert.Contains("@HeaderEnd", pcBranch);
        Assert.DoesNotContain("Sheet=", pcBranch);
        Assert.Contains("<SiteSearch />", Section(razor, "<SearchBox>", "</SearchBox>"));
    }

    [Fact]
    public void Search_button_controls_the_sheet_element_that_exists_in_the_markup()
    {
        var layout = Read(Kit, "Components", "SiteLayout.razor");
        Assert.Contains("aria-controls=\"fsk-search-sheet\"", layout);
        Assert.Contains("<div id=\"fsk-search-sheet\" class=\"fsk-search-sheet\"", layout);
        // 欄の枠は kit が持つ。このサイトの検索部品は、自分の id（site-search-sheet）を持たない（枠と二重にならない）
        var search = Read(Web, "Shared", "SiteSearch.razor");
        Assert.DoesNotContain("id=\"@(Sheet", search);
        Assert.DoesNotContain("id=\"site-search-sheet\"", search);
    }

    [Fact]
    public void Escape_is_handled_on_the_search_root_so_focus_anywhere_in_the_sheet_closes_it()
    {
        var razor = Read(Web, "Shared", "SiteSearch.razor");
        var root = Section(razor, "<div class=\"site-search @(Sheet", "\n");
        Assert.Contains("@onkeydown=\"OnKeyDown\"", root);
        // 検索の契約: 検索部品が Esc を先に使う（候補を閉じる・欄を閉じる）ので、kit の欄（#fsk-search-sheet）へは伝えない
        Assert.Contains("@onkeydown:stopPropagation=\"true\"", root);
        // 入力欄の keydown は、根の要素に移した（× ボタンや結果の上でも、Esc が届く）
        Assert.Contains("value=\"@query\" @oninput=\"OnInput\" />", razor);
        Assert.DoesNotContain("@onkeydown=\"OnKeyDown\" />", razor);
    }

    [Fact]
    public void Layout_closes_an_open_search_sheet_without_moving_focus_when_the_hamburger_is_pressed()
    {
        var razor = Read(Kit, "Components", "SiteLayout.razor");
        var toggle = Section(razor, "private void ToggleDrawer()", "/// <summary>MudBlazor が知らせる開閉");
        Assert.Contains("if (searchOpen) HideSearch(focusButton: false);", toggle);
    }

    [Fact]
    public void Every_search_sheet_js_call_goes_through_a_guarded_helper()
    {
        var razor = Read(Kit, "Components", "SiteLayout.razor");
        var helper = Section(razor, "private async Task SetSearchSheetAsync(", "private async Task ApplyDesktopStateAsync()");
        Assert.Contains("try { await JS.InvokeVoidAsync(\"faraSiteKit.searchSheet\", open, focus); }", helper);
        Assert.Contains("catch { /* JS が使えないときは、反映しない */ }", helper);
    }

    [Fact]
    public void Closing_on_mobile_or_navigation_moves_focus_out_of_the_sheet_first()
    {
        var razor = Read(Kit, "Components", "SiteLayout.razor");
        var mobileChanged = Section(razor, "public void OnMobileChanged(bool matches)", "private void ToggleDrawer()");
        Assert.Contains("_ = SetSearchSheetAsync(false, true);", mobileChanged);   // 表示の切り替えでは、C# の状態に関係なく閉じる
        var location = Section(razor, "private async void OnLocationChanged(", "if (!firstLocation)");
        Assert.Contains("HideSearch(focusButton: true);", location);
    }

    [Fact]
    public void Opening_the_search_does_not_wait_for_render_to_focus_it()
    {
        // フォーカスは、押した流れの中で kit の fara-site-kit.js が行う（iOS の対策）。C# 側の OpenSearch は、状態だけを変える
        var razor = Read(Kit, "Components", "SiteLayout.razor");
        var open = Section(razor, "private void OpenSearch()", "private void CloseSearch()");
        Assert.DoesNotContain("JS.", open);
        Assert.Contains("searchOpen = true;", open);
        var js = Read(Kit, "wwwroot", "js", "fara-site-kit.js");
        Assert.Contains("closest(\".fsk-search-toggle\")) api.searchSheet(true, true);", js);
        Assert.Contains("classList.toggle(\"fsk-search-open\", !!open)", js);
        // このサイトの page.js には、検索欄の開閉・ボタンの click の処理を残さない（kit と二重にならない）
        var page = Read(Web, "wwwroot", "js", "page.js");
        Assert.DoesNotContain("faraSearchSheet", page);
        Assert.DoesNotContain("header-search-toggle", page);
        Assert.DoesNotContain("faraWatchMobile", page);
        Assert.DoesNotContain("faraSidebarChanged", page);
    }

    [Fact]
    public void Search_component_has_a_sheet_mode_with_a_close_button_and_escape_closes_it()
    {
        var razor = Read(Web, "Shared", "SiteSearch.razor");
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
    public void Sheet_is_hidden_unless_the_mobile_search_is_open()
    {
        // 枠（.fsk-search-sheet）の出し入れは kit の CSS（閉じているあいだは display: none。html.fsk-search-open で出す）。見た目（余白・背景）は app.css で、これまで通りにする
        var kitCss = Read(Kit, "wwwroot", "css", "fara-site-kit.css");
        Assert.Contains(".fsk-search-sheet {\n    display: none;\n}", kitCss);
        Assert.Contains("html.fsk-search-open .fsk-search-sheet", kitCss);
        var css = Read(Web, "wwwroot", "css", "app.css");
        Assert.Contains("html.fsk-search-open .fsk-search-sheet {", css);
        Assert.Contains("padding: 8px 56px 8px 12px;", Section(css, "html.fsk-search-open .fsk-search-sheet {", "}"));
        // 検索部品は枠の中に position: static で入る（結果の一覧・× の位置の基準が、枠になる）
        Assert.Contains("position: static;", Section(css, ".fsk-search-sheet .site-search-sheet {", "}"));
        // 16px 未満の入力欄は、iOS が入力のときに拡大するので、スマホの検索欄は 16px
        var sheet = Section(css, ".site-search-sheet .site-search-input {", "}");
        Assert.Contains("font-size: 1rem;", sheet);
    }

    [Fact]
    public void Search_button_meets_the_44px_tap_target()
    {
        // ボタンの大きさは kit の CSS と、このサイトの app.css の両方で 44px
        foreach (var css in new[] { Read(Kit, "wwwroot", "css", "fara-site-kit.css"), Read(Web, "wwwroot", "css", "app.css") })
        {
            var rule = Section(css, ".fsk-header .fsk-search-toggle {", "}");
            Assert.Contains("width: 44px;", rule);
            Assert.Contains("height: 44px;", rule);
        }
    }

    [Fact]
    public void Mobile_page_scrolls_as_a_whole_like_before_even_though_the_kit_fits_the_page_to_the_screen()
    {
        // kit のレイアウトはスマホでも画面の高さに収める。このサイトのスマホは、これまで通りページ全体がスクロールする（app.css が、スマホだけ戻す）
        var css = Read(Web, "wwwroot", "css", "app.css");
        var mobile = Section(css, "@media (max-width: 767.98px) {\n    /* kit は、スマホでも", "/* 対戦画面を出している間は");
        Assert.Contains(".fsk-page.mud-layout {\n        height: auto;\n        min-height: 100vh;\n        overflow: visible;", mobile);
        Assert.Contains(".fsk-page .mud-main-content {\n        display: block;\n        height: auto;", mobile);
        Assert.Contains("overflow-y: visible;", mobile);
        Assert.Contains("html.fsk-sidebar-hidden .fsk-page .mud-main-content {\n        margin-left: 0 !important;", mobile);
    }

    private static string Section(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"見つからない: {startMarker}");
        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"見つからない: {endMarker}");
        return text[start..end];
    }

    /// <summary>ファイルを読む（改行は LF にそろえる）</summary>
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(parts)).Replace("\r\n", "\n");

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
