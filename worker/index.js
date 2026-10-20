// 登録データ（ポケモン・チーム）のデータ共有 API。
//
// サイト本体（静的アセット）は今までどおり Cloudflare が配信し、/api/my/* だけがこのスクリプトを通る
// （wrangler.jsonc の assets.run_worker_first）。ゲームデータ（CSV）の /api/game/* は静的アセットで、ここには来ない。
//
// 登録データはゲーム（データセット: Champions・Gen9 など）ごとに分かれているので、本文の dataSets はゲームのキーごとのオブジェクト。
//
//   GET    /api/my/pokemon → 200 {"revision": n, "dataSets": {"Champions": {"nextId": n, "pokemon": [...]}, ...}}
//   PUT    /api/my/pokemon ← {"baseRevision": n, "dataSets": {"Champions": {"pokemon": [...], "nextId"?: n}}}
//                           → 200 {"revision": n+1} / 409 {"revision": 現在, "dataSets": 現在}
//          書いたゲームのポケモンだけを置き換える（書かなかったゲームはそのまま）。同じ処理の中で、消えたポケモンをそのゲームの
//          すべてのチームから外し、0 体になったチームを消し、使用中のチームが消えたら使用中を解除する。
//   GET    /api/my/team    → 200 {"revision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n | null}, ...}}
//   PUT    /api/my/team    ← {"baseRevision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n | null}}}
//                           → 200 {"revision": n+1} / 409 {"revision": 現在, "dataSets": 現在}
//          書いたゲームのチームと使用中チームだけを置き換える。登録に無いポケモン・7 体以上・使用中のチームが無いなどは 400。
//   GET    /api/my/rival   → 200 {"revision": n, "dataSets": {"Champions": {"nextId": n, "rivals": [...], "nextMatchId": n, "history": [...]}, ...}}
//   PUT    /api/my/rival   ← {"baseRevision": n, "dataSets": {"Champions": {"rivals": [...], "nextId"?: n, "history": [...], "nextMatchId"?: n}}}
//                           → 200 {"revision": n+1} / 409 {"revision": 現在, "dataSets": 現在}
//          対戦中の相手チーム（rivals）と、終わった試合の記録（history。1 試合に相手のポケモンの配列 Foes）を、書いたゲームの分だけ置き換える。
//   GET    /api/my/rival?historyLimit=n → history を直近 n 件（新しい試合から数えて、古い順に並べる）にする。n=0 なら空。
//          指定しなければ従来どおり全件（古い版のクライアント用）。PUT の応答（409 の本文）にも同じ指定が使える。
//   PUT    /api/my/rival の history は省略できる。あるときは「知らない Id の試合を足す」だけで、消したり置き換えたりはしない
//          （古い版のクライアントが書き戻しても、履歴が消えない）。履歴の本体は下の /api/my/history。
//   GET    /api/my/history?dataSet=K[&limit=n][&before=Id]  → 200 {"total": 件数, "items": [試合…新しい順], "hasMore": bool}
//   GET    /api/my/history?dataSet=K&id=n                   → 200 {"total": 件数, "item": 試合} / 404
//   POST   /api/my/history ← {"dataSet": "K", "match": {"Date"?, "Memo"?, "Foes": [...]}}
//                           → 201 {"id": 割り当てた Id, "total": 件数} / 409 history_limit（ゲームごとに 10,000 件まで）
//   DELETE /api/my/history?dataSet=K&id=n                   → 204 / 404（1 試合を消す）
//   DELETE /api/my/history?dataSet=K&keepLatest=n           → 200 {"deleted": 消した数, "total": 件数}（新しい n 件を残して消す）
//          履歴はポケモン・チーム・相手チームとは別の表（SQLite）に 1 試合 1 行で持つ。履歴の変更では revision は増えない。
//   DELETE /api/my/access-key  → 204（アクセスキーの作り直し・データ共有の解除で、キーを使えなくしてその置き場（履歴も）を消す。以後そのキーは 410）
//
// revision は登録データ全体で 1 つ（どの PUT でも増える）。
// 認証は "Authorization: Bearer <アクセスキー>"。アクセスキーは 256 bit の乱数を base64url（パディングなし・43 文字）にしたもの。
// サーバーはキーそのものを保存しない。SHA-256(キー) の 16 進を Durable Object の名前にして、キーごとに 1 つの置き場を持つ。
// キーはログ・エラーメッセージ・URL に出さない（このファイルでは console.* を使わない）。
//
// 1 年間アクセスが無い置き場は、Durable Object のアラームで自動的に消す（アクセスすると延長。延長の書き込みは 1 日に 1 回まで）。

import { DurableObject } from "cloudflare:workers";

