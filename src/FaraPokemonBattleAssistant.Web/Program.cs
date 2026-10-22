using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using FaraPokemonBattleAssistant.Web;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
var baseAddress = builder.HostEnvironment.BaseAddress;

// メンテナンスの状態（Worker の変数 MAINTENANCE。external/fara-site-kit/worker/maintenance.js）。読めなければ通常。
// メンテナンス中（MAINTENANCE=true）は、アプリの代わりに、メンテナンス画面だけを動かす
var maintenance = await MaintenanceInfo.LoadAsync(new HttpClient { BaseAddress = new Uri(baseAddress) });
builder.Services.AddSingleton(maintenance);
if (maintenance.State == MaintenanceState.On)
{
    builder.RootComponents.Add<MaintenanceScreen>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");
    await builder.Build().RunAsync();
    return;
}
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// 確認用サイトで開いているときだけ fara-labs.com へのリンクを確認用に切り替える（ホスト名は実行時に決まる）
// 確認用のホスト名はこのサイトの値（kit の SiteLinks は既定では切り替えない）
FaraPokemonBattleAssistant.Core.SiteLinks.DevelopHost = "pokemon-dev.fara-labs.com";
FaraPokemonBattleAssistant.Core.SiteLinks.CurrentHost = new Uri(baseAddress).Host;
// シングルトンの DataCatalog から使うので、HttpClient もシングルトンにする
// （Scoped だと Development 環境のスコープ検証で起動時に例外になる）
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri(baseAddress) });
// ゲームデータ（CSV）は同じオリジンの /api/game/...（wwwroot/api/game の静的アセット。Worker を通して、そのまま返す。メンテナンス中だけ 503）
builder.Services.AddSingleton(sp => new DataCatalog(new HttpDataSource(sp.GetRequiredService<HttpClient>(), baseAddress + HttpDataSource.GameDataPath)));
// 登録データの保存先。アクセスキーがあればクラウド（同じオリジンの /api/my/pokemon・/api/my/team）、無ければ localStorage
builder.Services.AddSingleton(sp => new CloudSync(sp.GetRequiredService<IJSRuntime>(), sp.GetRequiredService<HttpClient>(), baseAddress, sp));
builder.Services.AddSingleton<IRosterStore>(sp => sp.GetRequiredService<CloudSync>());
builder.Services.AddSingleton(sp => new RosterRepository(sp.GetRequiredService<IRosterStore>()));
builder.Services.AddSingleton(sp => new DamageCommand(sp.GetRequiredService<DataCatalog>()));
builder.Services.AddSingleton(sp => new PokeCommand(sp.GetRequiredService<DataCatalog>(), sp.GetRequiredService<RosterRepository>()));
builder.Services.AddSingleton(sp => new AppState(sp.GetRequiredService<DataCatalog>(), sp.GetRequiredService<IJSRuntime>()));
builder.Services.AddSingleton<PageStateStore>();
builder.Services.AddSingleton<Changelog>();
builder.Services.AddSingleton<BattleStage>();
// MudBlazor の ISnackbar（スコープ付き）を使うので、ToastService もスコープ付き（WebAssembly では、アプリの間ずっと 1 つ）
builder.Services.AddScoped<ToastService>();
builder.Services.AddSingleton(sp => new SearchService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<DataCatalog>(), sp.GetRequiredService<AppState>()));

builder.Services.AddMudServices(config =>
{
    // 通知（ToastService）: 画面の右上に、同時に 5 つまで。閉じるボタン付き
    config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.TopRight;
    config.SnackbarConfiguration.MaxDisplayedSnackbars = 5;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.PreventDuplicates = false;
    config.SnackbarConfiguration.SnackbarVariant = MudBlazor.Variant.Filled;
});

await builder.Build().RunAsync();
