using System.Text.RegularExpressions;
using Bunit;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Web.Components.Pages;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// ダメージ計算・素早さ比較の「登録チームから選ぶ」: 候補は、ヘッダーのバトル形式の登録チームのメンバーだけ（チームごとのグループ、使用中が先頭）。
/// チームに入っていない育成ポケモンは出さない。その形式のチームが無ければ、自分チームでの作成を案内する。
/// </summary>
public class RosterPickerTests : BunitContext, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor のサービスは非同期の破棄だけなので、非同期で片付ける
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private const string Ds = "Champions";
    private const string SinglesOnly = "マニューラ";
    private const string DoublesOnly = "ガブリアス";
    private const string Both = "リザードン";
    private const string Unassigned = "ピカチュウ";
    private const string Placeholder = "input[placeholder='登録チームから選ぶ...']";
    private const string Guide = "この形式の登録チームがありません";

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

    /// <summary>
    /// シングル: チーム1（使用中）= マニューラ・リザードン、チーム3 = リザードン。ダブル: チーム1（使用中）= ガブリアス、チーム2 = リザードン。
    /// どのチームにも入っていないピカチュウも登録する。<paramref name="withTeams"/> が false なら、チームは作らず育成ポケモンだけ。
    /// </summary>
    private async Task SeedAsync(PokemonDataSet data, bool withTeams)
    {
        async Task<RegisteredPokemon> Add(string name) =>
            await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == name), new StatSet { Attack = 32 }, data.Natures.First(), null);
        var weavile = await Add(SinglesOnly);
        var garchomp = await Add(DoublesOnly);
        var charizard = await Add(Both);
        await Add(Unassigned);
        if (!withTeams) return;
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 3, charizard.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, weavile.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Singles, 1, charizard.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 1);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 2, charizard.Id, null);
        await repo.AddToTeamAsync(Ds, BattleFormat.Doubles, 1, garchomp.Id, null);
        await repo.UseTeamAsync(Ds, BattleFormat.Doubles, 1);
    }

    private async Task<IRenderedComponent<TPage>> OpenAsync<TPage>(BattleFormat headerFormat, bool withTeams, Action<PageStateStore> state)
        where TPage : IComponent
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var catalog = new DataCatalog(new FileSource(), null);
        app = new AppState(catalog, JSInterop.JSRuntime);
        await app.InitializeAsync();
        await app.SetDataSetAsync(Ds);
        await app.SetFormatAsync(headerFormat);
        await SeedAsync(await catalog.GetDataSetAsync(Ds), withTeams);

        var states = new PageStateStore();
        state(states);
        Services.AddSingleton(catalog);
        Services.AddSingleton(app);
        Services.AddSingleton(states);
        Services.AddSingleton(repo);
        Services.AddSingleton<BattleStage>();
        Services.AddSingleton(new CloudSync(JSInterop.JSRuntime, new HttpClient(), "http://localhost/", Services.BuildServiceProvider()));
        Services.AddScoped<ToastService>();

        var cut = Render<TPage>();
        cut.WaitForState(() => cut.Markup.Contains("ポケモン"));
        cut.WaitForState(() => cut.FindAll(Placeholder).Count > 0 || cut.Markup.Contains(Guide));   // 登録の読み込みが終わるまで待つ
        return cut;
    }

    private Task<IRenderedComponent<Speed>> OpenSpeedAsync(BattleFormat format, bool withTeams = true) =>
        OpenAsync<Speed>(format, withTeams, states =>
        {
            var s = states.Get<Speed.State>("speed3");
            s.DataSetKey = Ds;
            s.UseRoster[0] = true;
        });

    private Task<IRenderedComponent<DamageCalculator>> OpenDamageAsync(BattleFormat format, bool withTeams = true) =>
        OpenAsync<DamageCalculator>(format, withTeams, states =>
        {
            var s = states.Get<DamageCalculator.State>("damage");
            s.DataSetKey = Ds;
            s.UseRosterAttacker = true;
        });

    /// <summary>「登録チームから選ぶ」の入力欄を開き、候補を表示順に返す（各候補は「#ID 名前」）。チームの見出しは、候補を返す一覧には含めない（<see cref="Headings"/>）。</summary>
    private static List<string> Candidates<T>(IRenderedComponent<T> cut) where T : IComponent => Rows(cut).Where(r => !r.Heading).Select(r => r.Text).ToList();

    /// <summary>開いた一覧のチームの見出し（「チーム1（使用中）」など）を表示順に返す。</summary>
    private static List<string> Headings<T>(IRenderedComponent<T> cut) where T : IComponent => Rows(cut).Where(r => r.Heading).Select(r => r.Text).ToList();

    /// <summary>一覧を開いて、見出しと候補を表示順に返す（見出しは <c>role=presentation</c>、候補は <c>role=option</c>）。</summary>
    private static List<(bool Heading, string Text)> Rows<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        cut.Find(Placeholder).Click();
        cut.WaitForState(() => cut.FindAll("[role=listbox] [role=option]").Count > 0);
        var items = cut.FindAll("[role=listbox] > *").Select(o => (o.GetAttribute("role") == "presentation", o.TextContent.Trim())).ToList();
        cut.Find(".searchable-select").FocusOut();   // 閉じる（次に読むときに開き直して、最新の候補を読む）
        return items;
    }

    private static bool Has(List<string> items, string name) => items.Any(i => i.Contains(name));

    [Fact]
    public async Task Speed_singles_header_lists_only_members_of_the_singles_teams()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Singles);

        var items = Candidates(cut);
        Assert.Equal(3, items.Count);   // 使用中のチーム1（2 体）+ チーム3（1 体）
        Assert.True(Has(items, SinglesOnly));
        Assert.True(Has(items, Both));
        Assert.False(Has(items, DoublesOnly));   // ダブルのチームだけのポケモンは出ない
        Assert.False(Has(items, Unassigned));    // チームに入っていない育成ポケモンは出ない
    }

    [Fact]
    public async Task Speed_doubles_header_lists_only_members_of_the_doubles_teams()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Doubles);

        var items = Candidates(cut);
        Assert.Equal(2, items.Count);   // 使用中のチーム1（1 体）+ チーム2（1 体）
        Assert.True(Has(items, DoublesOnly));
        Assert.True(Has(items, Both));
        Assert.False(Has(items, SinglesOnly));
        Assert.False(Has(items, Unassigned));
    }

    [Fact]
    public async Task Speed_switching_the_header_changes_the_candidates()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Singles);
        Assert.True(Has(Candidates(cut), SinglesOnly));

        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() =>
        {
            var items = Candidates(cut);
            Assert.True(Has(items, DoublesOnly));
            Assert.False(Has(items, SinglesOnly));
            Assert.False(Has(items, Unassigned));
        });
    }

    [Fact]
    public async Task Candidates_are_grouped_under_team_headings_with_the_active_team_first()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Singles);

        // 使用中のチーム1（2 体）が先頭、そのあとにチーム3。見出しは、使用中のチームだけ「（使用中）」つき
        Assert.Equal(new[] { "チーム1（使用中）", "チーム3" }, Headings(cut));
        var rows = Rows(cut).Select(r => r.Heading ? $"[{r.Text}]" : r.Text).ToList();
        Assert.Equal(new[] { "[チーム1（使用中）]", "#1 マニューラ", "#3 リザードン", "[チーム3]", "#3 リザードン" }, rows);

        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム1（使用中）", "チーム2" }, Headings(cut)));
    }

    [Fact]
    public async Task Active_team_comes_first_even_when_its_number_is_larger()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Singles);
        await repo.UseTeamAsync(Ds, BattleFormat.Singles, 3);
        await app.SetFormatAsync(BattleFormat.Doubles);
        await app.SetFormatAsync(BattleFormat.Singles);

        cut.WaitForAssertion(() => Assert.Equal(new[] { "チーム3（使用中）", "チーム1" }, Headings(cut)));
    }

    [Fact]
    public async Task Speed_without_teams_in_the_format_shows_the_guide_instead_of_the_picker()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Singles, withTeams: false);

        Assert.Single(Regex.Matches(cut.Markup, Guide));   // 左右の入力欄ごとではなく、ページに 1 回だけ
        Assert.Contains("自分チーム", cut.Markup);
        Assert.Empty(cut.FindAll(Placeholder));
        Assert.DoesNotContain(Unassigned, cut.Markup);   // チームに入っていない育成ポケモンは出ない
    }

    [Fact]
    public async Task Speed_shows_the_guide_when_only_the_other_format_has_teams()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Singles);
        Assert.DoesNotContain(Guide, cut.Markup);   // シングルにはチームがあるので案内は出ない

        await repo.RemoveTeamAsync(Ds, BattleFormat.Doubles, 1);
        await repo.RemoveTeamAsync(Ds, BattleFormat.Doubles, 2);
        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() => Assert.Contains(Guide, cut.Markup));
        Assert.Empty(cut.FindAll(Placeholder));
    }

    [Fact]
    public async Task Damage_calculator_lists_only_members_of_the_header_format_teams()
    {
        var cut = await OpenDamageAsync(BattleFormat.Singles);
        var singles = Candidates(cut);
        Assert.Equal(3, singles.Count);
        Assert.True(Has(singles, SinglesOnly));
        Assert.True(Has(singles, Both));
        Assert.False(Has(singles, DoublesOnly));
        Assert.False(Has(singles, Unassigned));
        Assert.Equal(new[] { "チーム1（使用中）", "チーム3" }, Headings(cut));

        await app.SetFormatAsync(BattleFormat.Doubles);
        cut.WaitForAssertion(() =>
        {
            var doubles = Candidates(cut);
            Assert.Equal(2, doubles.Count);
            Assert.True(Has(doubles, DoublesOnly));
            Assert.False(Has(doubles, SinglesOnly));
            Assert.False(Has(doubles, Unassigned));
        });
    }

    [Fact]
    public async Task Damage_calculator_without_teams_shows_the_guide()
    {
        var cut = await OpenDamageAsync(BattleFormat.Doubles, withTeams: false);

        Assert.Single(Regex.Matches(cut.Markup, Guide));   // 攻撃側・防御側ごとではなく、ページに 1 回だけ
        Assert.Empty(cut.FindAll(Placeholder));
        Assert.DoesNotContain(Unassigned, cut.Markup);
    }

    [Fact]
    public async Task The_switch_and_help_say_pick_from_registered_teams()
    {
        var cut = await OpenSpeedAsync(BattleFormat.Singles);

        Assert.Contains("登録チームから選ぶ", cut.Markup);
        Assert.DoesNotContain("「登録から選ぶ」", cut.Markup);
        Assert.DoesNotContain(">登録から選ぶ<", cut.Markup);
    }
}
