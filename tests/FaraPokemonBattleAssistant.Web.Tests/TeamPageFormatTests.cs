using Bunit;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Xunit;
using TeamPage = FaraPokemonBattleAssistant.Web.Components.Pages.Team;
using RosterTeam = FaraPokemonBattleAssistant.Core.Roster.Team;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 自分チームのページ: バトル形式はヘッダーだけで選ぶ。追加フォームにもチームごとにも形式の欄は無く、
/// 一覧にはヘッダーの形式のチームだけが出て、新しいチームもその形式になる。ほかの形式のチームのデータは残る。
/// チーム番号と使用中のチームは、シングルとダブルで別々（同じ番号を両方に作れ、使用中も形式ごと）。
/// </summary>
public class TeamPageFormatTests : BunitContext, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor のサービスは非同期の破棄だけなので、非同期で片付ける
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private const string Ds = "Champions";

    private readonly RosterRepository repo = new(new MemoryRosterStore());
    private AppState app = default!;

    private sealed class FileSource : IDataSource
    {
        public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(DataDirectory, relativePath);
            return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : null);
        }

        private static string DataDirectory
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir is not null)
                {
                    var candidate = Path.Combine(dir.FullName, "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "api", "game");
                    if (Directory.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
                throw new DirectoryNotFoundException("wwwroot/api/game not found");
            }
        }
    }

    /// <summary>登録ポケモンを 1 体入れ、そのポケモンを選んだ状態で、自分チームのページを開く。</summary>
    private async Task<IRenderedComponent<TeamPage>> OpenAsync(BattleFormat headerFormat, Func<int, Task>? seed = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var catalog = new DataCatalog(new FileSource(), null);
        app = new AppState(catalog, JSInterop.JSRuntime);
        await app.InitializeAsync();
        await app.SetDataSetAsync(Ds);
        await app.SetFormatAsync(headerFormat);

        var data = await catalog.GetDataSetAsync(Ds);
        var entry = await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "マニューラ"), new StatSet { Attack = 32 }, data.Natures.First(), null);

        if (seed is not null) await seed(entry.Id);   // 画面を開く前に、チームを入れておく

        var states = new PageStateStore();
        var state = states.Get<TeamPage.State>("team");
        state.DataSetKey = Ds;
        state.TeamNumber = 1;
        state.TeamEntryId = entry.Id;

        Services.AddSingleton(catalog);
        Services.AddSingleton(app);
        Services.AddSingleton(states);
        Services.AddSingleton(repo);
        Services.AddSingleton(new CloudSync(JSInterop.JSRuntime, new HttpClient(), "http://localhost/", Services.BuildServiceProvider()));
        Services.AddScoped<ToastService>();

        var cut = Render<TeamPage>();
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("チームに追加")));
        return cut;
    }

    private static async Task ClickAddAsync(IRenderedComponent<TeamPage> cut)
    {
        cut.FindAll("button").First(b => b.TextContent.Trim() == "チームに追加").Click();
        await Task.Delay(50);
    }

    private async Task<RosterTeam?> StoredTeamAsync(BattleFormat format, int number) =>
        (await repo.GetAsync()).For(Ds).FindTeam(format, number);

    private async Task AddTeamAsync(int number, int entryId, BattleFormat format) =>
        await repo.AddToTeamAsync(Ds, format, number, entryId, null);

    /// <summary>チーム一覧の見出し（「チーム1」「チーム2（使用中）」…）。追加フォームの見出しは含めない。</summary>
    private static List<string> TeamHeadings(IRenderedComponent<TeamPage> cut) =>
        cut.FindAll(".card-header h5").Select(h => h.TextContent.Trim())
            .Where(h => System.Text.RegularExpressions.Regex.IsMatch(h, @"^チーム\d+")).ToList();

    private static bool AddButtonDisabled(IRenderedComponent<TeamPage> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "チームに追加").HasAttribute("disabled");

    private static async Task ClickAsync(IRenderedComponent<TeamPage> cut, string text, int index = 0)
    {
        cut.FindAll("button").Where(b => b.TextContent.Trim() == text).ElementAt(index).Click();
        await Task.Delay(50);
    }

    [Fact]
    public async Task The_add_form_has_no_battle_format_field()
    {
        var cut = await OpenAsync(BattleFormat.Singles);

        var form = cut.Find(".card-body");
        Assert.DoesNotContain("バトル形式", form.TextContent);
        Assert.Empty(form.QuerySelectorAll("[aria-label='チームのバトル形式']"));
        Assert.Contains("チーム番号", form.TextContent);
    }

    [Fact]
    public async Task Team_cards_have_no_format_select_or_badge()
    {
        var cut = await OpenAsync(BattleFormat.Singles, id => AddTeamAsync(1, id, BattleFormat.Singles));

        Assert.Single(TeamHeadings(cut));
        Assert.Empty(cut.FindAll(".team-format-select"));
        Assert.Empty(cut.FindAll(".team-format"));
        Assert.Empty(cut.FindAll("[aria-label$='のバトル形式']"));
    }

    [Fact]
    public async Task Only_teams_of_the_header_format_are_listed()
    {
        var cut = await OpenAsync(BattleFormat.Singles, async id =>
        {
            await AddTeamAsync(1, id, BattleFormat.Singles);
            await AddTeamAsync(2, id, BattleFormat.Doubles);
        });

        Assert.Equal(new[] { "チーム1" }, TeamHeadings(cut));
    }

    [Fact]
    public async Task Switching_the_header_changes_the_listed_teams_and_keeps_the_data()
    {
        var cut = await OpenAsync(BattleFormat.Singles, async id =>
        {
            await AddTeamAsync(1, id, BattleFormat.Singles);
            await AddTeamAsync(2, id, BattleFormat.Doubles);
        });

        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム2" }, TeamHeadings(cut)));
        await app.SetFormatAsync(BattleFormat.Singles);
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム1" }, TeamHeadings(cut)));

        Assert.Equal(BattleFormat.Singles, (await StoredTeamAsync(BattleFormat.Singles, 1))!.BattleFormat);
        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(BattleFormat.Doubles, 2))!.BattleFormat);
        Assert.Single((await StoredTeamAsync(BattleFormat.Doubles, 2))!.Slots);
    }

    [Fact]
    public async Task The_same_number_in_both_formats_is_listed_per_format()
    {
        var cut = await OpenAsync(BattleFormat.Singles, async id =>
        {
            await AddTeamAsync(1, id, BattleFormat.Singles);
            await AddTeamAsync(1, id, BattleFormat.Doubles);
            await AddTeamAsync(2, id, BattleFormat.Doubles);
        });

        Assert.Equal(new[] { "チーム1" }, TeamHeadings(cut));
        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム1", "チーム2" }, TeamHeadings(cut)));
    }

    [Fact]
    public async Task An_empty_format_shows_a_message_and_other_teams_stay()
    {
        var cut = await OpenAsync(BattleFormat.Doubles, id => AddTeamAsync(1, id, BattleFormat.Singles));

        Assert.Empty(TeamHeadings(cut));
        Assert.Contains("ダブルのチームはまだありません", cut.Markup);
        Assert.NotNull(await StoredTeamAsync(BattleFormat.Singles, 1));
    }

    [Fact]
    public async Task A_number_used_by_another_format_can_be_added_and_the_other_team_is_untouched()
    {
        var cut = await OpenAsync(BattleFormat.Singles, id => AddTeamAsync(1, id, BattleFormat.Doubles));   // 入力欄の番号は 1

        Assert.False(AddButtonDisabled(cut));
        Assert.Empty(cut.FindAll(".team-number-conflict"));
        Assert.DoesNotContain("他の形式", cut.Markup);
        await ClickAddAsync(cut);

        // シングルのチーム1 が新しくできて、ダブルのチーム1 はそのまま
        Assert.Equal(BattleFormat.Singles, (await StoredTeamAsync(BattleFormat.Singles, 1))!.BattleFormat);
        Assert.Single((await StoredTeamAsync(BattleFormat.Singles, 1))!.Slots);
        Assert.Single((await StoredTeamAsync(BattleFormat.Doubles, 1))!.Slots);
        Assert.Equal(2, (await repo.GetAsync()).For(Ds).Teams.Count);
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム1" }, TeamHeadings(cut)));
    }

    [Fact]
    public async Task A_number_not_used_by_another_format_can_be_added()
    {
        var cut = await OpenAsync(BattleFormat.Singles);

        Assert.False(AddButtonDisabled(cut));
        Assert.Empty(cut.FindAll(".team-number-conflict"));
    }

    [Fact]
    public async Task The_active_team_is_per_format_and_follows_the_header()
    {
        var cut = await OpenAsync(BattleFormat.Singles, async id =>
        {
            await AddTeamAsync(1, id, BattleFormat.Singles);
            await AddTeamAsync(1, id, BattleFormat.Doubles);
            await AddTeamAsync(2, id, BattleFormat.Doubles);
            await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 2);
        });

        // シングルには使用中が無い（ダブルのチーム2 が使用中でも、他の形式の注意文は出ない）
        Assert.Equal(new[] { "チーム1" }, TeamHeadings(cut));
        Assert.Empty(cut.FindAll(".team-active-hidden"));
        Assert.DoesNotContain("使用中のチーム", cut.Markup);

        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム1", "チーム2 （使用中）" }, TeamHeadings(cut)));
        Assert.Empty(cut.FindAll(".team-active-hidden"));
    }

    [Fact]
    public async Task Using_a_team_changes_only_the_header_format_and_unusing_does_too()
    {
        var cut = await OpenAsync(BattleFormat.Doubles, async id =>
        {
            await AddTeamAsync(1, id, BattleFormat.Singles);
            await AddTeamAsync(1, id, BattleFormat.Doubles);
            await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        });

        await ClickAsync(cut, "このチームを使用");
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Doubles));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));   // シングルの使用中はそのまま
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム1 （使用中）" }, TeamHeadings(cut)));

        await ClickAsync(cut, "使用を解除");
        roster = (await repo.GetAsync()).For(Ds);
        Assert.Null(roster.ActiveNumber(BattleFormat.Doubles));
        Assert.Equal(1, roster.ActiveNumber(BattleFormat.Singles));   // シングルの使用中は残る
    }

    [Fact]
    public void The_page_help_says_numbers_are_separate_per_format()
    {
        // ヘルプの吹き出しは押すまで描かれないので、ページのソースで確かめる
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "FaraPokemonBattleAssistant.Web", "Components", "Pages", "Team.razor"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "FaraPokemonBattleAssistant.Web", "Components", "Pages", "Team.razor"));
        Assert.Contains("シングルとダブルで別々", source);
        Assert.DoesNotContain("シングルとダブルで共通", source);
        Assert.DoesNotContain("追加できません", source);
    }

    [Fact]
    public async Task A_new_team_gets_the_header_format_doubles()
    {
        var cut = await OpenAsync(BattleFormat.Doubles);
        await ClickAddAsync(cut);

        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(BattleFormat.Doubles, 1))!.BattleFormat);
    }

    [Fact]
    public async Task A_new_team_gets_the_header_format_singles()
    {
        var cut = await OpenAsync(BattleFormat.Singles);
        await ClickAddAsync(cut);

        Assert.Equal(BattleFormat.Singles, (await StoredTeamAsync(BattleFormat.Singles, 1))!.BattleFormat);
    }

    [Fact]
    public async Task Changing_the_header_then_adding_uses_the_changed_format()
    {
        var cut = await OpenAsync(BattleFormat.Singles);
        await app.SetFormatAsync(BattleFormat.Doubles);   // ヘッダーのセレクトが呼ぶもの
        await ClickAddAsync(cut);

        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(BattleFormat.Doubles, 1))!.BattleFormat);
    }

    [Fact]
    public async Task Adding_to_an_existing_team_of_the_header_format_keeps_its_format_and_other_teams()
    {
        var cut = await OpenAsync(BattleFormat.Doubles, async id =>
        {
            await AddTeamAsync(1, id, BattleFormat.Doubles);
            await AddTeamAsync(2, id, BattleFormat.Singles);
        });
        await ClickAddAsync(cut);   // 同じ登録 ID をもう一度（持ち物の更新）

        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(BattleFormat.Doubles, 1))!.BattleFormat);
        Assert.Equal(BattleFormat.Singles, (await StoredTeamAsync(BattleFormat.Singles, 2))!.BattleFormat);
    }
}
