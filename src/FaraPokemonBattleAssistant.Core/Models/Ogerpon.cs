namespace FaraPokemonBattleAssistant.Core.Models;

/// <summary>
/// オーガポンの、すがた（お面）ごとの決まりをまとめた表。
/// 通常のすがた（みどりのめん）は持ち物を自由に選べ、ほかの 3 つのすがたはそのお面で固定。
/// </summary>
public static class Ogerpon
{
    /// <summary>オーガポン（みどりのめん・いどのめん・かまどのめん・いしずえのめん）の種族 ID。</summary>
    public const int SpeciesId = 1017;

    /// <summary>みどりのめん（通常のすがたのお面）の持ち物 ID。</summary>
    public const int TealMaskItemId = 2102;

    private sealed record Form(int PokemonId, int MaskItemId, string Type, Stat TeraBoostStat);

    private static readonly Form[] MaskForms =
    {
        new(10273, 2106, "Water", Stat.SpDefense),   // いどのめん
        new(10274, 2107, "Fire", Stat.Attack),       // かまどのめん
        new(10275, 2108, "Rock", Stat.Defense),      // いしずえのめん
    };

    private static Form? FormOf(int pokemonId) => Array.Find(MaskForms, f => f.PokemonId == pokemonId);

    /// <summary>お面のすがたの、固定の持ち物 ID（通常のすがた・オーガポン以外は null）。</summary>
    public static int? FixedMaskItemId(int speciesId, int pokemonId) => speciesId == SpeciesId ? FormOf(pokemonId)?.MaskItemId : null;

    /// <summary>持ち物がオーガポンのお面（みどり・いど・かまど・いしずえ）か。</summary>
    public static bool IsMask(int? itemId) => itemId == TealMaskItemId || MaskForms.Any(f => f.MaskItemId == itemId);

    /// <summary>ほかのすがたのお面（いど・かまど・いしずえ）か。通常のオーガポンが持っていたら、みどりのめんに直す対象。</summary>
    public static bool IsOtherMask(int? itemId) => MaskForms.Any(f => f.MaskItemId == itemId);

    /// <summary>そのすがたに合ったお面か（通常のすがたはみどりのめん）。</summary>
    public static bool IsMatchingMask(int pokemonId, int? itemId) =>
        itemId is { } id && (pokemonId == SpeciesId ? id == TealMaskItemId : FormOf(pokemonId)?.MaskItemId == id);

    /// <summary>ツタこんぼうのタイプ（お面をつけたすがたで変わる）。お面のすがたでなければ null。</summary>
    public static string? CudgelType(int pokemonId) => FormOf(pokemonId)?.Type;

    /// <summary>テラスタルしたときに 1 段階上がる能力（みどり=すばやさ・いど=とくぼう・かまど=こうげき・いしずえ=ぼうぎょ）。</summary>
    public static Stat TeraBoostStat(int pokemonId) => FormOf(pokemonId)?.TeraBoostStat ?? Stat.Speed;
}
