using FaraPokemonBattleAssistant.Core.Models;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>オーガポンの、すがた（お面）ごとの決まりの表。</summary>
public class OgerponTableTests
{
    [Theory]
    [InlineData(10273, 2106, "Water", Stat.SpDefense)]
    [InlineData(10274, 2107, "Fire", Stat.Attack)]
    [InlineData(10275, 2108, "Rock", Stat.Defense)]
    public void Mask_forms_have_a_fixed_mask_type_and_tera_boost(int formId, int maskId, string type, Stat boost)
    {
        Assert.Equal(maskId, Ogerpon.FixedMaskItemId(Ogerpon.SpeciesId, formId));
        Assert.Equal(type, Ogerpon.CudgelType(formId));
        Assert.Equal(boost, Ogerpon.TeraBoostStat(formId));
        Assert.True(Ogerpon.IsMatchingMask(formId, maskId));
        Assert.False(Ogerpon.IsMatchingMask(formId, Ogerpon.TealMaskItemId));
        Assert.True(Ogerpon.IsOtherMask(maskId));
    }

    [Fact]
    public void Base_form_is_free_and_uses_the_teal_mask()
    {
        Assert.Null(Ogerpon.FixedMaskItemId(Ogerpon.SpeciesId, Ogerpon.SpeciesId));
        Assert.Null(Ogerpon.CudgelType(Ogerpon.SpeciesId));
        Assert.Equal(Stat.Speed, Ogerpon.TeraBoostStat(Ogerpon.SpeciesId));
        Assert.True(Ogerpon.IsMatchingMask(Ogerpon.SpeciesId, Ogerpon.TealMaskItemId));
        Assert.False(Ogerpon.IsMatchingMask(Ogerpon.SpeciesId, 2106));
        Assert.True(Ogerpon.IsMask(Ogerpon.TealMaskItemId));
        Assert.False(Ogerpon.IsOtherMask(Ogerpon.TealMaskItemId));
    }

    [Fact]
    public void Other_pokemon_and_no_item_never_match()
    {
        Assert.False(Ogerpon.IsMatchingMask(6, null));
        Assert.False(Ogerpon.IsMatchingMask(Ogerpon.SpeciesId, null));
        Assert.False(Ogerpon.IsMask(null));
        Assert.Null(Ogerpon.FixedMaskItemId(6, 10273));   // 種族がオーガポンでなければ固定しない
    }
}
