namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 読み込み中に届いた検索の ID（?attacker= など）の保留。届いたときのゲームのキーと結びつけ、
/// そのゲームの読み込みが成功したときだけ適用する（失敗したとき、別のゲームが読めたときは捨てる）。
/// </summary>
public sealed class HeldQuery
{
    /// <summary>保留している、届いたときのゲームのキー（無ければ null）。</summary>
    public string? Key { get; private set; }

    public bool IsHeld => Key is not null;

    /// <summary>ゲームのデータが無いあいだに届いた ID を、いまのゲームのキーで保留する。</summary>
    public void Hold(string key) => Key = key;

    /// <summary>
    /// 読み込みが終わったときに呼ぶ。保留は、この呼び出しで必ず消える。
    /// 保留したゲームの読み込みが成功したときだけ true（適用する）。
    /// </summary>
    public bool Complete(string loadedKey, bool succeeded)
    {
        var apply = Key is not null && succeeded && string.Equals(Key, loadedKey, StringComparison.Ordinal);
        Key = null;
        return apply;
    }
}

/// <summary>
/// 「次に読み込みが成功したときに、解析（ParseText など）をやり直す」印。読み込みが途中で古くなっても、印は次の成功まで残る。
/// </summary>
public sealed class DeferredFlag
{
    public bool IsSet { get; private set; }

    public void Set() => IsSet = true;

    /// <summary>印が付いていれば、外して true を返す（そのとき、やり直しを行う）。</summary>
    public bool Take()
    {
        if (!IsSet) return false;
        IsSet = false;
        return true;
    }
}
