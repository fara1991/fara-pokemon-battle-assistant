using FaraPokemonBattleAssistant.Core.Roster;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>ブラウザの localStorage に登録データを保存する。</summary>
public sealed class BrowserRosterStore : IRosterStore
{
    private const string Key = "fara-pokemon-battle-assistant.roster";
    private readonly IJSRuntime _js;

    public BrowserRosterStore(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>直近の読み込みで localStorage を読めなかった（読めないまま保存すると、残っている登録データを空で上書きしてしまう）。</summary>
    private bool _loadFailed;

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", ct, Key);
            _loadFailed = false;
            return json;
        }
        catch (JSException)
        {
            // 読めなくても、見るだけなら空として使える。ただし、読めていないあいだは保存しない
            _loadFailed = true;
            return null;
        }
    }

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        ThrowIfUnreadable();
        await _js.InvokeVoidAsync("localStorage.setItem", ct, Key, json);
    }

    private void ThrowIfUnreadable()
    {
        if (_loadFailed)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "ブラウザの保存領域を読み込めなかったため、保存できません。ページを再読み込みしてください");
    }

    /// <summary>ブラウザでは登録データ全体をそのまま保存する。</summary>
    public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
        SaveAsync(RosterRepository.Serialize(state), ct);

    /// <summary>このブラウザの登録データを消す（データ共有を始めたとき）。</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", ct, Key);
    }
}
