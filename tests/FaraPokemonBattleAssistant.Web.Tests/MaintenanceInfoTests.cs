using System.Net;
using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>メンテナンスの状態（Worker の変数 MAINTENANCE / MAINTENANCE_MESSAGE）の読み取り。読めなければ通常として扱う。</summary>
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
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"on\",\"message\":\"20:00ごろ\"}") }));
        var info = await MaintenanceInfo.LoadAsync(Client(handler));
        Assert.Equal(new MaintenanceInfo(MaintenanceState.On, "20:00ごろ"), info);
        Assert.Equal("/api/maintenance", handler.RequestedPath);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Load_treats_an_error_response_as_normal(HttpStatusCode status)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"state\":\"on\"}") }));
        Assert.Equal(MaintenanceInfo.Off, await MaintenanceInfo.LoadAsync(Client(handler)));
    }

    [Fact]
    public async Task Load_treats_a_network_failure_as_normal()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("offline"));
        Assert.Equal(MaintenanceInfo.Off, await MaintenanceInfo.LoadAsync(Client(handler)));
    }

    [Fact]
    public async Task Load_does_not_make_the_app_wait_for_a_slow_endpoint()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);   // キャンセルされるまで待つ
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"on\"}") };
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var info = await MaintenanceInfo.LoadAsync(Client(handler), TimeSpan.FromMilliseconds(100));
        Assert.Equal(MaintenanceInfo.Off, info);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "時間切れで、通常として起動する");
    }

    [Fact]
    public void The_shipped_wrangler_config_keeps_dashboard_variables()
    {
        // 公開（wrangler deploy）のたびに、ダッシュボードの MAINTENANCE が消えないように（本番・確認用の両方）
        var path = FindUp("wrangler.jsonc");
        var text = File.ReadAllText(path);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "\"keep_vars\"\\s*:\\s*true").Count);
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
