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

    private async Task<RosterTeam?> StoredTeamAsync(int number) =>
        (await repo.GetAsync()).For(Ds).Teams.FirstOrDefault(t => t.Number == number);

    private async Task AddTeamAsync(int number, int entryId, BattleFormat format) =>
        await repo.AddToTeamAsync(Ds, number, entryId, null, format);

    /// <summary>チーム一覧の見出し（「チーム1」「チーム2（使用中）」…）。追加フォームの見出しは含めない。</summary>
    private static List<string> TeamHeadings(IRenderedComponent<TeamPage> cut) =>
        cut.FindAll(".card-header h5").Select(h => h.TextContent.Trim())
            .Where(h => System.Text.RegularExpressions.Regex.IsMatch(h, @"^チーム\d+")).ToList();

    private static bool AddButtonDisabled(IRenderedComponent<TeamPage> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "チームに追加").HasAttribute("disabled");

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

        Assert.Equal(BattleFormat.Singles, (await StoredTeamAsync(1))!.BattleFormat);
        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(2))!.BattleFormat);
        Assert.Single((await StoredTeamAsync(2))!.Slots);
    }

    [Fact]
    public async Task An_empty_format_shows_a_message_and_other_teams_stay()
    {
        var cut = await OpenAsync(BattleFormat.Doubles, id => AddTeamAsync(1, id, BattleFormat.Singles));

        Assert.Empty(TeamHeadings(cut));
        Assert.Contains("ダブルのチームはまだありません", cut.Markup);
        Assert.NotNull(await StoredTeamAsync(1));
    }

    [Fact]
    public async Task A_number_used_by_another_format_cannot_be_added_to_and_the_team_is_untouched()
    {
        var cut = await OpenAsync(BattleFormat.Singles, id => AddTeamAsync(1, id, BattleFormat.Doubles));   // 入力欄の番号は 1

        Assert.True(AddButtonDisabled(cut));
        Assert.Contains("チーム1 はダブルのチームです", cut.Find(".team-number-conflict").TextContent);
        await ClickAddAsync(cut);

        var stored = (await StoredTeamAsync(1))!;
        Assert.Equal(BattleFormat.Doubles, stored.BattleFormat);
        Assert.Single(stored.Slots);
    }

    [Fact]
    public async Task A_number_not_used_by_another_format_can_be_added()
    {
        var cut = await OpenAsync(BattleFormat.Singles);

        Assert.False(AddButtonDisabled(cut));
        Assert.Empty(cut.FindAll(".team-number-conflict"));
    }

    [Fact]
    public async Task An_active_team_of_another_format_is_not_listed_but_stays_active_and_is_explained()
    {
        var cut = await OpenAsync(BattleFormat.Singles, async id =>
        {
            await AddTeamAsync(2, id, BattleFormat.Doubles);
            await repo.UseTeamAsync(Ds, 2);
        });

        Assert.Empty(TeamHeadings(cut));
        Assert.Contains("使用中のチーム2 はダブルのチームです", cut.Find(".team-active-hidden").TextContent);
        Assert.Equal(2, (await repo.GetAsync()).For(Ds).ActiveTeam);

        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム2 （使用中）" }, TeamHeadings(cut)));
        Assert.Empty(cut.FindAll(".team-active-hidden"));
    }

    [Fact]
    public async Task A_new_team_gets_the_header_format_doubles()
    {
        var cut = await OpenAsync(BattleFormat.Doubles);
        await ClickAddAsync(cut);

        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(1))!.BattleFormat);
    }

    [Fact]
    public async Task A_new_team_gets_the_header_format_singles()
    {
        var cut = await OpenAsync(BattleFormat.Singles);
        await ClickAddAsync(cut);

        Assert.Equal(BattleFormat.Singles, (await StoredTeamAsync(1))!.BattleFormat);
    }

    [Fact]
    public async Task Changing_the_header_then_adding_uses_the_changed_format()
    {
        var cut = await OpenAsync(BattleFormat.Singles);
        await app.SetFormatAsync(BattleFormat.Doubles);   // ヘッダーのセレクトが呼ぶもの
        await ClickAddAsync(cut);

        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(1))!.BattleFormat);
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

        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(1))!.BattleFormat);
        Assert.Equal(BattleFormat.Singles, (await StoredTeamAsync(2))!.BattleFormat);
    }
}
