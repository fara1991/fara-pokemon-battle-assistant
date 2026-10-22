// データ共有 API（worker/index.js）の動作確認。
//   npm run test:worker
// wrangler dev をローカル（一時ディレクトリに保存）で起動して、/api/my/pokemon・/api/my/team・/api/my/access-key の
// 401・空の GET・PUT・409・400・413・410、ポケモン削除でチームから外れることなどを確かめる。
// あわせて、ゲームデータ（/api/game/*）が静的アセットとして（Worker を通して、そのまま）返ること、以前のパス（/api/my-pokemon・/data/...）が無いことも確かめる。
// publish/wwwroot が無いと wrangler dev が起動しないので、無ければ空の index.html と最小の api/game/datasets.csv だけ置く。
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const port = Number(process.env.PORT ?? 8799);
const base = `http://127.0.0.1:${port}`;
const persist = mkdtempSync(join(tmpdir(), "roster-api-test-"));

if (!existsSync("publish/wwwroot/index.html")) {
  mkdirSync("publish/wwwroot", { recursive: true });
  writeFileSync("publish/wwwroot/index.html", "<!doctype html><title>test</title>");
}
if (!existsSync("publish/wwwroot/api/game/datasets.csv")) {
  mkdirSync("publish/wwwroot/api/game", { recursive: true });
  writeFileSync("publish/wwwroot/api/game/datasets.csv", "Key,Name,Generation,EvSystem,CommandPrefix,Gimmick,UsageFallback,Calc\nChampions,test,9,Points,pokech,None,,Full\n");
}

const wrangler = spawn(
  process.platform === "win32" ? "npx.cmd" : "npx",
  ["wrangler", "dev", "--port", String(port), "--ip", "127.0.0.1", "--persist-to", persist, "--show-interactive-dev-session=false", "--var", "ALLOW_TEST_OVERRIDES:1", "--var", "HISTORY_LIMIT:3", "--var", "HISTORY_BYTES_LIMIT:1500", "--var", "EXPIRE_MS:5000"],
  { stdio: ["ignore", "pipe", "pipe"], shell: process.platform === "win32", detached: process.platform !== "win32" },
);
let output = "";
wrangler.stdout.on("data", (d) => (output += d));
wrangler.stderr.on("data", (d) => (output += d));

const newKey = () => randomBytes(32).toString("base64url");
const auth = (key) => ({ Authorization: `Bearer ${key}` });
let failures = 0;
function check(name, ok, detail = "") {
  console.log(`${ok ? "OK  " : "FAIL"} ${name}${ok || !detail ? "" : ` — ${detail}`}`);
  if (!ok) failures++;
}

