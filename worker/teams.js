// チーム（my-team）のバトル形式ごとの番号と使用中の扱い。Worker（worker/index.js）と動作確認（worker/teams.test.mjs）で使う。
// チームは (バトル形式, 番号) で 1 つ。番号はシングル（Singles）とダブル（Doubles）で別々に数える。使用中のチームは形式ごとに 1 つ（ActiveTeams）。

const isPlainObject = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
const isId = (v) => Number.isSafeInteger(v) && v >= 1 && v <= 2147483647;

/** チームのバトル形式（省略は Singles）。 */
export function teamFormat(t) {
  return t?.Format === "Doubles" ? "Doubles" : "Singles";
}

/**
 * バトル形式ごとにチーム番号を数える前の古いデータ（ActiveTeams が無い）を新しい形にする。
 * 古いデータのチームは、保存してあったバトル形式が何でもすべて Singles として扱う（番号は全体で一意なので衝突しない）。
 * 使用中のチーム（ActiveTeam）は Singles の使用中になる。何度呼んでも結果は同じ。
 */
export function upgradeTeamFormats(r) {
  const teams = r.Teams.filter(isPlainObject);
  if (!isPlainObject(r.ActiveTeams)) {
    for (const t of teams) t.Format = "Singles";
    r.ActiveTeams = { Singles: null, Doubles: null };
    if (isId(r.ActiveTeam) && teams.some((t) => t.Number === r.ActiveTeam)) r.ActiveTeams.Singles = r.ActiveTeam;
    delete r.ActiveTeam;
    return;
  }
  delete r.ActiveTeam;
  // 新しい形でも、存在しないチームを指す使用中は外す。Singles・Doubles の両方のキーをそろえる
  const actives = {};
  for (const f of ["Singles", "Doubles"]) {
    const n = r.ActiveTeams[f];
    actives[f] = isId(n) && teams.some((t) => teamFormat(t) === f && t.Number === n) ? n : null;
  }
  r.ActiveTeams = actives;
}

/** チームの並び順（番号の小さい順。同じ番号ならシングルが先）。 */
export function compareTeams(a, b) {
  return a.Number - b.Number || (teamFormat(a) === teamFormat(b) ? 0 : teamFormat(a) === "Singles" ? -1 : 1);
}

/** 消えたチームを指している使用中を外す（ポケモンを消して空のチームが消えたときなど）。 */
export function releaseMissingActiveTeams(d) {
  const teams = d.Teams.filter(isPlainObject);
  for (const f of ["Singles", "Doubles"]) {
    const n = d.ActiveTeams?.[f];
    if (isId(n) && !teams.some((t) => teamFormat(t) === f && t.Number === n)) d.ActiveTeams[f] = null;
  }
}
