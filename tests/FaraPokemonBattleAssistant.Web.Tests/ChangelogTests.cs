using FaraPokemonBattleAssistant.Web.Services;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>更新履歴（Services/Changelog.cs と wwwroot/changelog.json）。</summary>
public class ChangelogTests
{
    [Fact]
    public void Parse_returns_entries_newest_first_and_keeps_order_within_a_day()
    {
        var list = Changelog.Parse("""
            { "entries": [
              { "date": "2026-10-01", "title": "古い" },
              { "date": "2026-10-20", "title": "同じ日の 1 つ目", "items": ["a", " b "] },
              { "date": "2026-10-20", "title": "同じ日の 2 つ目" }
            ] }
            """);
        Assert.Equal(new[] { "同じ日の 1 つ目", "同じ日の 2 つ目", "古い" }, list.Select(e => e.Title));
        Assert.Equal(new[] { "a", "b" }, list[0].Items);
        Assert.Empty(list[2].Items);
        Assert.Equal(new DateOnly(2026, 10, 20), list[0].Date);
    }

    [Theory]
    [InlineData("<!doctype html><html></html>")]                                      // ファイルが無いときの SPA のフォールバック
    [InlineData("[]")]                                                                  // entries が無い
    [InlineData("""{ "entries": ["x"] }""")]                                            // 要素がオブジェクトでない
    [InlineData("""{ "entries": {} }""")]                                               // entries が配列でない
    [InlineData("""{ "entries": [ { "date": "2026/10/20", "title": "x" } ] }""")]       // 日付の形が違う
    [InlineData("""{ "entries": [ { "date": "2026-10-20", "title": " " } ] }""")]       // 見出しが空
    [InlineData("""{ "entries": [ { "date": "2026-10-20", "title": "x", "items": "a" } ] }""")]   // items が配列でない
    [InlineData("""{ "entries": [ { "date": "2026-10-20", "title": "x", "items": [""] } ] }""")]  // 空の項目
    public void Parse_rejects_malformed_input(string json) =>
        Assert.Throws<FormatException>(() => Changelog.Parse(json));

    [Fact]
    public async Task LoadAsync_reads_once_and_retries_after_a_failure()
    {
        using var handler = new CountingHandler(new[] { "<html>", """{ "entries": [ { "date": "2026-10-20", "title": "x" } ] }""" });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var log = new Changelog(http);
        await Assert.ThrowsAsync<FormatException>(() => log.LoadAsync());      // 1 回目は失敗
        Assert.Single(await log.LoadAsync());                                   // 2 回目は読めた
        Assert.Single(await log.LoadAsync());                                   // 3 回目は覚えている（通信しない）
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void Shipped_changelog_is_valid_newest_first_and_has_details()
    {
        var path = FindUp("src/FaraPokemonBattleAssistant.Web/wwwroot/changelog.json");
        var list = Changelog.Parse(File.ReadAllText(path));
        Assert.NotEmpty(list);
        Assert.True(list.Count >= 3, "ホームに出す件数（3）より少ない");
        Assert.Equal(list.OrderByDescending(e => e.Date).Select(e => e.Date), list.Select(e => e.Date));   // 新しい順に書く
        Assert.All(list, e => Assert.True(e.Title.Length <= 60, $"{e.Date} の見出しが長い（ホームでは見出しだけ出すので短く）: {e.Title}"));
        Assert.All(list, e => Assert.NotEmpty(e.Items));                                                      // 細かい内容は items に書く
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

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Queue<string> bodies;
        public int Calls { get; private set; }
        public CountingHandler(IEnumerable<string> bodies) => this.bodies = new Queue<string>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = bodies.Count > 1 ? bodies.Dequeue() : bodies.Peek();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