/** パス → 使えるメソッド。 */
const ROUTES = {
  "/api/my/pokemon": ["GET", "PUT"],
  "/api/my/team": ["GET", "PUT"],
  "/api/my/rival": ["GET", "PUT"],
  "/api/my/history": ["GET", "POST", "DELETE"],
  "/api/my/access-key": ["DELETE"],
};
const KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/;
/** ゲーム（データセット）のキー。 */
const DATA_SET_PATTERN = /^[A-Za-z0-9_-]{1,32}$/;
/** オブジェクトのキーに使うと、プロトタイプを書き換えてしまう名前（ゲームのキーにはできない）。 */
const RESERVED_KEYS = new Set(["__proto__", "constructor", "prototype"]);
/** ID の最大値。クライアント（C#）は int で持つので、次の ID（最大 + 1）も int に収まるようにする。 */
const MAX_ID = 2147483646;
/** PUT の本文と、保存する登録データ全体の上限（UTF-8 のバイト数）。 */
const MAX_BYTES = 256 * 1024;
/** 1 チームの上限。 */
const MAX_TEAM_SIZE = 6;
/** 試合の履歴の上限（アクセスキー 1 つ・ゲーム 1 つあたり）。 */
const MAX_HISTORY_PER_DATA_SET = 10000;
/** 履歴 1 試合の本文の上限（UTF-8 のバイト数）と、相手の人数の上限。 */
const MAX_MATCH_BYTES = 64 * 1024;
const MAX_FOES_PER_MATCH = 50;
/** 履歴の一覧で 1 回に返す件数（既定・最大）。 */
const HISTORY_PAGE_DEFAULT = 50;
const HISTORY_PAGE_MAX = 200;
/** アクセスの無い置き場を消すまでの期間と、「最後のアクセス」を更新する間隔（書き込みを減らすため）。 */
const EXPIRE_MS = 365 * 24 * 60 * 60 * 1000;
const TOUCH_INTERVAL_MS = 24 * 60 * 60 * 1000;

const BASE_HEADERS = {
  "Cache-Control": "no-store",
  "X-Content-Type-Options": "nosniff",
};

function json(status, body, extraHeaders) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: {
      ...BASE_HEADERS,
      ...(body === undefined ? {} : { "Content-Type": "application/json; charset=utf-8" }),
      ...extraHeaders,
    },
  });
}

function error(status, code, message, extraHeaders) {
  return json(status, { error: code, message }, extraHeaders);
}

function tooLarge() {
  return error(413, "too_large", `データが大きすぎます（上限 ${MAX_BYTES / 1024} KB）`);
}

/** "Bearer <key>" から形式の正しいキーを取り出す。正しくなければ null。 */
function readKey(request) {
  const header = request.headers.get("Authorization");
  if (!header) return null;
  const match = /^Bearer ([^\s]+)$/i.exec(header);
  if (!match) return null;
  return KEY_PATTERN.test(match[1]) ? match[1] : null;
}

async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

class TooLargeError extends Error {}

/** 本文を上限付きで読む（上限を超えたら読むのをやめる）。 */
async function readBodyLimited(request, limit) {
  const declared = request.headers.get("Content-Length");
  if (declared !== null && Number(declared) > limit) throw new TooLargeError();
  if (!request.body) return new Uint8Array(0);
  const reader = request.body.getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > limit) {
      await reader.cancel().catch(() => {});
      throw new TooLargeError();
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const c of chunks) {
    bytes.set(c, offset);
    offset += c.byteLength;
  }
  return bytes;
}

