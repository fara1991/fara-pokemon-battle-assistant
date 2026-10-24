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
/// 自分チームのページ: 追加フォームにバトル形式の欄は無く（ヘッダーだけ）、新しいチームはヘッダーで選んでいる形式になる。
/// 既にあるチームの形式は、追加しても変わらない（チーム一覧のチームごとの選択で変える）。
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
    private async Task<IRenderedComponent<TeamPage>> OpenAsync(BattleFormat headerFormat)
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
    public async Task Adding_to_an_existing_team_keeps_its_format()
    {
        var cut = await OpenAsync(BattleFormat.Singles);
        await ClickAddAsync(cut);   // チーム 1 をシングルで作る
        Assert.True(await repo.SetTeamFormatAsync(Ds, 1, BattleFormat.Doubles));
        await app.SetFormatAsync(BattleFormat.Singles);
        await ClickAddAsync(cut);   // 同じ登録 ID をもう一度（持ち物の更新）

        Assert.Equal(BattleFormat.Doubles, (await StoredTeamAsync(1))!.BattleFormat);
    }
}
