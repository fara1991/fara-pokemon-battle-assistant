// チームのバトル形式ごとの番号・使用中の扱い（worker/teams.js）。
//   node --test worker/teams.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { teamFormat, upgradeTeamFormats, compareTeams, releaseMissingActiveTeams } from "./teams.js";

const team = (Number, Format, ids = [1]) => ({ Number, Format, Slots: ids.map((EntryId) => ({ EntryId })) });
const legacy = () => ({
  Teams: [team(1, undefined), team(2, "Doubles"), team(3, "Singles")],
  ActiveTeam: 2,
});

test("形式の読み替え: 省略・知らない値はシングル", () => {
  assert.equal(teamFormat({}), "Singles");
  assert.equal(teamFormat({ Format: null }), "Singles");
  assert.equal(teamFormat({ Format: "Singles" }), "Singles");
  assert.equal(teamFormat({ Format: "Doubles" }), "Doubles");
  assert.equal(teamFormat({ Format: "Triples" }), "Singles");
  assert.equal(teamFormat(undefined), "Singles");
});

test("古いデータ: チームはダブルで保存されていてもすべてシングルになり、使用中はシングルの使用中になる", () => {
  const d = legacy();
  upgradeTeamFormats(d);
  assert.deepEqual(d.Teams.map((t) => t.Format), ["Singles", "Singles", "Singles"]);
  assert.deepEqual(d.Teams.map((t) => t.Number), [1, 2, 3]);
  assert.deepEqual(d.ActiveTeams, { Singles: 2, Doubles: null });
  assert.equal("ActiveTeam" in d, false);
});

test("古いデータの使用中が存在しない番号なら外す・使用中なしも新しい形になる", () => {
  const dangling = { Teams: [team(1)], ActiveTeam: 9 };
  upgradeTeamFormats(dangling);
  assert.deepEqual(dangling.ActiveTeams, { Singles: null, Doubles: null });
  const none = { Teams: [], ActiveTeam: null };
  upgradeTeamFormats(none);
  assert.deepEqual(none.ActiveTeams, { Singles: null, Doubles: null });
});

test("移行は何度呼んでも同じで、新しい形のダブルのチームをシングルにしない", () => {
  const d = legacy();
  upgradeTeamFormats(d);
  d.Teams.push(team(1, "Doubles", [2]));
  d.ActiveTeams.Doubles = 1;
  const snapshot = JSON.stringify(d);
  upgradeTeamFormats(d);
  upgradeTeamFormats(d);
  assert.equal(JSON.stringify(d), snapshot);
  assert.equal(d.Teams.filter((t) => t.Format === "Doubles").length, 1);
  assert.deepEqual(d.ActiveTeams, { Singles: 2, Doubles: 1 });
});

test("新しい形でも、存在しないチームを指す使用中は外し、両方の形式のキーをそろえる", () => {
  const d = { Teams: [team(1, "Singles")], ActiveTeams: { Singles: 1, Doubles: 1 } };
  upgradeTeamFormats(d);
  assert.deepEqual(d.ActiveTeams, { Singles: 1, Doubles: null });
  const sparse = { Teams: [team(1, "Doubles")], ActiveTeams: { Doubles: 1 } };
  upgradeTeamFormats(sparse);
  assert.deepEqual(sparse.ActiveTeams, { Singles: null, Doubles: 1 });
});

test("並び順: 番号の小さい順、同じ番号ならシングルが先", () => {
  const teams = [team(2, "Doubles"), team(1, "Doubles"), team(2, "Singles"), team(1, "Singles")];
  assert.deepEqual(teams.sort(compareTeams).map((t) => `${t.Format}${t.Number}`), ["Singles1", "Doubles1", "Singles2", "Doubles2"]);
});

test("消えたチームの使用中だけを外す", () => {
  const d = { Teams: [team(1, "Singles")], ActiveTeams: { Singles: 1, Doubles: 1 } };
  releaseMissingActiveTeams(d);
  assert.deepEqual(d.ActiveTeams, { Singles: 1, Doubles: null });
});