function isPlainObject(v) {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function isId(v) {
  return Number.isSafeInteger(v) && v >= 1 && v <= MAX_ID;
}

/** 次に振る ID（最大 + 1）。ID の最大値の次まで。 */
function isNextId(v) {
  return Number.isSafeInteger(v) && v >= 1 && v <= MAX_ID + 1;
}

/**
 * 検証の失敗（400）。Durable Object の呼び出しをまたぐと例外の型が分からなくなるので、
 * 投げた側（同じ実行環境の中）で {@link catchInvalid} によって 400 の応答に直す。
 */
class InvalidError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

function invalid(code, message) {
  throw new InvalidError(code, message);
}

/** InvalidError を 400 の応答に直す。 */
async function catchInvalid(action) {
  try {
    return await action();
  } catch (e) {
    if (e instanceof InvalidError) return error(400, e.code, e.message);
    throw e;
  }
}

/** PUT の本文を検証して {baseRevision, dataSets} を返す（dataSets の中身は各リソースの検証で見る）。 */
function parsePutBody(bytes) {
  let body;
  try {
    body = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  } catch {
    invalid("invalid_json", "本文が JSON ではありません");
  }
  if (!isPlainObject(body)) invalid("invalid_body", "本文はオブジェクトにしてください");
  const { baseRevision, dataSets } = body;
  if (!Number.isSafeInteger(baseRevision) || baseRevision < 0)
    invalid("invalid_revision", "baseRevision は 0 以上の整数にしてください");
  if (!isPlainObject(dataSets)) invalid("invalid_data_sets", "dataSets はゲームのキーごとのオブジェクトにしてください");
  const keys = Object.keys(dataSets);
  if (keys.length === 0) invalid("invalid_data_sets", "dataSets に 1 つ以上のゲームを入れてください");
  const seen = new Set();
  for (const key of keys) {
    if (!DATA_SET_PATTERN.test(key) || RESERVED_KEYS.has(key.toLowerCase())) invalid("invalid_data_set", "ゲームのキーは英数字・-・_（32 文字まで）にしてください");
    const lower = key.toLowerCase();
    if (seen.has(lower)) invalid("invalid_data_set", `同じゲームが 2 回あります: ${key}`);
    seen.add(lower);
    if (!isPlainObject(dataSets[key])) invalid("invalid_data_set", `${key} はオブジェクトにしてください`);
  }
  return { baseRevision, dataSets };
}

/** my-pokemon の 1 ゲーム分を検証して {pokemon, nextId} を返す。 */
function validatePokemons(key, value) {
  const { pokemon, nextId } = value;
  if (!Array.isArray(pokemon)) invalid("invalid_pokemon", `${key}.pokemon は配列にしてください`);
  if (nextId !== undefined && nextId !== null && !isNextId(nextId))
    invalid("invalid_next_id", `${key}.nextId は 1 以上の整数にしてください`);
  const ids = new Set();
  for (const p of pokemon) {
    if (!isPlainObject(p)) invalid("invalid_pokemon", `${key}.pokemon の要素はオブジェクトにしてください`);
    if (!isId(p.Id)) invalid("invalid_pokemon", `${key}.pokemon の Id は 1 以上の整数にしてください`);
    if (ids.has(p.Id)) invalid("duplicate_pokemon", `${key}.pokemon に同じ Id（${p.Id}）が 2 つあります`);
    ids.add(p.Id);
  }
  return { pokemon, nextId: nextId ?? null };
}

/** my-rival の 1 ゲーム分を検証して {rivals, nextId, history, nextMatchId} を返す。 */
function validateRivals(key, value) {
  const { rivals, nextId, history, nextMatchId } = value;
  if (!Array.isArray(rivals)) invalid("invalid_rivals", `${key}.rivals は配列にしてください`);
  if (history !== undefined && history !== null && !Array.isArray(history)) invalid("invalid_history", `${key}.history は配列にしてください`);
  for (const [name, v] of [["nextId", nextId], ["nextMatchId", nextMatchId]]) {
    if (v !== undefined && v !== null && !isNextId(v)) invalid("invalid_next_id", `${key}.${name} は 1 以上の整数にしてください`);
  }
  const ids = new Set();
  for (const r of rivals) {
    if (!isPlainObject(r) || !isId(r.Id)) invalid("invalid_rival", `${key}.rivals の要素は Id（1 以上の整数）を持つオブジェクトにしてください`);
    if (ids.has(r.Id)) invalid("duplicate_rival", `${key}.rivals に同じ Id（${r.Id}）が 2 つあります`);
    ids.add(r.Id);
  }
  const matchIds = new Set();
  for (const m of history ?? []) {
    if (!isPlainObject(m) || !isId(m.Id)) invalid("invalid_history", `${key}.history の要素は Id（1 以上の整数）を持つオブジェクトにしてください`);
    if (matchIds.has(m.Id)) invalid("duplicate_match", `${key}.history に同じ Id（${m.Id}）が 2 つあります`);
    matchIds.add(m.Id);
    if (!Array.isArray(m.Foes)) invalid("invalid_history", `${key}.history の Id ${m.Id} の Foes は配列にしてください`);
  }
  return { rivals, nextId: nextId ?? null, history: history ?? null, nextMatchId: nextMatchId ?? null };
}

/** ゲームのキー（クエリ・本文から来たもの）を検証する。 */
function checkDataSet(value) {
  if (typeof value !== "string" || !DATA_SET_PATTERN.test(value) || RESERVED_KEYS.has(value.toLowerCase()))
    invalid("invalid_data_set", "ゲームのキーは英数字・-・_（32 文字まで）にしてください");
  return value;
}

/** クエリの整数（無ければ undefined）。 */
function queryInt(url, name, min, max) {
  const raw = url.searchParams.get(name);
  if (raw === null) return undefined;
  const n = Number(raw);
  if (!/^\d+$/.test(raw) || !Number.isSafeInteger(n) || n < min || n > max) invalid("invalid_query", `${name} は ${min} 以上 ${max} 以下の整数にしてください`);
  return n;
}

/** 履歴に足す 1 試合を検証する（Id は割り当てるので受け付けない）。 */
function validateMatch(value) {
  if (!isPlainObject(value)) invalid("invalid_match", "match はオブジェクトにしてください");
  const { Foes, Date: date, Memo } = value;
  if (!Array.isArray(Foes)) invalid("invalid_match", "match.Foes は配列にしてください");
  if (Foes.length > MAX_FOES_PER_MATCH) invalid("invalid_match", `match.Foes は ${MAX_FOES_PER_MATCH} 体までにしてください`);
  if (!Foes.every(isPlainObject)) invalid("invalid_match", "match.Foes の要素はオブジェクトにしてください");
  if (date !== undefined && (typeof date !== "string" || date.length > 64)) invalid("invalid_match", "match.Date は 64 文字までの文字列にしてください");
  if (Memo !== undefined && (typeof Memo !== "string" || Memo.length > 500)) invalid("invalid_match", "match.Memo は 500 文字までの文字列にしてください");
  const { Id: _ignored, ...rest } = value;
  return rest;
}

/** my-team の 1 ゲーム分を、そのゲームの登録ポケモン（ID の集合）と突き合わせて検証する。 */
function validateTeams(key, value, pokemonIds) {
  const { teams, activeTeam } = value;
  if (!Array.isArray(teams)) invalid("invalid_teams", `${key}.teams は配列にしてください`);
  const numbers = new Set();
  for (const t of teams) {
    if (!isPlainObject(t)) invalid("invalid_team", `${key}.teams の要素はオブジェクトにしてください`);
    if (!isId(t.Number)) invalid("invalid_team", `${key}.teams の Number は 1 以上の整数にしてください`);
    if (numbers.has(t.Number)) invalid("duplicate_team", `${key} にチーム${t.Number}が 2 つあります`);
    numbers.add(t.Number);
    if (!Array.isArray(t.Slots)) invalid("invalid_team", `${key} のチーム${t.Number}の Slots は配列にしてください`);
    if (t.Slots.length === 0) invalid("empty_team", `${key} のチーム${t.Number}が 0 体です（空のチームは送らずに消してください）`);
    if (t.Slots.length > MAX_TEAM_SIZE)
      invalid("team_too_large", `${key} のチーム${t.Number}が ${t.Slots.length} 体います（${MAX_TEAM_SIZE} 体まで）`);
    const entries = new Set();
    for (const s of t.Slots) {
      if (!isPlainObject(s) || !isId(s.EntryId))
        invalid("invalid_slot", `${key} のチーム${t.Number}の要素は EntryId（1 以上の整数）を持つオブジェクトにしてください`);
      if (s.ItemId !== undefined && s.ItemId !== null && !isId(s.ItemId))
        invalid("invalid_slot", `${key} のチーム${t.Number}の ItemId は 1 以上の整数か null にしてください`);
      if (!pokemonIds.has(s.EntryId))
        invalid("unknown_pokemon", `${key} のチーム${t.Number}に、登録に無いポケモン（Id ${s.EntryId}）が入っています`);
      if (entries.has(s.EntryId)) invalid("duplicate_slot", `${key} のチーム${t.Number}に同じポケモン（Id ${s.EntryId}）が 2 回入っています`);
      entries.add(s.EntryId);
    }
  }
  if (activeTeam !== undefined && activeTeam !== null) {
    if (!isId(activeTeam)) invalid("invalid_active_team", `${key}.activeTeam はチーム番号か null にしてください`);
    if (!numbers.has(activeTeam)) invalid("unknown_active_team", `${key}.activeTeam のチーム${activeTeam}がありません`);
  }
  return { teams, activeTeam: activeTeam ?? null };
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (!url.pathname.startsWith("/api/") || url.pathname.startsWith("/api/game/")) {
      // run_worker_first の対象外（通常は来ない。/api/game/* はゲームデータの静的アセット）。静的アセットに任せる。
      // ここを通ったことが分かるようにヘッダーを付ける（tools/worker-api-test.mjs が「付いていない」ことを確かめる）
      const response = await env.ASSETS.fetch(request);
      const passed = new Response(response.body, response);
      passed.headers.set("X-Worker-Fallback", "assets");
      return passed;
    }
    try {
      return await catchInvalid(() => handleApi(request, env, url));
    } catch {
      // 詳細（キーを含む可能性がある）は返さない
      return error(500, "internal_error", "サーバーでエラーが発生しました");
    }
  },
};

