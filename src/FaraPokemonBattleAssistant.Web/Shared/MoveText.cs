using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Web.Shared;

/// <summary>技の表示用の文字（複数のページで同じ書き方をそろえる）。</summary>
public static class MoveText
{
    /// <summary>技の分類（物理・特殊・変化）。</summary>
    public static string Category(Move move) => move.Category switch
    {
        MoveCategory.Physical => "物理",
        MoveCategory.Special => "特殊",
        _ => "変化",
    };
}
