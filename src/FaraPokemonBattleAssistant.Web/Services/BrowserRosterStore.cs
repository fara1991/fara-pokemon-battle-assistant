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

    /// <summary>
    /// localStorage を読めなかったことがある（読めないまま保存・削除すると、残っている登録データを空で上書き・削除してしまう）。
    /// リポジトリは一度読んだ（読めなかったときは空の）内容を使い続けるので、あとで読めても戻さない。ページを読み込み直すと解除される。
    /// </summary>
    public bool LoadFailed { get; private set; }

    /// <summary>ブラウザの保存領域（約 5 MB）に収まるよう、試合の履歴はゲームごとに <see cref="RosterRepository.BrowserHistoryLimit"/> 件まで。</summary>
    public int? LocalHistoryLimit => RosterRepository.BrowserHistoryLimit;

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", ct, Key);
        }
        catch (JSException)
        {
            // 読めなくても、見るだけなら空として使える。ただし、保存・削除はしない
            LoadFailed = true;
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
        if (LoadFailed)
            throw new RosterUnavailableException(RosterUnavailableReason.InvalidResponse, null,
                "ブラウザの保存領域を読み込めなかったため、保存できません。ページを再読み込みしてください（直らないときは、ブラウザがサイトデータの保存を許可しているか確認してください）");
    }

    /// <summary>ブラウザでは登録データ全体をそのまま保存する。</summary>
    public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
        SaveAsync(RosterRepository.Serialize(state), ct);

    /// <summary>このブラウザの登録データを消す（データ共有を始めたとき）。</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        ThrowIfUnreadable();
        await _js.InvokeVoidAsync("localStorage.removeItem", ct, Key);
    }
}