async function handleApi(request, env, url) {
  const methods = ROUTES[url.pathname];
  if (!methods) return error(404, "not_found", "見つかりません");
  const method = request.method;
  if (!methods.includes(method)) return error(405, "method_not_allowed", "使えないメソッドです", { Allow: methods.join(", ") });

  const key = readKey(request);
  if (key === null)
    return error(401, "unauthorized", "アクセスキーが正しくありません", { "WWW-Authenticate": 'Bearer realm="pokemon"' });

  // Durable Object にはキーを渡さない（名前は SHA-256 のみ）
  const stub = env.ROSTER.get(env.ROSTER.idFromName(await sha256Hex(key)));

  if (url.pathname === "/api/my/access-key") return respond(stub.retire());
  if (url.pathname === "/api/my/history") return handleHistory(request, url, stub);
  const resource = url.pathname === "/api/my/pokemon" ? "pokemon" : url.pathname === "/api/my/rival" ? "rival" : "teams";
  const historyLimit = resource === "rival" ? queryInt(url, "historyLimit", 0, MAX_HISTORY_PER_DATA_SET) : undefined;
  if (method === "GET") return respond(stub.getResource(resource, historyLimit));

  let bytes;
  try {
    bytes = await readBodyLimited(request, MAX_BYTES);
  } catch (e) {
    if (e instanceof TooLargeError) return tooLarge();
    throw e;
  }
  const { baseRevision, dataSets } = parsePutBody(bytes);
  if (resource === "pokemon") {
    const values = {};
    for (const [k, v] of Object.entries(dataSets)) values[k] = validatePokemons(k, v);
    return respond(stub.putPokemons(baseRevision, values));
  }
  if (resource === "rival") {
    const values = {};
    for (const [k, v] of Object.entries(dataSets)) values[k] = validateRivals(k, v);
    return respond(stub.putRivals(baseRevision, values, historyLimit));
  }
  // チームは登録ポケモンとの突き合わせが要るので、Durable Object の中で検証する
  return respond(stub.putTeams(baseRevision, dataSets));
}

