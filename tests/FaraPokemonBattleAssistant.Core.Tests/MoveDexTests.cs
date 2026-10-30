using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>技一覧（絞り込み・並べ替え・技を覚えるポケモン）。</summary>
public class MoveDexTests
{
    [Fact]
    public async Task Move_dex_filters_sorts_and_lists_learners()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        // 名前（ローマ字・かなでも）・タイプ・分類で絞り込む
        var byName = MoveDex.Query(data, new MoveFilter { Name = "なみのり" });
        Assert.Contains(byName, m => m.Name == "なみのり");
        var fire = MoveDex.Query(data, new MoveFilter { Type = "Fire", Category = MoveCategory.Special });
        Assert.NotEmpty(fire);
        Assert.All(fire, m => { Assert.Equal("Fire", m.Type); Assert.Equal(MoveCategory.Special, m.Category); });
        // 威力の大きい順。同じ威力は技名順
        var byPower = MoveDex.Query(data, new MoveFilter { Sort = MoveSort.Power, Descending = true });
        Assert.True(byPower.Zip(byPower.Skip(1)).All(p => p.First.Power >= p.Second.Power));
        // 1 つの技だけと、その技を覚えるポケモン
        var surf = data.Moves.First(m => m.Name == "なみのり");
        Assert.Single(MoveDex.Query(data, new MoveFilter { MoveId = surf.Id }));
        var learners = MoveDex.Learners(data, surf.Id);
        Assert.Contains(learners, p => p.Name == "ガブリアス");
        Assert.All(learners, p => Assert.True(data.CanLearn(p.Id, surf.Id)));
        Assert.True(learners.Zip(learners.Skip(1)).All(p => Pokedex.CompareDex(p.First, p.Second) <= 0));
    }

    [Fact]
    public async Task Move_dex_sorts_in_gojuon_order_and_ties_by_name()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var byName = MoveDex.Query(data, new MoveFilter());
        // ひらがなとカタカナを区別せず五十音順（ひらがな始まりの技が先に固まらない）
        Assert.True(byName.Zip(byName.Skip(1)).All(p => string.CompareOrdinal(KanaSearch.Normalize(p.First.Name), KanaSearch.Normalize(p.Second.Name)) <= 0));
        Assert.Equal(byName.Reverse().Select(m => m.Id), MoveDex.Query(data, new MoveFilter { Descending = true }).Select(m => m.Id));
        // 同じ威力の技どうしは、技名の順
        var byPower = MoveDex.Query(data, new MoveFilter { Sort = MoveSort.Power });
        var nameRank = byName.Select((m, i) => (m.Id, i)).ToDictionary(x => x.Id, x => x.i);
        Assert.True(byPower.Zip(byPower.Skip(1)).All(p => p.First.Power < p.Second.Power || nameRank[p.First.Id] < nameRank[p.Second.Id]));
        // タイプは TypeNames.All の順
        var typeOrder = TypeNames.All.ToList();
        var byType = MoveDex.Query(data, new MoveFilter { Sort = MoveSort.Type });
        Assert.True(byType.Zip(byType.Skip(1)).All(p => typeOrder.IndexOf(p.First.Type) <= typeOrder.IndexOf(p.Second.Type)));
    }

    [Fact]
    public async Task A_canceled_usage_request_does_not_break_later_requests()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        // 最初の呼び出しがキャンセルされても、読み込みを使い回す次の呼び出しは成功する
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => data.GetUsageAsync(BattleFormat.Singles, cts.Token));
        var usage = await data.GetUsageAsync(BattleFormat.Singles);
        Assert.NotNull(usage);
    }
}