async function waitReady() {
  for (let i = 0; i < 120; i++) {
    try {
      const r = await fetch(`${base}/api/my/pokemon`);
      if (r.status === 401) return;
    } catch {}
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error("wrangler dev が起動しませんでした\n" + output);
}

async function req(method, path, key, body, headers = {}) {
  const r = await fetch(`${base}${path}`, {
    method,
    headers: { ...(key ? auth(key) : {}), ...(body !== undefined ? { "Content-Type": "application/json" } : {}), ...headers },
    body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch {}
  return { status: r.status, json, text, headers: r.headers };
}
const P = "/api/my/pokemon";
const T = "/api/my/team";
const K = "/api/my/access-key";
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const mon = (id, name) => ({ Id: id, PokemonId: 445, PokemonName: name, HP: 4, Attack: 32, Nature: "ようき" });
const slot = (id, item = null) => ({ EntryId: id, ItemId: item });

try {
  await waitReady();
  const key = newKey();

  // 認証
  check("キーなしは 401", (await req("GET", P)).status === 401);
  check("キーなしの my-team は 401", (await req("GET", T)).status === 401);
  check("短いキーは 401", (await req("GET", P, "abc")).status === 401);
  check("44 文字のキーは 401", (await req("GET", P, key + "A")).status === 401);
  check("使えない文字を含むキーは 401", (await req("GET", P, key.slice(0, 42) + "=")).status === 401);
  check("Bearer 以外は 401", (await req("GET", P, null, undefined, { Authorization: `Basic ${key}` })).status === 401);
  const unauth = await req("PUT", P, null, { baseRevision: 0, dataSets: { Champions: { pokemon: [] } } });
  check("キーなしの PUT は 401", unauth.status === 401);
  check("401 の本文にキーが含まれない", !unauth.text.includes(key));
  check("キーなしの DELETE /api/my/access-key は 401", (await req("DELETE", K)).status === 401);

  // 空の GET
  const emptyP = await req("GET", P, key);
  check("空の GET my-pokemon は 200 / revision 0 / dataSets {}", emptyP.status === 200 && emptyP.json?.revision === 0 && same(emptyP.json?.dataSets, {}), emptyP.text);
  const emptyT = await req("GET", T, key);
  check("空の GET my-team は 200 / revision 0 / dataSets {}", emptyT.status === 200 && emptyT.json?.revision === 0 && same(emptyT.json?.dataSets, {}), emptyT.text);
  check("Cache-Control: no-store", emptyP.headers.get("cache-control") === "no-store");
  check("CORS ヘッダーを付けない", emptyP.headers.get("access-control-allow-origin") === null);

  // ポケモンの PUT → GET
  const mons = [mon(1, "ガブリアス"), mon(2, "リザードン"), mon(3, "イエッサン(♂)")];
  const put1 = await req("PUT", P, key, { baseRevision: 0, dataSets: { Champions: { pokemon: mons } } });
  check("PUT my-pokemon（baseRevision 0）は 200 / revision 1", put1.status === 200 && put1.json?.revision === 1, put1.text);
  const got = await req("GET", P, key);
  check("GET my-pokemon で保存した内容と nextId が返る",
    got.json?.revision === 1 && same(got.json?.dataSets?.Champions?.pokemon, mons) && got.json?.dataSets?.Champions?.nextId === 4, got.text);

  // メンテナンスの状態（変数 MAINTENANCE が無いので、通常。アプリが起動時に読む）
  const maint = await req("GET", "/api/maintenance", null);
  check("GET /api/maintenance は通常（off）を返し、キャッシュさせない", maint.status === 200 && maint.json?.state === "off" && maint.json?.message === "", maint.text);

  // チームの PUT → GET（revision は共通）
  const teams = [{ Number: 1, Slots: [slot(1, 275), slot(2)] }, { Number: 2, Slots: [slot(2)], Format: "Doubles" }, { Number: 3, Slots: [slot(3)], Format: "Singles" }];
  const put2 = await req("PUT", T, key, { baseRevision: 1, dataSets: { champions: { teams, activeTeam: 2 } } });
  check("PUT my-team は 200 / revision 2（ゲームのキーは大文字小文字を区別しない）", put2.status === 200 && put2.json?.revision === 2, put2.text);
  const gotT = await req("GET", T, key);
  check("GET my-team で保存した内容が返る", gotT.json?.revision === 2 && same(gotT.json?.dataSets?.Champions?.teams, teams) && gotT.json?.dataSets?.Champions?.activeTeam === 2 && gotT.json?.dataSets?.champions === undefined, gotT.text);
  check("GET my-pokemon も revision 2", (await req("GET", P, key)).json?.revision === 2);

  // 別のキーからは見えない
  const other = await req("GET", P, newKey());
  check("別のキーは空", other.status === 200 && other.json?.revision === 0 && same(other.json?.dataSets, {}));

  // 競合（409 は現在の内容を返す）
  const staleP = await req("PUT", P, key, { baseRevision: 1, dataSets: { Champions: { pokemon: [] } } });
  check("古い baseRevision の PUT my-pokemon は 409 で現在の内容を返す", staleP.status === 409 && staleP.json?.revision === 2 && same(staleP.json?.dataSets?.Champions?.pokemon, mons), staleP.text);
  const staleT = await req("PUT", T, key, { baseRevision: 0, dataSets: { Champions: { teams: [], activeTeam: null } } });
  check("古い baseRevision の PUT my-team は 409 で現在の内容を返す", staleT.status === 409 && staleT.json?.revision === 2 && same(staleT.json?.dataSets?.Champions?.teams, teams), staleT.text);

  // チームの検証（400・何も変わらない）
  const badTeam = async (name, dataSets) => {
    const r = await req("PUT", T, key, { baseRevision: 2, dataSets });
    check(`${name} は 400`, r.status === 400 && typeof r.json?.message === "string", r.text);
  };
  await badTeam("登録に無いポケモン ID", { Champions: { teams: [{ Number: 1, Slots: [slot(99)] }], activeTeam: null } });
  await badTeam("7 体のチーム", { Champions: { teams: [{ Number: 1, Slots: [1, 2, 3, 4, 5, 6, 7].map((i) => slot(i)) }], activeTeam: null } });
  await badTeam("存在しない使用中チーム", { Champions: { teams: [{ Number: 1, Slots: [slot(1)] }], activeTeam: 5 } });
  await badTeam("同じチーム番号が 2 つ", { Champions: { teams: [{ Number: 1, Slots: [slot(1)] }, { Number: 1, Slots: [slot(2)] }], activeTeam: null } });
  await badTeam("同じポケモンが 2 回", { Champions: { teams: [{ Number: 1, Slots: [slot(1), slot(1)] }], activeTeam: null } });
  await badTeam("知らないバトル形式", { Champions: { teams: [{ Number: 1, Slots: [slot(1)], Format: "Triples" }], activeTeam: null } });
  await badTeam("小文字のバトル形式", { Champions: { teams: [{ Number: 1, Slots: [slot(1)], Format: "doubles" }], activeTeam: null } });
  await badTeam("文字列でないバトル形式", { Champions: { teams: [{ Number: 1, Slots: [slot(1)], Format: 2 }], activeTeam: null } });
  await badTeam("空のチーム", { Champions: { teams: [{ Number: 1, Slots: [] }], activeTeam: null } });
  await badTeam("登録の無いゲームのチーム", { Gen9: { teams: [{ Number: 1, Slots: [slot(1)] }], activeTeam: null } });
  const r400 = await req("PUT", T, key, { baseRevision: 2, dataSets: { Champions: { teams: [{ Number: 1, Slots: [slot(99)] }], activeTeam: null } } });
  check("400 のメッセージに理由（登録に無いポケモン）", (r400.json?.message ?? "").includes("登録に無い"), r400.text);

  // 本文の検証
  check("JSON でない本文は 400", (await req("PUT", P, key, "{not json")).status === 400);
  // ゲームのキーに __proto__ などは使えない（プロトタイプの書き換えを防ぐ）。PUT の本文は JSON 文字列のまま送る（オブジェクトにすると __proto__ が消えるため）
  for (const [name, path] of [["pokemon", P], ["team", T]]) {
    const body = `{"baseRevision":2,"dataSets":{"__proto__":{"${name === "pokemon" ? "pokemon" : "teams"}":[]}}}`;
    check(`__proto__ のゲームのキーは ${name} の PUT で 400`, (await req("PUT", path, key, body)).status === 400);
  }
  check("2^31 以上の ID は 400（クライアントの int に収まらない）", (await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: [{ Id: 2147483647 }] } } })).status === 400);
  check("dataSets が無ければ 400", (await req("PUT", P, key, { baseRevision: 2 })).status === 400);
  check("dataSets が配列なら 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: [] })).status === 400);
  check("dataSets が空なら 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: {} })).status === 400);
  check("ゲームのキーが不正なら 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { "a/b": { pokemon: [] } } })).status === 400);
  check("pokemon が配列でなければ 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: {} } } })).status === 400);
  check("Id の無いポケモンは 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: [{ PokemonName: "x" }] } } })).status === 400);
  check("同じ Id のポケモンは 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: [mon(1, "a"), mon(1, "b")] } } })).status === 400);
  check("baseRevision が無ければ 400", (await req("PUT", P, key, { dataSets: { Champions: { pokemon: [] } } })).status === 400);
  check("baseRevision が負なら 400", (await req("PUT", P, key, { baseRevision: -1, dataSets: { Champions: { pokemon: [] } } })).status === 400);
  check("baseRevision が小数なら 400", (await req("PUT", P, key, { baseRevision: 1.5, dataSets: { Champions: { pokemon: [] } } })).status === 400);
  const big = { baseRevision: 2, dataSets: { Champions: { pokemon: [{ Id: 1, pad: "あ".repeat(90 * 1024) }] } } }; // 約 270 KB（UTF-8）
  check("256 KB を超える本文は 413", (await req("PUT", P, key, big)).status === 413);
  const afterBad = await req("GET", T, key);
  check("不正な PUT では内容が変わらない", afterBad.json?.revision === 2 && same(afterBad.json?.dataSets?.Champions?.teams, teams), afterBad.text);

  // 保存後の全体が 256 KB を超えるなら 413（本文はそれぞれ上限内）
  const sizeKey = newKey();
  const half = (id) => [{ Id: id, pad: "x".repeat(150 * 1024) }];
  check("1 つめのゲームは保存できる", (await req("PUT", P, sizeKey, { baseRevision: 0, dataSets: { Gen8: { pokemon: half(1) } } })).status === 200);
  check("合計が 256 KB を超える PUT は 413", (await req("PUT", P, sizeKey, { baseRevision: 1, dataSets: { Gen9: { pokemon: half(1) } } })).status === 413);
  check("413 のあとも revision は変わらない", (await req("GET", P, sizeKey)).json?.revision === 1);

  // ポケモンの削除でチームから外れる・空のチームが消える・使用中が解除される（同じ PUT の中で）
  const put3 = await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: [mons[0], mons[2]] } } });
  check("ポケモンを消す PUT は 200 / revision 3", put3.status === 200 && put3.json?.revision === 3, put3.text);
  const afterRemove = await req("GET", T, key);
  const ch = afterRemove.json?.dataSets?.Champions;
  check("消したポケモンがチームから外れる", same(ch?.teams?.find((t) => t.Number === 1)?.Slots, [slot(1, 275)]), afterRemove.text);
  check("0 体になったチームが消える", ch?.teams?.length === 2 && !ch.teams.some((t) => t.Number === 2), afterRemove.text);
  check("使用中のチームが消えたら使用中を解除", ch?.activeTeam === null, afterRemove.text);
  check("チームの変更も同じ revision 3", afterRemove.json?.revision === 3);
  const afterRemoveP = await req("GET", P, key);
  check("消した ID は使い回さない（nextId は 4 のまま）", afterRemoveP.json?.dataSets?.Champions?.nextId === 4, afterRemoveP.text);

  // 書かなかったゲームはそのまま
  const put4 = await req("PUT", P, key, { baseRevision: 3, dataSets: { Gen9: { pokemon: [mon(1, "ハバタクカミ")] } } });
  check("別のゲームの PUT は 200 / revision 4", put4.status === 200 && put4.json?.revision === 4, put4.text);
  const both = await req("GET", P, key);
  check("書かなかったゲームのポケモンは変わらない", both.json?.dataSets?.Champions?.pokemon?.length === 2 && both.json?.dataSets?.Gen9?.pokemon?.length === 1, both.text);
  const bothT = await req("GET", T, key);
  check("書かなかったゲームのチームは変わらない", bothT.json?.dataSets?.Champions?.teams?.length === 2 && same(bothT.json?.dataSets?.Gen9?.teams, []), bothT.text);

  // 相手チーム（/api/my/rival）: 対戦中の相手と試合の履歴
  const rkey = newKey();
  const R = "/api/my/rival";
  check("キーなしの my-rival は 401", (await req("GET", R)).status === 401);
  const emptyR = await req("GET", R, rkey);
  check("空の GET my-rival は 200 / revision 0 / dataSets {}", emptyR.status === 200 && emptyR.json?.revision === 0 && same(emptyR.json?.dataSets, {}), emptyR.text);
  const rival = (id, name, extra = {}) => ({ Id: id, PokemonId: 445, PokemonName: name, ...extra });
  const match = (id, foes) => ({ Id: id, Date: "2026-10-16T10:00:00+09:00", Memo: "テスト戦", Foes: foes });
  const rivals1 = [rival(1, "カイリュー", { SpDefense: 32 }), rival(2, "ハバタクカミ")];
  const putR1 = await req("PUT", R, rkey, { baseRevision: 0, dataSets: { Champions: { rivals: rivals1, history: [] } } });
  check("PUT my-rival は 200 / revision 1", putR1.status === 200 && putR1.json?.revision === 1, putR1.text);
  const gotR = await req("GET", R, rkey);
  check("GET my-rival で保存した内容と nextId が返る",
    gotR.json?.revision === 1 && same(gotR.json?.dataSets?.Champions?.rivals, rivals1) && gotR.json?.dataSets?.Champions?.nextId === 3 && gotR.json?.dataSets?.Champions?.nextMatchId === 1, gotR.text);
  const history1 = [match(1, [rivals1[0]])];
  const putR2 = await req("PUT", R, rkey, { baseRevision: 1, dataSets: { Champions: { rivals: [], nextId: 3, history: history1 } } });
  check("試合を終えて履歴に移す PUT は 200 / revision 2", putR2.status === 200 && putR2.json?.revision === 2, putR2.text);
  const gotR2 = await req("GET", R, rkey);
  check("履歴が保存され、相手は空になり、ID は使い回さない（nextId 3・nextMatchId 2）",
    same(gotR2.json?.dataSets?.Champions?.history, history1) && gotR2.json?.dataSets?.Champions?.rivals?.length === 0 && gotR2.json?.dataSets?.Champions?.nextId === 3 && gotR2.json?.dataSets?.Champions?.nextMatchId === 2, gotR2.text);
  check("my-pokemon の revision も 2（登録データ全体で 1 つ）", (await req("GET", P, rkey)).json?.revision === 2);
  const staleR = await req("PUT", R, rkey, { baseRevision: 1, dataSets: { Champions: { rivals: [], history: [] } } });
  check("古い baseRevision の PUT my-rival は 409 で現在の内容を返す", staleR.status === 409 && staleR.json?.revision === 2 && same(staleR.json?.dataSets?.Champions?.history, history1), staleR.text);
  const badR = (name, value) => req("PUT", R, rkey, { baseRevision: 2, dataSets: { Champions: value } }).then((r) => check(`${name} は 400`, r.status === 400, r.text));
  await badR("rivals が配列でない", { rivals: {}, history: [] });
  await badR("history が配列でない", { rivals: [], history: {} });
  await badR("Id の無い相手", { rivals: [{ PokemonName: "x" }], history: [] });
  await badR("同じ Id の相手", { rivals: [rival(1, "a"), rival(1, "b")], history: [] });
  await badR("Foes が配列でない履歴", { rivals: [], history: [{ Id: 1, Foes: {} }] });
  await badR("同じ Id の履歴", { rivals: [], history: [match(1, []), match(1, [])] });
  check("不正な PUT では my-rival の内容が変わらない", (await req("GET", R, rkey)).json?.revision === 2);
  check("my-pokemon の PUT は相手チームを消さない", (await req("PUT", P, rkey, { baseRevision: 2, dataSets: { Champions: { pokemon: [mon(1, "ガブリアス")] } } })).status === 200
    && same((await req("GET", R, rkey)).json?.dataSets?.Champions?.history, history1));
  check("DELETE /api/my/rival は 405", (await req("DELETE", R, rkey)).status === 405);

  // 試合の履歴（/api/my/history。SQLite の表。revision は増えない。テストでは HISTORY_LIMIT=3 で上限を小さくしている）
  const H = "/api/my/history";
  const hkey = newKey();
  const foe = (id, name) => ({ Id: id, PokemonId: 1, PokemonName: name });
  check("キーなしの history は 401", (await req("GET", `${H}?dataSet=Champions`)).status === 401);
  check("dataSet なしで id・limit・before を付けると 400", (await req("GET", `${H}?id=1`, hkey)).status === 400 && (await req("GET", `${H}?limit=5`, hkey)).status === 400);
  const noSets = await req("GET", H, hkey);
  check("dataSet の無い GET history は、履歴のあるゲームの一覧（空）", noSets.status === 200 && same(noSets.json?.dataSets, []), noSets.text);
  check("予約語の dataSet は 400", (await req("GET", `${H}?dataSet=__proto__`, hkey)).status === 400);
  const empty = await req("GET", `${H}?dataSet=Champions`, hkey);
  check("空の履歴は total 0・items []", empty.status === 200 && empty.json?.total === 0 && same(empty.json?.items, []) && empty.json?.hasMore === false, empty.text);
  const a1 = await req("POST", H, hkey, { dataSet: "Champions", match: { Date: "2026-10-21T00:00:00Z", Memo: "1", Foes: [foe(1, "a")] } });
  check("POST history は 201 で Id 1・total 1", a1.status === 201 && a1.json?.id === 1 && a1.json?.total === 1, a1.text);
  const a2 = await req("POST", H, hkey, { dataSet: "champions", match: { Memo: "2", Foes: [foe(1, "b")] } });
  check("大文字小文字違いのゲームも同じ履歴（Id 2・total 2）", a2.status === 201 && a2.json?.id === 2 && a2.json?.total === 2, a2.text);
  const a3 = await req("POST", H, hkey, { dataSet: "Champions", match: { Id: 99, Memo: "3", Foes: [] } });
  check("本文の Id は無視して連番を割り当てる（Id 3）", a3.status === 201 && a3.json?.id === 3, a3.text);
  const list = await req("GET", `${H}?dataSet=Champions`, hkey);
  check("一覧は新しい順で total 3", list.json?.total === 3 && same(list.json?.items?.map((m) => m.Id), [3, 2, 1]) && list.json?.items?.[2]?.Memo === "1", list.text);
  const page1 = await req("GET", `${H}?dataSet=Champions&limit=2`, hkey);
  check("limit=2 は 2 件で hasMore", same(page1.json?.items?.map((m) => m.Id), [3, 2]) && page1.json?.hasMore === true, page1.text);
  const page2 = await req("GET", `${H}?dataSet=Champions&limit=2&before=2`, hkey);
  check("before=2 で続き（Id 1）・hasMore なし", same(page2.json?.items?.map((m) => m.Id), [1]) && page2.json?.hasMore === false, page2.text);
  const one = await req("GET", `${H}?dataSet=Champions&id=2`, hkey);
  check("id 指定で 1 試合", one.status === 200 && one.json?.item?.Memo === "2" && one.json?.total === 3, one.text);
  check("無い Id は 404", (await req("GET", `${H}?dataSet=Champions&id=99`, hkey)).status === 404);
  check("limit=0 は 400", (await req("GET", `${H}?dataSet=Champions&limit=0`, hkey)).status === 400);
  check("履歴の追加では revision が増えない", (await req("GET", P, hkey)).json?.revision === 0);
  const sets3 = await req("GET", H, hkey);
  check("履歴のあるゲームの一覧（小文字のキーと件数）", same(sets3.json?.dataSets, [{ dataSet: "champions", total: 3, nextId: 4 }]), sets3.text);
  const full = await req("POST", H, hkey, { dataSet: "Champions", match: { Foes: [] } });
  check("上限（テストでは 3 件）に達していたら 409 history_limit", full.status === 409 && full.json?.error === "history_limit", full.text);
  const gen9Add = await req("POST", H, hkey, { dataSet: "Gen9", match: { Foes: [foe(2, "z")] } });
  check("上限はゲームごと（別のゲームは足せる）", gen9Add.status === 201 && gen9Add.json?.id === 1 && gen9Add.json?.total === 1, gen9Add.text);
  const badMatch = (name, body) => req("POST", H, hkey, body).then((r) => check(`${name} は 400`, r.status === 400, r.text));
  await badMatch("match が無い", { dataSet: "Gen9" });
  await badMatch("Foes が配列でない", { dataSet: "Gen9", match: { Foes: {} } });
  await badMatch("Memo が長すぎる", { dataSet: "Gen9", match: { Foes: [], Memo: "x".repeat(501) } });
  await badMatch("dataSet が不正", { dataSet: "a b", match: { Foes: [] } });
  check("JSON でない POST は 400", (await req("POST", H, hkey, "{ not json")).status === 400);
  check("履歴 1 試合の上限（64 KB）を超えると 400", (await req("POST", H, hkey, { dataSet: "Gen9", match: { Foes: [{ Note: "x".repeat(70 * 1024) }] } })).status === 400);
  check("本文全体の上限（256 KB）を超えると 413", (await req("POST", H, hkey, { dataSet: "Gen9", match: { Foes: [{ Note: "x".repeat(300 * 1024) }] } })).status === 413);

  // 古い版のクライアント向けの相手チーム API（履歴は別の表から返し、PUT の history は「次の Id 以降の、まだ無い試合」だけ足す）
  const hist = (r) => r.json?.dataSets?.Champions?.history?.map((m) => m.Id);
  const putR0 = await req("PUT", R, hkey, { baseRevision: 0, dataSets: { Champions: { rivals: [], history: [] } } });
  check("履歴があるゲームへの PUT my-rival は 200（履歴は置き換えない）", putR0.status === 200 && putR0.json?.revision === 1, putR0.text);
  const v1 = await req("GET", R, hkey);
  check("GET my-rival: 全履歴が古い順で、nextMatchId は 4", same(hist(v1), [1, 2, 3]) && v1.json?.dataSets?.Champions?.nextMatchId === 4, v1.text);
  const v2 = await req("GET", `${R}?historyLimit=0`, hkey);
  check("historyLimit=0 は履歴なし", same(hist(v2), []) && v2.json?.dataSets?.Champions?.nextMatchId === 4, v2.text);
  const v3 = await req("GET", `${R}?historyLimit=2`, hkey);
  check("historyLimit=2 は直近 2 件を古い順で", same(hist(v3), [2, 3]), v3.text);
  check("historyLimit が不正なら 400", (await req("GET", `${R}?historyLimit=-1`, hkey)).status === 400);
  const delOne = await req("DELETE", `${H}?dataSet=Champions&id=1`, hkey);
  check("DELETE id は 204、もう一度は 404", delOne.status === 204 && (await req("DELETE", `${H}?dataSet=Champions&id=1`, hkey)).status === 404);
  const stale = await req("PUT", R, hkey, { baseRevision: 1, dataSets: { Champions: { rivals: [], history: [match(1, []), match(2, [])] } } });
  check("古い版が消した試合（Id 1）を書き戻しても復活しない", stale.status === 200 && same(hist(await req("GET", R, hkey)), [2, 3]), stale.text);
  await req("DELETE", `${H}?dataSet=Champions&id=2`, hkey);
  const oldAdd = await req("PUT", R, hkey, { baseRevision: 2, dataSets: { Champions: { rivals: [], history: [{ ...match(4, []), Memo: "old" }] } } });
  const v4 = await req("GET", R, hkey);
  check("古い版が足した試合（次の Id 4）は入る", oldAdd.status === 200 && same(hist(v4), [3, 4]) && v4.json?.dataSets?.Champions?.history?.[1]?.Memo === "old", v4.text);
  const trim = await req("DELETE", `${H}?dataSet=Champions&keepLatest=1`, hkey);
  check("keepLatest=1 で新しい 1 件を残す", trim.status === 200 && trim.json?.deleted === 1 && trim.json?.total === 1
    && same((await req("GET", `${H}?dataSet=Champions`, hkey)).json?.items?.map((m) => m.Id), [4]), trim.text);
  check("DELETE に id も keepLatest も無いと 400", (await req("DELETE", `${H}?dataSet=Champions`, hkey)).status === 400);
  check("DELETE に id と keepLatest の両方があると 400", (await req("DELETE", `${H}?dataSet=Champions&id=1&keepLatest=1`, hkey)).status === 400);
  check("履歴の Id は使い回さない（消したあとに足すと Id 5）", (await req("POST", H, hkey, { dataSet: "Champions", match: { Foes: [] } })).json?.id === 5);
  check("PUT 相手チームに history を省略してもよい", (await req("PUT", R, hkey, { baseRevision: 3, dataSets: { Champions: { rivals: [] } } })).status === 200);
  // 共有解除（DELETE access-key）で履歴も消え、以後 410
  check("DELETE access-key は 204", (await req("DELETE", K, hkey)).status === 204);
  check("消したキーの履歴 GET は 410", (await req("GET", `${H}?dataSet=Champions`, hkey)).status === 410);
  check("消したキーの履歴 POST は 410", (await req("POST", H, hkey, { dataSet: "Champions", match: { Foes: [] } })).status === 410);
  check("消したキーの履歴 DELETE は 410", (await req("DELETE", `${H}?dataSet=Champions&id=5`, hkey)).status === 410);
  check("history に使えないメソッド（PUT）は 405", (await req("PUT", H, newKey(), {})).status === 405);

  // その他のパス・メソッド
  check("POST は 405", (await req("POST", P, key, {})).status === 405);
  check("DELETE /api/my/pokemon は 405", (await req("DELETE", P, key)).status === 405);
  check("GET /api/my/access-key は 405", (await req("GET", K, key)).status === 405);
  check("以前の /api/roster は 404", (await req("GET", "/api/roster", key)).status === 404);
  check("以前の /api/my-pokemon は 404", (await req("GET", "/api/my-pokemon", key)).status === 404);
  check("以前の /api/my-team は 404", (await req("GET", "/api/my-team", key)).status === 404);
  check("以前の DELETE /api/access-key は 404", (await req("DELETE", "/api/access-key", key)).status === 404);
  check("複数形の /api/my/pokemons は 404", (await req("GET", "/api/my/pokemons", key)).status === 404);
  check("/api/my そのものは 404", (await req("GET", "/api/my", key)).status === 404);
  check("/api/ の他のパスは 404", (await req("GET", "/api/other", key)).status === 404);
  const page = await fetch(`${base}/my-pokemon`);
  check("以前の URL（/my-teams）も静的アセット（SPA）", (await fetch(`${base}/my-teams`)).status === 200);
  check("API 以外は今までどおり静的アセット（SPA）", page.status === 200 && (page.headers.get("content-type") ?? "").includes("text/html"));

  // ゲームデータ（/api/game/*）は静的アセット。Worker を通して、そのまま返す（メンテナンス中だけ 503。external/fara-site-kit/worker/maintenance.test.mjs）
  // （Worker を通ると worker/index.js が X-Worker-Fallback を付けるので、それがあることで確かめる）
  const csv = await fetch(`${base}/api/game/datasets.csv`);
  const csvText = await csv.text();
  check("/api/game/datasets.csv は 200 で CSV（アクセスキーなし）", csv.status === 200 && (csv.headers.get("content-type") ?? "").includes("text/csv") && csvText.startsWith("Key,"), `${csv.status} ${csv.headers.get("content-type")}`);
  check("/api/game/* は Worker を通して、静的アセットをそのまま返す（X-Worker-Fallback がある）", csv.headers.get("x-worker-fallback") === "assets");
  const missingCsv = await fetch(`${base}/api/game/NoSuchGame/pokemon.csv`);
  const missingText = await missingCsv.text();
  check("無い /api/game/... は CSV として返らない（404 か SPA の index.html）",
    missingCsv.status === 404 || (missingCsv.status === 200 && (missingCsv.headers.get("content-type") ?? "").includes("text/html") && /<!doctype html|<html/i.test(missingText)),
    `${missingCsv.status} ${missingCsv.headers.get("content-type")}`);
  console.log(`     （参考）無い /api/game/... の応答: ${missingCsv.status} ${missingCsv.headers.get("content-type")}`);
  const oldData = await fetch(`${base}/data/datasets.csv`);
  check("以前の /data/datasets.csv は無い（404 か SPA の index.html）",
    oldData.status === 404 || (oldData.status === 200 && (oldData.headers.get("content-type") ?? "").includes("text/html")), `${oldData.status} ${oldData.headers.get("content-type")}`);

  // DELETE /api/my/access-key（キーの作り直し）
  const del = await req("DELETE", K, key);
  check("DELETE /api/my/access-key は 204", del.status === 204);
  check("DELETE 後の GET my-pokemon は 410", (await req("GET", P, key)).status === 410);
  check("DELETE 後の GET my-team は 410", (await req("GET", T, key)).status === 410);
  check("DELETE 後の GET my-rival は 410", (await req("GET", R, key)).status === 410);
  check("DELETE 後の PUT my-pokemon は 410", (await req("PUT", P, key, { baseRevision: 0, dataSets: { Champions: { pokemon: [] } } })).status === 410);
  check("DELETE 後の PUT my-team は 410", (await req("PUT", T, key, { baseRevision: 0, dataSets: { Champions: { teams: [], activeTeam: null } } })).status === 410);
  check("DELETE はもう一度呼んでも 204", (await req("DELETE", K, key)).status === 204);
  // 履歴の量の上限（テストでは合計 1,500 バイト・ゲーム 16 まで）と、古い版のクライアント向けの PUT
  const bkey = newKey();
  const bigMatch = (n) => ({ dataSet: "Champions", match: { Foes: [{ Note: "x".repeat(n) }] } });
  check("合計サイズの上限: 1 つ目は足せる", (await req("POST", H, bkey, bigMatch(600))).status === 201);
  check("合計サイズの上限: 2 つ目も足せる", (await req("POST", H, bkey, bigMatch(600))).status === 201);
  const over = await req("POST", H, bkey, bigMatch(600));
  check("合計サイズの上限（1,500 バイト）を超えるときは 409 history_limit", over.status === 409 && over.json?.error === "history_limit", over.text);
  check("1 件消すと、合計サイズが減って、また足せる", (await req("DELETE", `${H}?dataSet=Champions&id=1`, bkey)).status === 204 && (await req("POST", H, bkey, bigMatch(600))).status === 201);
  check("keepLatest で消しても合計サイズが減る", (await req("DELETE", `${H}?dataSet=Champions&keepLatest=1`, bkey)).json?.total === 1 && (await req("POST", H, bkey, bigMatch(600))).status === 201);
  const dkey = newKey();
  let dOk = true;
  for (let i = 1; i <= 16; i++) dOk = dOk && (await req("POST", H, dkey, { dataSet: `d${i}`, match: { Foes: [] } })).status === 201;
  check("履歴を持てるゲームは 16 まで（16 個は足せる）", dOk);
  const d17 = await req("POST", H, dkey, { dataSet: "d17", match: { Foes: [] } });
  check("17 個目のゲームは 409 history_limit", d17.status === 409 && d17.json?.error === "history_limit", d17.text);
  check("履歴のあるゲームには、続けて足せる", (await req("POST", H, dkey, { dataSet: "d1", match: { Foes: [] } })).status === 201);

  // まとめて足す（データの移し替え用）
  const mkey = newKey();
  const batch = await req("POST", H, mkey, { dataSet: "Gen9", matches: [{ Memo: "a", Foes: [] }, { Memo: "b", Foes: [] }] });
  check("matches でまとめて足すと、古い順に Id が振られる（1・2）", batch.status === 201 && same(batch.json?.ids, [1, 2]) && batch.json?.total === 2, batch.text);
  const batch2 = await req("POST", H, mkey, { dataSet: "Gen9", matches: [{ Foes: [] }, { Foes: [] }] });
  check("上限（テストでは 3 件）を超えるまとめ追加は、何も足さずに 409", batch2.status === 409 && (await req("GET", `${H}?dataSet=Gen9`, mkey)).json?.total === 2, batch2.text);
  check("match と matches の両方は 400", (await req("POST", H, mkey, { dataSet: "Gen9", match: { Foes: [] }, matches: [{ Foes: [] }] })).status === 400);
  check("空の matches は 400", (await req("POST", H, mkey, { dataSet: "Gen9", matches: [] })).status === 400);
  check("101 件の matches は 400", (await req("POST", H, mkey, { dataSet: "Gen9", matches: Array.from({ length: 101 }, () => ({ Foes: [] })) })).status === 400);
  check("matches の中の不正な試合は 400", (await req("POST", H, mkey, { dataSet: "Gen9", matches: [{ Foes: {} }] })).status === 400);

  const ikey = newKey();
  await req("POST", H, ikey, { dataSet: "Champions", match: { Foes: [] } });   // Id 1（次の Id は 2）
  const ig = await req("PUT", R, ikey, { baseRevision: 0, dataSets: { Champions: { rivals: [], history: [match(1, []), match(5000, [])] } } });
  check("古い版の PUT: すでにある試合は数えず、離れすぎた Id（5000）は足さずに historyIgnored で知らせる", ig.status === 200 && ig.json?.historyIgnored === 1, ig.text);
  const ig2 = await req("PUT", R, ikey, { baseRevision: 1, dataSets: { Champions: { rivals: [], history: [match(2, []), match(3, [])] } } });
  check("古い版の PUT: 次の Id 以降（2・3）は足される", ig2.status === 200 && ig2.json?.historyIgnored === undefined && (await req("GET", `${H}?dataSet=Champions`, ikey)).json?.total === 3, ig2.text);
  const ig3 = await req("PUT", R, ikey, { baseRevision: 2, dataSets: { Champions: { rivals: [], history: [match(4, [])] } } });
  check("古い版の PUT: 上限（3 件）で足せなければ historyIgnored", ig3.status === 200 && ig3.json?.historyIgnored === 1, ig3.text);
  const badOld = (name, m) => req("PUT", R, ikey, { baseRevision: 3, dataSets: { Champions: { rivals: [], history: [m] } } }).then((r) => check(`古い版の PUT: ${name} は 400`, r.status === 400, r.text));
  await badOld("Foes が 51 体", { Id: 9, Foes: Array.from({ length: 51 }, () => ({})) });
  await badOld("Memo が長すぎる", { Id: 9, Foes: [], Memo: "x".repeat(501) });
  await badOld("1 試合が 64 KB を超える", { Id: 9, Foes: [{ Note: "x".repeat(70 * 1024) }] });
  const defaultView = await req("GET", R, ikey);
  check("historyLimit なしの GET my-rival は直近（最大 200）件を古い順で返す", same(defaultView.json?.dataSets?.Champions?.history?.map((m) => m.Id), [1, 2, 3]), defaultView.text);

  // 文書が 256 KB を超えて 413 になるとき、履歴にも何も書かない
  const ukey = newKey();
  check("413 の準備: 150 KB の登録ポケモン", (await req("PUT", P, ukey, { baseRevision: 0, dataSets: { Champions: { pokemon: [{ ...mon(1, "a"), Note: "x".repeat(150000) }] } } })).status === 200);
  const tooBig = await req("PUT", R, ukey, { baseRevision: 1, dataSets: { Champions: { rivals: [{ ...rival(1, "b"), Note: "x".repeat(150000) }], history: [match(1, [])] } } });
  check("文書が 256 KB を超える PUT my-rival は 413", tooBig.status === 413, tooBig.text);
  const after = await req("GET", `${H}?dataSet=Champions`, ukey);
  check("413 のとき、履歴は足されない", after.json?.total === 0 && (await req("GET", R, ukey)).json?.revision === 1, after.text);

  // 重複判定用の ClientId（応答が届かずにやり直しても、二重に記録されない）
  const ckey = newKey();
  const withId = (clientId, extra = {}) => ({ dataSet: "Champions", match: { ClientId: clientId, Foes: [], ...extra } });
  const c1 = await req("POST", H, ckey, withId("end:1,2"));
  check("ClientId 付きの POST は 201", c1.status === 201 && c1.json?.id === 1 && c1.json?.duplicate === undefined, c1.text);
  const c1b = await req("POST", H, ckey, withId("end:1,2", { Memo: "やり直し" }));
  check("同じ ClientId の POST は、足さずに 200 で同じ Id と duplicate:true", c1b.status === 200 && c1b.json?.id === 1 && c1b.json?.duplicate === true && c1b.json?.total === 1, c1b.text);
  check("やり直しで内容は書き換わらない・件数も増えない", (await req("GET", `${H}?dataSet=Champions&id=1`, ckey)).json?.item?.Memo === undefined && (await req("GET", `${H}?dataSet=Champions`, ckey)).json?.total === 1);
  const c2 = await req("POST", H, ckey, withId("end:3"));
  check("別の ClientId は新しい試合（Id 2）", c2.status === 201 && c2.json?.id === 2, c2.text);
  const c3 = await req("POST", H, ckey, { dataSet: "Gen9", match: { ClientId: "end:1,2", Foes: [] } });
  check("ClientId はゲームごと（別のゲームなら同じ ClientId も足せる）", c3.status === 201 && c3.json?.id === 1, c3.text);
  await req("POST", H, ckey, withId("end:4"));   // Champions は 3 件（テストの上限）
  const atLimit = await req("POST", H, ckey, withId("end:1,2"));
  check("上限に達していても、重複なら 409 ではなく 200 で既存の Id", atLimit.status === 200 && atLimit.json?.duplicate === true && atLimit.json?.id === 1, atLimit.text);
  check("上限に達していて、新しい ClientId なら 409", (await req("POST", H, ckey, withId("end:5"))).status === 409);
  check("試合を消すと、その ClientId はまた使える（新しい Id）", (await req("DELETE", `${H}?dataSet=Champions&id=1`, ckey)).status === 204 && (await req("POST", H, ckey, withId("end:1,2"))).json?.id === 4);
  const badClient = (name, value) => req("POST", H, ckey, withId(value)).then((r) => check(`ClientId ${name} は 400`, r.status === 400, r.text));
  await badClient("が空文字", "");
  await badClient("が 101 文字", "x".repeat(101));
  await badClient("が数値", 5);
  check("ClientId が 100 文字までなら使える", (await req("POST", H, ckey, { dataSet: "Gen9", match: { ClientId: "y".repeat(100), Foes: [] } })).status === 201);

  // 履歴の状態を確かめてから消す（キーの作り直しで、写している間の変化を取りこぼさない）
  const guardKey = newKey();
  await req("POST", H, guardKey, { dataSet: "Champions", match: { Foes: [] } });
  await req("POST", H, guardKey, { dataSet: "Champions", match: { Foes: [] } });
  const guardState = await req("GET", H, guardKey);
  check("履歴のあるゲームの一覧に、次の Id（3）が入る", same(guardState.json?.dataSets, [{ dataSet: "champions", total: 2, nextId: 3 }]), guardState.text);
  const guardBad = await req("DELETE", `${K}?expect=champions:2:2`, guardKey);
  check("状態が違えば（次の Id が違う）消さずに 409 changed と今の状態を返す", guardBad.status === 409 && guardBad.json?.error === "changed" && same(guardBad.json?.dataSets, [{ dataSet: "champions", total: 2, nextId: 3 }]), guardBad.text);
  check("消されていない（履歴も登録も読める）", (await req("GET", `${H}?dataSet=Champions`, guardKey)).json?.total === 2);
  await req("POST", H, guardKey, { dataSet: "Champions", match: { Foes: [] } });   // 写している間に、別の端末が足した
  check("件数が違えば 409", (await req("DELETE", `${K}?expect=champions:2:3`, guardKey)).status === 409);
  const guardNow = (await req("GET", H, guardKey)).json?.dataSets;
  const guardOk = `${guardNow[0].dataSet}:${guardNow[0].total}:${guardNow[0].nextId}`;
  check("今の状態と同じなら消す（204）", (await req("DELETE", `${K}?expect=${guardOk}`, guardKey)).status === 204);
  check("消したあとは 410 で、履歴も無い", (await req("GET", `${H}?dataSet=Champions`, guardKey)).status === 410);
  check("消えているキーへの、expect 付きの 2 回目の DELETE も 204", (await req("DELETE", `${K}?expect=champions:9:9`, guardKey)).status === 204);
  const guardNone = newKey();
  check("履歴が無いキーは expect= （空）で消せる", (await req("DELETE", `${K}?expect=`, guardNone)).status === 204);
  const guardBadKey = newKey();
  check("expect の書式が違うと 400", (await req("DELETE", `${K}?expect=abc`, guardBadKey)).status === 400 && (await req("DELETE", `${K}?expect=a:1:2,a:1:2`, guardBadKey)).status === 400);
  check("書式が違うときは、消えない（まだ使える）", (await req("GET", P, guardBadKey)).status === 200);

  // 1 年アクセスが無い置き場の自動削除（テストでは EXPIRE_MS=5000。アラームで消える）
  const ekey = newKey();
  const rkey2 = newKey();
  check("自動削除の準備: 履歴を足す", (await req("POST", H, ekey, { dataSet: "Champions", match: { Foes: [] } })).status === 201);
  check("自動削除の準備: ポケモンを登録する", (await req("PUT", P, ekey, { baseRevision: 0, dataSets: { Champions: { pokemon: [mon(1, "ガブリアス")] } } })).status === 200);
  await req("POST", H, rkey2, { dataSet: "Champions", match: { Foes: [] } });
  check("自動削除の確認用に、消したキーも作る", (await req("DELETE", K, rkey2)).status === 204);
  await new Promise((r) => setTimeout(r, 9000));
  const expired = await req("GET", `${H}?dataSet=Champions`, ekey);
  check("期間（5 秒）アクセスが無いと、履歴が消える", expired.status === 200 && expired.json?.total === 0, expired.text);
  check("ポケモンの登録も消える（revision 0・空）", (await req("GET", P, ekey)).json?.revision === 0 && same((await req("GET", P, ekey)).json?.dataSets, {}));
  const reborn = await req("POST", H, ekey, { dataSet: "Champions", match: { Foes: [] } });
  check("消えたあとは新しい置き場として使える（Id 1 から）", reborn.status === 201 && reborn.json?.id === 1, reborn.text);
  check("作り直し・共有解除で消したキーは、期間が過ぎても 410 のまま", (await req("GET", P, rkey2)).status === 410);

} catch (e) {
  console.error(e);
  failures++;
} finally {
  // npx の子プロセス（workerd）ごと止める
  try {
    if (process.platform === "win32") spawn("taskkill", ["/pid", String(wrangler.pid), "/T", "/F"]);
    else process.kill(-wrangler.pid, "SIGTERM");
  } catch {}
  await new Promise((r) => setTimeout(r, 500));
  rmSync(persist, { recursive: true, force: true });
}

console.log(failures === 0 ? "\nすべて OK" : `\n${failures} 件失敗`);
if (failures > 0) console.log("\n--- wrangler の出力（末尾） ---\n" + output.slice(-3000));
process.exit(failures === 0 ? 0 : 1);