/** /api/my/history（試合の履歴）。 */
async function handleHistory(request, url, stub) {
  if (request.method === "POST") return postHistory(request, stub);
  const dataSet = checkDataSet(url.searchParams.get("dataSet"));
  if (request.method === "GET") {
    const id = queryInt(url, "id", 1, MAX_ID);
    if (id !== undefined) return respond(stub.getMatch(dataSet, id));
    const limit = queryInt(url, "limit", 1, HISTORY_PAGE_MAX) ?? HISTORY_PAGE_DEFAULT;
    return respond(stub.listHistory(dataSet, limit, queryInt(url, "before", 1, MAX_ID + 1)));
  }
  if (request.method === "DELETE") {
    const id = queryInt(url, "id", 1, MAX_ID);
    const keepLatest = queryInt(url, "keepLatest", 0, MAX_HISTORY_PER_DATA_SET);
    if ((id === undefined) === (keepLatest === undefined)) invalid("invalid_query", "id か keepLatest のどちらか 1 つを指定してください");
    return respond(id !== undefined ? stub.removeMatch(dataSet, id) : stub.trimHistory(dataSet, keepLatest));
  }
  // 使えないメソッドは handleApi で弾いているので、ここには来ない
  return error(405, "method_not_allowed", "使えないメソッドです");
}

/** POST /api/my/history: 試合を 1 つ足す（ゲームのキーは本文の dataSet）。 */
async function postHistory(request, stub) {
  let bytes;
  try {
    bytes = await readBodyLimited(request, MAX_MATCH_BYTES);
  } catch (e) {
    if (e instanceof TooLargeError) return error(413, "too_large", `1 試合のデータが大きすぎます（上限 ${MAX_MATCH_BYTES / 1024} KB）`);
    throw e;
  }
  let body;
  try {
    body = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  } catch {
    invalid("invalid_json", "本文が JSON ではありません");
  }
  if (!isPlainObject(body)) invalid("invalid_body", "本文はオブジェクトにしてください");
  return respond(stub.addMatch(checkDataSet(body.dataSet), validateMatch(body.match)));
}

/** 保存している登録データ（RosterState の JSON）から、ゲームのキーを大文字小文字を区別せずに探す。 */
function findDataSetKey(roster, key) {
  const lower = key.toLowerCase();
  return Object.keys(roster.DataSets).find((k) => k.toLowerCase() === lower) ?? key;
}

/** ゲーム 1 つ分（DataSetRoster）を、項目が欠けていても扱える形にする（知らない項目は残す）。 */
function normalizeDataSet(d) {
  const r = isPlainObject(d) ? d : {};
  if (!Array.isArray(r.Pokemon)) r.Pokemon = [];
  if (!Array.isArray(r.Teams)) r.Teams = [];
  if (!Array.isArray(r.Rivals)) r.Rivals = [];
  if (!Array.isArray(r.History)) r.History = [];
  if (!isId(r.NextRivalId)) r.NextRivalId = 1;
  if (!isId(r.NextMatchId)) r.NextMatchId = 1;
  if (!isId(r.NextId)) r.NextId = 1;
  if (r.ActiveTeam === undefined) r.ActiveTeam = null;
  return r;
}

/**
 * Durable Object のメソッドは、Response ではなく {status, body} を返す（Response は本文のストリームを RPC で渡すことになり、
 * 直後のリクエストと重なると「ReadableStream received over RPC disconnected prematurely」で失敗することがあるため）。
 * Worker 側が {@link respond} で Response にする。
 */
function plain(status, body) {
  return { status, body };
}

function plainError(status, code, message) {
  return plain(status, { error: code, message });
}

function plainTooLarge() {
  return plainError(413, "too_large", `データが大きすぎます（上限 ${MAX_BYTES / 1024} KB）`);
}

/** {@link catchInvalid} の、Durable Object 内で使う版（400 を {status, body} で返す）。 */
async function catchInvalidPlain(action) {
  try {
    return await action();
  } catch (e) {
    if (e instanceof InvalidError) return plainError(400, e.code, e.message);
    throw e;
  }
}

/** Durable Object の返した {status, body} を Response にする。 */
async function respond(call) {
  const result = await call;
  return json(result.status, result.body);
}

/**
 * アクセスキー 1 つ分の置き場（SQLite ストレージの Durable Object）。
 * 名前ごとにインスタンスは 1 つだけで、リクエストは 1 つずつ処理されるので、revision の比較と保存の間に別の書き込みは入らない
 * （PUT の検証・連鎖する変更・保存は 1 回の呼び出しの中で行い、保存は 1 回の put で書く）。
 *
 * ストレージ: "revision"（数値）・"roster"（RosterState の JSON 文字列）・"deleted"（キーの作り直しで消した印）・
 * "touched"（最後のアクセスの時刻。1 年で自動削除するため）。登録データは今までどおり 1 つの文書として保存し、
 * API はその一部（ポケモン / チーム / 相手チーム）を読み書きする。
 * 試合の履歴は文書には入れず、SQLite の表 history（ゲームのキー・Id・試合の JSON）に 1 試合 1 行で持つ
 * （履歴が増えても、登録データの上限 MAX_BYTES に当たらないように）。history_seq はゲームごとの次の Id。
 * 古い版が文書の中に持っていた履歴は、最初のアクセスで表へ移す。
 */
