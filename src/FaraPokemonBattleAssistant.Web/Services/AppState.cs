using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 全ページで共有する設定（データセット・バトル形式・テーマ）。サイドバーで変更し、各ページが購読する。
/// </summary>
public sealed class AppState
{
    private const string StorageKey = "fara-pokemon-battle-assistant.settings";
    private readonly DataCatalog _catalog;
    private readonly IJSRuntime _js;
    private Task? _init;

    public AppState(DataCatalog catalog, IJSRuntime js)
    {
        _catalog = catalog;
        _js = js;
    }

    public IReadOnlyList<DataSetInfo> DataSets { get; private set; } = Array.Empty<DataSetInfo>();
    public string DataSetKey { get; private set; } = DataCatalog.DefaultDataSetKey;
    public DataSetInfo? DataSet => DataSets.FirstOrDefault(d => d.Key == DataSetKey);
    public string CommandPrefix => DataSet?.CommandPrefix ?? "pokech";
    public BattleFormat Format { get; private set; } = BattleFormat.Singles;
    /// <summary>"light" / "dark"。</summary>
    public string Theme { get; private set; } = "light";

    /// <summary>読み込んだゲームのキーが、いま選んでいるゲームか（まだ読んでいなければ「同じ」とみなす）。ゲームを切り替えた直後は、読み込みが終わるまで違う。</summary>
    public static bool IsSameGame(string? loadedKey, string currentKey) => loadedKey is null || loadedKey == currentKey;

    /// <summary>データセット・バトル形式が変わったときに発火する。</summary>
    public event Func<Task>? Changed;

    /// <summary>初期化。複数のコンポーネントから同時に呼ばれても同じ処理を待つ。</summary>
    public Task InitializeAsync()
    {
        // 初期化に失敗したときは、失敗したまま使い回さず、次に呼ばれたときにやり直す（再読み込みするまで直らなくなるのを避ける）
        if (_init is null || _init.IsFaulted || _init.IsCanceled) _init = InitializeCoreAsync();
        return _init;
    }

    private async Task InitializeCoreAsync()
    {
        DataSets = await _catalog.GetDataSetsAsync();
        DataSetKey = await _catalog.ResolveDefaultKeyAsync();
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(json);
                if (saved is not null)
                {
                    if (DataSets.Any(d => d.Key == saved.DataSetKey)) DataSetKey = saved.DataSetKey!;
                    if (Enum.TryParse<BattleFormat>(saved.Format, out var f)) Format = f;
                    if (saved.Theme is "light" or "dark") Theme = saved.Theme;
                }
            }
        }
        catch
        {
            // localStorage が使えない環境（プライベートモード等）では既定値のまま
        }
        await ApplyThemeAsync();
        // 起動直後は、選んでいるゲームのデータだけを読む（他のゲームは、選んだとき・使うページを開いたときに読む）
        _ = PreloadOtherFormatAsync();
    }

    /// <summary>
    /// 起動の少し後に、選んでいるゲームの「もう一方の形式」の使用率だけを低い優先度で読んでおく（形式を切り替えたときにすぐ並び替えられるように）。
    /// 起動直後の通信を増やさないよう、待ってから読む。失敗は無視する（切り替えたときに読み直す）。
    /// </summary>
    private async Task PreloadOtherFormatAsync()
    {
        try
        {
            await Task.Delay(OtherFormatPreloadDelayMs);
            var other = Format == BattleFormat.Singles ? BattleFormat.Doubles : BattleFormat.Singles;
            var data = await _catalog.GetDataSetAsync(DataSetKey);
            await data.GetUsageAsync(other);
        }
        catch
        {
        }
    }

    /// <summary>形式の先読みまでの待ち時間（起動直後の通信と競合しないように）。</summary>
    private const int OtherFormatPreloadDelayMs = 3000;

    public async Task SetDataSetAsync(string key)
    {
        if (key == DataSetKey || DataSets.All(d => d.Key != key)) return;
        DataSetKey = key;
        // 保存を先にする（読み込みの待ち時間のあいだにページを閉じても、選んだゲームを覚えている）
        await SaveAsync();
        await RaiseChangedAsync();
    }

    public async Task SetFormatAsync(BattleFormat format)
    {
        if (format == Format) return;
        Format = format;
        await SaveAsync();
        await RaiseChangedAsync();
    }

    public async Task SetThemeAsync(string theme)
    {
        Theme = theme == "dark" ? "dark" : "light";
        await SaveAsync();
        await ApplyThemeAsync();
        await RaiseChangedAsync();   // MudBlazor のテーマ（Shared/MudProviders.razor）・スイッチの表示を合わせる
    }

    public Task ToggleThemeAsync() => SetThemeAsync(Theme == "dark" ? "light" : "dark");

    private async Task ApplyThemeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("document.documentElement.setAttribute", "data-bs-theme", Theme);
        }
        catch
        {
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new Saved { DataSetKey = DataSetKey, Format = Format.ToString(), Theme = Theme });
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
        }
    }

    private async Task RaiseChangedAsync()
    {
        if (Changed is null) return;
        // 購読先は全部いっぺんに呼ぶ。読み込みに時間がかかる画面（データの読み込みを待つ）が前にいても、メニューの表示が後ろに回らないように。
        // 1 つの画面の読み込みが失敗しても、残りの購読先（ほかの画面・メニュー）を呼ぶ。失敗した画面は、自分でメッセージを出す
        var running = Changed.GetInvocationList().Cast<Func<Task>>().Select(async handler =>
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"画面の更新に失敗しました: {ex.Message}");
            }
        });
        await Task.WhenAll(running);
    }

    private sealed class Saved
    {
        public string? DataSetKey { get; set; }
        public string? Format { get; set; }
        public string? Theme { get; set; }
    }
}

/// <summary>
/// ページを切り替えても入力内容を保持するための置き場。ページごとに状態オブジェクトを 1 つ持つ。
/// </summary>
public sealed class PageStateStore
{
    private readonly Dictionary<string, object> _states = new();

    public T Get<T>(string key) where T : class, new()
    {
        if (_states.TryGetValue(key, out var existing) && existing is T typed) return typed;
        var created = new T();
        _states[key] = created;
        return created;
    }
}
