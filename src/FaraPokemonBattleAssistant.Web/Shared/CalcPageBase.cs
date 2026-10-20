using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.AspNetCore.Components;

namespace FaraPokemonBattleAssistant.Web.Shared;

/// <summary>
/// 「ダメージ計算機」「素早さ比較」ページの共通部分。2 体のポケモン（側 0 = 攻撃側・ポケモン1、側 1 = 防御側・ポケモン2）を扱い、
/// 選んだポケモン・性格・「登録から選ぶ」の状態を、共有の <see cref="BattleStage"/> を通して、もう一方のページに引き継ぐ。
/// </summary>
public abstract class CalcPageBase : AppPageBase
{
    [Inject] protected BattleStage Stage { get; set; } = default!;

    /// <summary>読み込みと、もう一方のページで選んだポケモン・性格の引き継ぎが終わったか。終わる前の描画で、前回の内容を共有の Stage に書かない。</summary>
    protected bool stageReady;

    /// <summary>読み込んだゲームのデータ（読み込めていなければ null）。</summary>
    protected abstract PokemonDataSet? CalcData { get; }

    /// <summary>「登録から選ぶ」の候補。</summary>
    protected abstract IReadOnlyList<RosterChoiceGroup> RosterChoices { get; }

    /// <summary>いま入っているページの入力のゲームのキー（読み込み中の別のゲームの値を、Stage に書かないために使う）。</summary>
    protected abstract string CalcDataSetKey { get; }

    protected abstract bool GetUseRoster(int side);
    protected abstract void SetUseRoster(int side, bool value);
    protected abstract string? GetRosterKey(int side);
    protected abstract Pokemon? GetMega(int side);
    protected abstract PokemonBuild? GetBuild(int side);

    /// <summary>登録から選んだ構成を、その側に入れる（メガシンカの姿つき）。</summary>
    protected abstract void PickRosterChoice(int side, RosterChoice choice, Pokemon? mega);

    /// <summary>ポケモン（全ポケモンから選ぶ欄）を、その側に入れる。</summary>
    protected abstract void SelectPokemon(int side, int pokemonId);

    /// <summary>「登録から選ぶ」が実際に効いているか（登録が 1 つも無いときは、スイッチが出ず、普通の入力欄になる）。</summary>
    protected bool RosterOn(int side) => GetUseRoster(side) && RosterChoices.Count > 0;

    protected RosterChoice? FindChoice(string? key) => PickedBuild.FindChoice(RosterChoices, key);

    /// <summary>選んだ登録ポケモンのメガシンカ後の姿（メガシンカしないポケモンなら空）。</summary>
    protected IReadOnlyList<Pokemon> MegaFormsOf(string? key) =>
        CalcData is { } data ? PickedBuild.MegaFormsOf(data, RosterChoices, key) : Array.Empty<Pokemon>();

    /// <summary>URL のクエリ（?attacker=ID など）の ID。数字でない・存在しないポケモンなら null。</summary>
    protected int? QueryPokemonId(string? query) =>
        int.TryParse(query, out var id) && id > 0 && CalcData?.FindPokemon(id) is not null ? id : null;

    /// <summary>
    /// もう一方のページで選んだポケモン・性格・「登録から選ぶ」を入れる（ゲームのキーが同じときだけ）。
    /// 「登録から選ぶ」がオンの側は、選んだ登録とメガシンカの姿も入れる。オフの側は、ポケモンと性格を入れる。
    /// </summary>
    protected void ImportFromStage(string dataSetKey)
    {
        if (Stage.DataSetKey != dataSetKey) return;
        // 「登録から選ぶ」のオン・オフ
        if (Stage.UseRosterMine is { } useMine) SetUseRoster(0, useMine);
        if (Stage.UseRosterFoe is { } useFoe) SetUseRoster(1, useFoe);
        for (var side = 0; side < 2; side++)
        {
            var key = side == 0 ? Stage.RosterKeyMine : Stage.RosterKeyFoe;
            var mega = side == 0 ? Stage.RosterMegaMine : Stage.RosterMegaFoe;
            var picked = side == 0 ? Stage.PickedMine : Stage.PickedFoe;
            var nature = side == 0 ? Stage.PickedMineNature : Stage.PickedFoeNature;
            if (RosterOn(side))
            {
                if (key is not null && FindChoice(key) is { } choice && (GetRosterKey(side) != key || GetMega(side)?.Id != mega?.Id))
                    PickRosterChoice(side, choice, mega);
                continue;
            }
            if (picked is not null && GetBuild(side)?.Pokemon.Id != picked.Id) SelectPokemon(side, picked.Id);
            // 同じポケモンのときだけ、性格も引き継ぐ
            if (nature is not null && GetBuild(side) is { } build && build.Pokemon.Id == picked?.Id) build.Nature = nature;
        }
    }

    /// <summary>今のポケモン・性格・「登録から選ぶ」を、もう一方のページに引き継げるよう、Stage に書く（読み込みが終わってから）。</summary>
    protected void ShareToStage()
    {
        if (!stageReady) return;
        Stage.ShareRosterMode(CalcDataSetKey, RosterOn(0), RosterOn(1), GetRosterKey(0), GetMega(0), GetRosterKey(1), GetMega(1));
        Stage.Show(CalcDataSetKey, GetBuild(0)?.Pokemon, GetBuild(1)?.Pokemon, GetBuild(0)?.Nature, GetBuild(1)?.Nature);
    }

    public override void Dispose()
    {
        Stage.ClearScene(this);
        base.Dispose();
    }
}
