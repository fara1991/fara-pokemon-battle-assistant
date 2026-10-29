using System.Net;
using FaraPokemonBattleAssistant.Web.Services;
using FaraBlazorComponent.Core;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>メンテナンスの状態（Worker の変数 MAINTENANCE_MODE / MAINTENANCE_MESSAGE）の読み取り。読めなければ通常として扱う。</summary>
public class MaintenanceInfoTests
{
    [Theory]
    [InlineData("{\"state\":\"off\",\"message\":\"\"}", MaintenanceState.Off)]
    [InlineData("{\"state\":\"notice\",\"message\":\"20:00ごろ\"}", MaintenanceState.Notice)]
    [InlineData("{\"state\":\"on\",\"message\":\"20:00ごろ\"}", MaintenanceState.On)]
    [InlineData("{\"state\":\"ON\"}", MaintenanceState.On)]
    [InlineData("{\"state\":\" notice \"}", MaintenanceState.Notice)]
    public void The_state_is_read_from_the_json(string json, MaintenanceState expected) =>
        Assert.Equal(expected, MaintenanceInfo.Parse(json).State);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<!doctype html><title>app</title>")]   // 開発中など、Worker が無くて index.html が返ってきたとき
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"state\":5}")]
    [InlineData("{\"state\":\"maintenance\"}")]
    [InlineData("{\"state\":null}")]
    public void Anything_unreadable_is_treated_as_normal(string? json) => Assert.Equal(MaintenanceInfo.Off, MaintenanceInfo.Parse(json));

    [Fact]
    public void The_message_is_kept_only_while_a_notice_or_maintenance_is_on()
    {
        Assert.Equal("20:00ごろ", MaintenanceInfo.Parse("{\"state\":\"notice\",\"message\":\"  20:00ごろ \"}").Message);
        Assert.Equal("20:00ごろ", MaintenanceInfo.Parse("{\"state\":\"on\",\"message\":\"20:00ごろ\"}").Message);
        Assert.Equal("", MaintenanceInfo.Parse("{\"state\":\"off\",\"message\":\"20:00ごろ\"}").Message);   // 通常に戻したら、お知らせも消える
        Assert.Equal("", MaintenanceInfo.Parse("{\"state\":\"notice\",\"message\":5}").Message);
        Assert.Equal("", MaintenanceInfo.Parse("{\"state\":\"notice\"}").Message);
    }

    [Fact]
    public void The_notice_text_falls_back_to_a_default()
    {
        Assert.Equal("まもなくメンテナンスを行います。", new MaintenanceInfo(MaintenanceState.Notice, "").NoticeText);
        Assert.Equal("まもなくメンテナンスを行います。", new MaintenanceInfo(MaintenanceState.Notice, "  ").NoticeText);
        Assert.Equal("10月25日 20:00ごろ", new MaintenanceInfo(MaintenanceState.Notice, "10月25日 20:00ごろ").NoticeText);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        public string? RequestedPath { get; private set; }
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => this.respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPath = request.RequestUri!.AbsolutePath;
            return respond(request, cancellationToken);
        }
    }

    private static HttpClient Client(FakeHandler handler) => new(handler) { BaseAddress = new Uri("https://pokemon.example/") };

    [Fact]
    public async Task Load_reads_the_state_from_the_maintenance_endpoint()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"on\",\"message\":\"20:00ごろ\"}") }));
        using var client = Client(handler);
        var info = await MaintenanceInfo.LoadAsync(client);
        Assert.Equal(new MaintenanceInfo(MaintenanceState.On, "20:00ごろ"), info);
        Assert.Equal("/api/maintenance", handler.RequestedPath);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Load_treats_an_error_response_as_normal(HttpStatusCode status)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"state\":\"on\"}") }));
        using var client = Client(handler);
        Assert.Equal(MaintenanceInfo.Off, await MaintenanceInfo.LoadAsync(client));
    }

    [Fact]
    public async Task Load_treats_a_network_failure_as_normal()
    {
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException("offline"));
        using var client = Client(handler);
        Assert.Equal(MaintenanceInfo.Off, await MaintenanceInfo.LoadAsync(client));
    }

    [Fact]
    public async Task Load_treats_an_unexpected_exception_as_normal()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("unexpected"));
        using var client = Client(handler);
        Assert.Equal(MaintenanceInfo.Off, await MaintenanceInfo.LoadAsync(client));
    }

    [Fact]
    public async Task Load_gives_up_quickly_by_default()
    {
        using var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"on\"}") };
        });
        using var client = Client(handler);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(MaintenanceInfo.Off, await MaintenanceInfo.LoadAsync(client));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "既定の待ち時間は 2 秒（起動を長く止めない）");
    }

    [Fact]
    public async Task Load_does_not_make_the_app_wait_for_a_slow_endpoint()
    {
        using var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);   // キャンセルされるまで待つ
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"on\"}") };
        });
        using var client = Client(handler);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var info = await MaintenanceInfo.LoadAsync(client, TimeSpan.FromMilliseconds(100));
        Assert.Equal(MaintenanceInfo.Off, info);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "時間切れで、通常として起動する");
    }

    [Fact]
    public void The_shipped_wrangler_config_keeps_dashboard_variables()
    {
        // 公開（wrangler deploy）のたびに、ダッシュボードの MAINTENANCE_MODE が消えないように。keep_vars はトップレベルだけが有効
        // （env の中に書くと、wrangler が「知らない項目」と警告する。本番・確認用の両方に、トップレベルの値が効く）
        var path = FindUp("wrangler.jsonc");
        var text = File.ReadAllText(path);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"keep_vars\"\\s*:\\s*true"));
        var envStart = text.IndexOf("\"env\"", StringComparison.Ordinal);
        Assert.True(envStart > 0 && text.IndexOf("keep_vars", StringComparison.Ordinal) < envStart, "トップレベルにある（env の中ではない）");
    }

    [Fact]
    public void Game_data_goes_through_the_worker_so_it_can_be_stopped_during_maintenance()
    {
        // データの更新をメンテナンス中に行えるように、/api/game/* も Worker を通す（通常はそのまま返し、メンテナンス中だけ 503）
        var text = File.ReadAllText(FindUp("wrangler.jsonc"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, @"""run_worker_first"": \[""/"", ""/home"", ""/home\.html"", ""/home/"", ""/api/\*""\]").Count);
        Assert.DoesNotContain("!/api/game/*", text);
        var js = File.ReadAllText(FindUp("worker/index.js"));
        var maintenance = js.IndexOf("maintenance.state === \"on\" && url.pathname.startsWith(\"/api/\")", StringComparison.Ordinal);
        var gameFallback = js.IndexOf("url.pathname.startsWith(\"/api/game/\")", StringComparison.Ordinal);
        Assert.True(maintenance > 0 && gameFallback > maintenance, "メンテナンスの確認が、ゲームデータをそのまま返す処理より先にある");
    }

    [Fact]
    public void Production_deploy_requires_MAINTENANCE_MODE_notice_and_turns_maintenance_on()
    {
        var yml = File.ReadAllText(FindUp(".github/workflows/deploy.yml")).Replace("\r\n", "\n");
        Assert.Contains("node .github/scripts/require-maintenance.mjs fara-pokemon", yml);
        Assert.True(yml.IndexOf("require-maintenance.mjs", StringComparison.Ordinal) < yml.IndexOf("npx wrangler deploy", StringComparison.Ordinal), "確認は公開より前");
        // 公開と同時のメンテナンス化は、push と何も指定しない手動実行だけ（分岐は deploy-workflow.test.mjs が bash で実行して固定）
        Assert.Contains("vars+=(--var \"MAINTENANCE_MODE:true\")", yml);
        Assert.Contains("vars+=(--var \"MAINTENANCE_MODE:$MAINTENANCE_MODE_INPUT\")", yml);
        Assert.Contains("[ -z \"$MAINTENANCE_MESSAGE_INPUT\" ] && [ \"$SKIP_MAINTENANCE_CHECK\" != \"true\" ]", yml);
        Assert.Contains("inputs.skip_maintenance_check", yml);
        Assert.Contains("skip_maintenance_check:", yml);
        // mode を指定した手動実行は、確認（ゲート）を飛ばす
        Assert.Contains("if: ${{ !contains(fromJSON('[\"true\",\"notice\",\"false\"]'), inputs.maintenance_mode) }}", yml);
        Assert.Contains("maintenance_mode:", yml);
        Assert.Contains("maintenance_message:", yml);
        Assert.Contains("name: Summary (maintenance)", yml);
        // 旧名 MAINTENANCE は使わない
        Assert.DoesNotMatch(@"(?<![_A-Za-z])MAINTENANCE(?![_A-Za-z])", yml);
        // 確認用（develop）の公開は、確認も自動のメンテナンス化もしない（指定した入力だけを渡す）
        var develop = File.ReadAllText(FindUp(".github/workflows/deploy-develop.yml")).Replace("\r\n", "\n");
        Assert.DoesNotContain("require-maintenance", develop);
        Assert.DoesNotContain("MAINTENANCE_MODE:true", develop);
        Assert.Contains("vars+=(--var \"MAINTENANCE_MODE:$MAINTENANCE_MODE_INPUT\")", develop);
        Assert.Contains("vars+=(--var \"MAINTENANCE_MESSAGE:$MAINTENANCE_MESSAGE_INPUT\")", develop);
        Assert.Contains("name: Summary (maintenance)", develop);
    }

    [Fact]
    public void Gate_script_is_tested_in_ci()
    {
        Assert.True(File.Exists(FindUp(".github/scripts/require-maintenance.mjs")));
        Assert.Contains("test:gate", File.ReadAllText(FindUp(".github/workflows/build.yml")));
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
