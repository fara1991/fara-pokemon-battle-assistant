using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using FaraPokemonBattleAssistant.Web;
using FaraPokemonBattleAssistant.Web.Services;
using FaraSiteKit.Core;
using FaraSiteKit.Razor;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
var baseAddress = builder.HostEnvironment.BaseAddress;

// 共通部品（fara-site-kit の Razor。ヘッダー・フッター・テーマ・報告フォームなど）の設定・テーマの状態・MudBlazor（通知の位置など）。
// メンテナンス画面（下）も SiteOptions を読むので、メンテナンスの分岐より前に登録する。値は Services/SiteInfo.cs
builder.Services.AddFaraSiteKit(SiteInfo.Configure);

// メンテナンスの状態（Worker の変数 MAINTENANCE。external/fara-site-kit/worker/maintenance.js）。読めなければ通常。
// メンテナンス中（MAINTENANCE=true）は、アプリの代わりに、メンテナンス画面だけを動かす
var maintenance = await MaintenanceInfo.LoadAsync(new HttpClient { BaseAddress = new Uri(baseAddress) });
builder.Services.AddSingleton(maintenance);
if (maintenance.State == MaintenanceState.On)
{
    builder.RootComponents.Add<MaintenancePage>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");
    await builder.Build().RunAsync();
    return;
}
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// シングルトンの DataCatalog から使うので、HttpClient もシングルトンにする
// （Scoped だと Development 環境のスコープ検証で起動時に例外になる）
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri(baseAddress) });
// ゲームデータ（CSV）は同じオリジンの /api/game/...（wwwroot/api/game の静的アセット。Worker を通して、そのまま返す。メンテナンス中だけ 503）
// 使用率（usage_*.csv）は、代用の仕組み（UsageFallback）で別のゲームの使用率を読むときも同じファイルを 1 回だけ取る（Services/SharedUsageDataSource.cs）
builder.Services.AddSingleton(sp => new DataCatalog(new SharedUsageDataSource(new HttpDataSource(sp.GetRequiredService<HttpClient>(), baseAddress + HttpDataSource.GameDataPath))));
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

await builder.Build().RunAsync();