export class RosterSlot extends DurableObject {
  /** 文書の中の履歴を表へ移し終えたか。 */
  #migrated = false;

  /** アクセスの無い置き場を消すまでの期間。動作確認用に、環境変数 EXPIRE_MS（1 秒以上）で短くできる（長くはできない）。 */
  get #expireMs() {
    const n = Number(this.env?.EXPIRE_MS);
    return Number.isSafeInteger(n) && n >= 1000 && n < EXPIRE_MS ? n : EXPIRE_MS;
  }

  /** 履歴の上限（ゲームごと）。動作確認用に、環境変数 HISTORY_LIMIT で小さくできる（大きくはできない）。 */
  get #historyLimit() {
    const n = Number(this.env?.HISTORY_LIMIT);
    return Number.isSafeInteger(n) && n >= 1 && n < MAX_HISTORY_PER_DATA_SET ? n : MAX_HISTORY_PER_DATA_SET;
  }

  constructor(ctx, env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => this.#ensureSchema());
  }

  #ensureSchema() {
    this.#write("CREATE TABLE IF NOT EXISTS history (data_set TEXT NOT NULL, id INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY (data_set, id))");
    this.#write("CREATE TABLE IF NOT EXISTS history_seq (data_set TEXT PRIMARY KEY, next_id INTEGER NOT NULL)");
  }

  async #read() {
    const values = await this.ctx.storage.get(["revision", "roster", "deleted"]);
    const text = values.get("roster");
    let roster = text ? JSON.parse(text) : null;
    if (!isPlainObject(roster)) roster = { Version: 1, DataSets: {} };
    if (!isPlainObject(roster.DataSets)) roster.DataSets = {};
    const deleted = values.get("deleted") === true;
    if (!deleted && !this.#migrated) await this.#migrateHistory(roster);
    this.#migrated = true;
    return { revision: values.get("revision") ?? 0, roster, deleted };
  }

  /** 文書の中の履歴（古い版が書いたもの）を表へ移し、文書からは空にする。何度呼んでも同じ結果になる。 */
  async #migrateHistory(roster) {
    let moved = false;
    for (const [key, d] of Object.entries(roster.DataSets)) {
      if (!isPlainObject(d) || !Array.isArray(d.History) || d.History.length === 0) continue;
      const h = key.toLowerCase();
      for (const m of d.History) {
        if (!isPlainObject(m) || !isId(m.Id)) continue;
        this.#write("INSERT OR IGNORE INTO history (data_set, id, json) VALUES (?, ?, ?)", h, m.Id, JSON.stringify(m));
      }
      this.#raiseNextId(h, isId(d.NextMatchId) ? d.NextMatchId : 1);
      d.History = [];
      moved = true;
    }
    if (moved) await this.ctx.storage.put("roster", JSON.stringify(roster));
  }

  #gone() {
    return plainError(410, "key_retired", "このアクセスキーは作り直されたため使えません。新しいアクセスキーを使ってください");
  }

  // ---- 試合の履歴（SQLite の表）

  /** 書き込みの SQL を実行し、書いた行数を返す（カーソルは最後まで読んでから行数を見る）。 */
  #write(query, ...bindings) {
    const cursor = this.ctx.storage.sql.exec(query, ...bindings);
    cursor.toArray();
    return cursor.rowsWritten;
  }

  #count(h) {
    return this.ctx.storage.sql.exec("SELECT COUNT(*) AS c FROM history WHERE data_set = ?", h).one().c;
  }

  /** 次に振る Id（今ある最大の Id の次より小さくならない）。 */
  #nextId(h) {
    const sql = this.ctx.storage.sql;
    const seq = sql.exec("SELECT next_id FROM history_seq WHERE data_set = ?", h).toArray()[0]?.next_id ?? 1;
    const max = sql.exec("SELECT MAX(id) AS m FROM history WHERE data_set = ?", h).one().m ?? 0;
    return Math.max(seq, max + 1);
  }

  /** 次に振る Id を、少なくとも next 以上にする（Id は使い回さない）。 */
  #raiseNextId(h, next) {
    const value = Math.min(Math.max(this.#nextId(h), next), MAX_ID + 1);
    this.#write(
      "INSERT INTO history_seq (data_set, next_id) VALUES (?, ?) ON CONFLICT(data_set) DO UPDATE SET next_id = excluded.next_id", h, value);
  }

  /** 相手チーム API の応答に入れる履歴（古い順）。limit が undefined なら全件、0 なら空、n なら直近 n 件。 */
  #historyFor(key, limit) {
    if (limit === 0) return [];
    const h = key.toLowerCase();
    const sql = this.ctx.storage.sql;
    const rows = limit === undefined
      ? sql.exec("SELECT json FROM history WHERE data_set = ? ORDER BY id", h)
      : sql.exec("SELECT json FROM (SELECT id, json FROM history WHERE data_set = ? ORDER BY id DESC LIMIT ?) ORDER BY id", h, limit);
    return rows.toArray().map((r) => JSON.parse(r.json));
  }

  /** GET・409 の本文（リソースの現在の内容）。 */
  #view(resource, revision, roster, historyLimit) {
    const dataSets = {};
    for (const [k, v] of Object.entries(roster.DataSets)) {
      const d = normalizeDataSet(v);
      dataSets[k] = resource === "pokemon"
        ? { nextId: d.NextId, pokemon: d.Pokemon }
        : resource === "rival"
          ? { nextId: d.NextRivalId, rivals: d.Rivals, nextMatchId: this.#nextId(k.toLowerCase()), history: this.#historyFor(k, historyLimit) }
          : { teams: d.Teams, activeTeam: d.ActiveTeam };
    }
    return { revision, dataSets };
  }

  async #save(revision, roster) {
    const text = JSON.stringify(roster);
    if (new TextEncoder().encode(text).byteLength > MAX_BYTES) return plainTooLarge();
    const next = revision + 1;
    await this.ctx.storage.put({ revision: next, roster: text });
    await this.#touch();
    return plain(200, { revision: next });
  }

  // ---- 1 年アクセスが無い置き場の自動削除

  /** 最後のアクセスの時刻を更新し、削除のアラームを延ばす（書き込みは 1 日に 1 回まで）。 */
  async #touch() {
    const now = Date.now();
    const last = await this.ctx.storage.get("touched");
    // 更新は期間の 1/4 か 1 日ごと（短い期間で動作確認するときも、期間内のアクセスは延長される）
    if (typeof last === "number" && now - last < Math.min(TOUCH_INTERVAL_MS, this.#expireMs / 4)) return;
    await this.ctx.storage.put("touched", now);
    await this.ctx.storage.setAlarm(now + this.#expireMs);
  }

  async alarm() {
    const values = await this.ctx.storage.get(["touched", "deleted"]);
    // 作り直し・共有解除で消したキーは、「消した印」を残す（印まで消すと、古いキーが空の置き場として使えてしまう）
    if (values.get("deleted") === true) return;
    const last = values.get("touched");
    if (typeof last === "number" && Date.now() - last < this.#expireMs) {
      await this.ctx.storage.setAlarm(last + this.#expireMs);
      return;
    }
    await this.ctx.storage.deleteAll();
    this.#ensureSchema();
    this.#migrated = false;
  }

  // ---- 登録データ（ポケモン・チーム・相手チーム）

  async getResource(resource, historyLimit) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    if (s.revision > 0) await this.#touch();
    return plain(200, this.#view(resource, s.revision, s.roster, historyLimit));
  }

  /** ポケモンを置き換え、消えたポケモンをチームから外す（空のチームは消し、使用中が消えたら解除）。 */
  async putPokemons(baseRevision, values) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    if (baseRevision !== s.revision) return plain(409, this.#view("pokemon", s.revision, s.roster));
    for (const [requested, { pokemon, nextId }] of Object.entries(values)) {
      const key = findDataSetKey(s.roster, requested);
      const d = normalizeDataSet(s.roster.DataSets[key]);
      s.roster.DataSets[key] = d;
      d.Pokemon = pokemon;
      // 登録 ID は使い回さない（消した ID の次から振る）
      const maxId = pokemon.reduce((m, p) => Math.max(m, p.Id), 0);
      d.NextId = Math.max(d.NextId, nextId ?? 1, maxId + 1);
      const ids = new Set(pokemon.map((p) => p.Id));
      const teams = [];
      for (const t of d.Teams) {
        if (!isPlainObject(t) || !Array.isArray(t.Slots)) continue;
        t.Slots = t.Slots.filter((slot) => isPlainObject(slot) && ids.has(slot.EntryId));
        if (t.Slots.length > 0) teams.push(t);
      }
      d.Teams = teams;
      if (d.ActiveTeam !== null && !teams.some((t) => t.Number === d.ActiveTeam)) d.ActiveTeam = null;
    }
    return this.#save(s.revision, s.roster);
  }

  /**
   * 対戦中の相手チームを置き換える（ID は使い回さない）。
   * 履歴（history）は置き換えない。古い版のクライアントが、自分が足した試合を書き戻してくるので、
   * 「次の Id 以降の、まだ無い試合」だけを足す（古い一覧に残っている、すでに消した試合を復活させない）。
   */
  async putRivals(baseRevision, values, historyLimit) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    if (baseRevision !== s.revision) return plain(409, this.#view("rival", s.revision, s.roster, historyLimit));
    for (const [requested, { rivals, nextId, history, nextMatchId }] of Object.entries(values)) {
      const key = findDataSetKey(s.roster, requested);
      const d = normalizeDataSet(s.roster.DataSets[key]);
      s.roster.DataSets[key] = d;
      d.Rivals = rivals;
      d.NextRivalId = Math.max(d.NextRivalId, nextId ?? 1, rivals.reduce((m, r) => Math.max(m, r.Id), 0) + 1);
      const h = key.toLowerCase();
      if (history) this.#addUnknownMatches(h, history);
      if (nextMatchId) this.#raiseNextId(h, nextMatchId);
    }
    return this.#save(s.revision, s.roster);
  }

  #addUnknownMatches(h, history) {
    let next = this.#nextId(h);
    let count = this.#count(h);
    for (const m of [...history].sort((a, b) => a.Id - b.Id)) {
      if (m.Id < next || count >= this.#historyLimit) continue;
      this.#write("INSERT OR IGNORE INTO history (data_set, id, json) VALUES (?, ?, ?)", h, m.Id, JSON.stringify(m));
      next = m.Id + 1;
      count++;
    }
    this.#raiseNextId(h, next);
  }

  /** チームと使用中チームを置き換える（登録に無いポケモンを含むなどは 400 で、何も保存しない）。 */
  async putTeams(baseRevision, dataSets) {
    return catchInvalidPlain(async () => {
      const s = await this.#read();
      if (s.deleted) return this.#gone();
      if (baseRevision !== s.revision) return plain(409, this.#view("teams", s.revision, s.roster));
      const updates = [];
      for (const [requested, value] of Object.entries(dataSets)) {
        const key = findDataSetKey(s.roster, requested);
        const d = normalizeDataSet(s.roster.DataSets[key]);
        const ids = new Set(d.Pokemon.filter(isPlainObject).map((p) => p.Id));
        updates.push([key, d, validateTeams(requested, value, ids)]);
      }
      for (const [key, d, { teams, activeTeam }] of updates) {
        d.Teams = [...teams].sort((a, b) => a.Number - b.Number);
        d.ActiveTeam = activeTeam;
        s.roster.DataSets[key] = d;
      }
      return this.#save(s.revision, s.roster);
    });
  }

  // ---- 試合の履歴 API（revision は増えない）

  /** 試合の一覧（新しい順）。before を付けると、その Id より前の試合から。 */
  async listHistory(dataSet, limit, before) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    const h = dataSet.toLowerCase();
    const total = this.#count(h);
    const sql = this.ctx.storage.sql;
    const rows = (before === undefined
      ? sql.exec("SELECT json FROM history WHERE data_set = ? ORDER BY id DESC LIMIT ?", h, limit + 1)
      : sql.exec("SELECT json FROM history WHERE data_set = ? AND id < ? ORDER BY id DESC LIMIT ?", h, before, limit + 1)).toArray();
    if (total > 0) await this.#touch();
    return plain(200, { total, items: rows.slice(0, limit).map((r) => JSON.parse(r.json)), hasMore: rows.length > limit });
  }

  async getMatch(dataSet, id) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    const h = dataSet.toLowerCase();
    const row = this.ctx.storage.sql.exec("SELECT json FROM history WHERE data_set = ? AND id = ?", h, id).toArray()[0];
    if (!row) return plainError(404, "not_found", `履歴 #${id} はありません`);
    return plain(200, { total: this.#count(h), item: JSON.parse(row.json) });
  }

  /** 試合を 1 つ足す。Id はここで割り当てる。上限（ゲームごとに MAX_HISTORY_PER_DATA_SET 件）を超えるときは 409。 */
  async addMatch(dataSet, match) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    const h = dataSet.toLowerCase();
    const count = this.#count(h);
    if (count >= this.#historyLimit)
      return plainError(409, "history_limit", `試合の履歴が上限（${this.#historyLimit} 件）に達しています。古い履歴を削除してください`);
    const id = this.#nextId(h);
    if (id > MAX_ID) return plainError(409, "history_limit", "これ以上、試合の履歴を足せません");
    this.#write("INSERT INTO history (data_set, id, json) VALUES (?, ?, ?)", h, id, JSON.stringify({ ...match, Id: id }));
    this.#raiseNextId(h, id + 1);
    await this.#touch();
    return plain(201, { id, total: count + 1 });
  }

  async removeMatch(dataSet, id) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    const removed = this.#write("DELETE FROM history WHERE data_set = ? AND id = ?", dataSet.toLowerCase(), id);
    return removed > 0 ? plain(204) : plainError(404, "not_found", `履歴 #${id} はありません`);
  }

  /** 新しい keepLatest 件を残して、古い試合を消す。 */
  async trimHistory(dataSet, keepLatest) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    const h = dataSet.toLowerCase();
    const deleted = this.#write(
      "DELETE FROM history WHERE data_set = ? AND id NOT IN (SELECT id FROM history WHERE data_set = ? ORDER BY id DESC LIMIT ?)", h, h, keepLatest);
    return plain(200, { deleted, total: this.#count(h) });
  }

  /** アクセスキーを使えなくし、置き場のデータ（履歴も）を消す。 */
  async retire() {
    // 先に「消した印」を書く（deleteAll してから印を書くと、その間に失敗したとき、データだけ消えて印が残らず、
    // 古いキーが空の置き場として使えてしまう）。印以外のデータを消す
    await this.ctx.storage.put("deleted", true);
    this.#write("DELETE FROM history");
    this.#write("DELETE FROM history_seq");
    const keys = [...(await this.ctx.storage.list()).keys()].filter((k) => k !== "deleted");
    for (let i = 0; i < keys.length; i += 128) await this.ctx.storage.delete(keys.slice(i, i + 128));
    await this.ctx.storage.deleteAlarm();
    return plain(204);
  }
}
