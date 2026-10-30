using System.Net.Http;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>アクセスキーごとに別の置き場を持つ偽サーバー（キーの作り直しで、新しいキーの空の置き場に移せるように）。</summary>
internal sealed class KeyRoutedServer : HttpMessageHandler
{
    private readonly Dictionary<string, FakeRosterServer> _buckets = new();

    public FakeRosterServer For(string key)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            _buckets[key] = bucket = new FakeRosterServer { ExpectedKey = key };
            OnCreate?.Invoke(key, bucket);
        }
        return bucket;
    }

    /// <summary>新しいアクセスキーの置き場を作ったときに呼ぶ（その置き場の動作を変えるため）。</summary>
    public Action<string, FakeRosterServer>? OnCreate { get; set; }

    /// <summary>true の間、すべての要求が接続エラーになる。</summary>
    public bool Offline { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline) throw new HttpRequestException("offline");
        var key = request.Headers.Authorization?.Parameter ?? "";
        return For(key).HandleAsync(request, ct);
    }
}
