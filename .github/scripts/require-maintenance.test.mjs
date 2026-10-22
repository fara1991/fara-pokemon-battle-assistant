// require-maintenance.mjs のテスト。  node --test .github/scripts/require-maintenance.test.mjs
// 4 つのリポジトリで同じファイルなので、このテストも同じ内容にしている。
import { test } from "node:test";
import assert from "node:assert/strict";
import { evaluate, readMaintenanceValue, fetchSettings, run } from "./require-maintenance.mjs";

const bindings = (text, type = "plain_text") => [{ name: "OTHER", type: "plain_text", text: "x" }, { name: "MAINTENANCE", type, text }];
const settingsResponse = (b, status = 200) => new Response(JSON.stringify({ success: status === 200, result: { bindings: b } }), { status });
const ENV = { CLOUDFLARE_API_TOKEN: "tok", CLOUDFLARE_ACCOUNT_ID: "acc" };

test("evaluate: notice と true は通す。false・未設定・そのほかは止める", () => {
  for (const v of ["notice", "NOTICE", " Notice "]) assert.equal(evaluate(v).ok, true, v);
  for (const v of ["true", "TRUE", "on", "1"]) assert.equal(evaluate(v).ok, true, v);
  for (const v of ["false", "False", "", undefined, null, "0", "off", "maintenance", "yes"]) assert.equal(evaluate(v).ok, false, String(v));
});

test("evaluate: 止めるときのメッセージは、notice にする手順を案内する。シークレットは別のメッセージ", () => {
  assert.match(evaluate("false").message, /notice にして/);
  assert.match(evaluate("").message, /未設定/);
  assert.match(evaluate("(secret)").message, /テキスト/);
  assert.equal(evaluate("(secret)").state, "secret");
});

test("evaluate: 値に改行などが入っていても、メッセージは 1 行（::error:: の偽装を防ぐ）。長い値は切る", () => {
  const message = evaluate("x\n::error::偽物\r\nfalse").message;
  assert.ok(!/[\r\n]/.test(message));
  assert.ok(evaluate("a".repeat(500)).message.length < 300);
});

test("readMaintenanceValue: テキストの変数の値を読む。シークレットと、無いときと、想定外の形", () => {
  assert.equal(readMaintenanceValue(bindings(" notice ")), "notice");
  assert.equal(readMaintenanceValue(bindings("x", "secret_text")), "(secret)");
  assert.equal(readMaintenanceValue([{ name: "OTHER", type: "plain_text", text: "x" }]), "");
  assert.equal(readMaintenanceValue(undefined), "");
  assert.equal(readMaintenanceValue(null), "");
  assert.equal(readMaintenanceValue([null, { name: "MAINTENANCE", type: "plain_text", text: 5 }]), "");
});

test("fetchSettings: 正しい URL と認証で読み、404 は「無い」", async () => {
  let seen;
  const ok = await fetchSettings("fara-x", { token: "tok", accountId: "acc", fetchImpl: async (url, init) => { seen = { url, init }; return settingsResponse(bindings("notice")); } });
  assert.equal(seen.url, "https://api.cloudflare.com/client/v4/accounts/acc/workers/scripts/fara-x/settings");
  assert.equal(seen.init.headers.Authorization, "Bearer tok");
  assert.equal(ok.missing, false);
  const missing = await fetchSettings("fara-x", { token: "t", accountId: "a", fetchImpl: async () => new Response("", { status: 404 }) });
  assert.equal(missing.missing, true);
  await assert.rejects(() => fetchSettings("fara-x", { token: "t", accountId: "a", fetchImpl: async () => new Response("", { status: 403 }) }), /403/);
  await assert.rejects(() => fetchSettings("fara-x", { token: "t", accountId: "a", fetchImpl: async () => new Response(JSON.stringify({ success: false }), { status: 200 }) }), /エラー/);
});

const runWith = async (fetchImpl, env = ENV, workerName = "fara-x") => {
  const lines = [];
  const code = await run({ workerName, env, fetchImpl, log: (m) => lines.push(m), error: (m) => lines.push(m) });
  return { code, text: lines.join("\n") };
};

test("run: notice のときは通す", async () => {
  const r = await runWith(async () => settingsResponse(bindings("notice")));
  assert.equal(r.code, 0);
  assert.match(r.text, /notice/);
});

test("run: すでに true のときも通す（公開のやり直し）", async () => {
  assert.equal((await runWith(async () => settingsResponse(bindings("true")))).code, 0);
});

test("run: false・未設定・シークレットのときは止める", async () => {
  for (const b of [bindings("false"), [], bindings("notice", "secret_text")]) {
    const r = await runWith(async () => settingsResponse(b));
    assert.equal(r.code, 1);
    assert.match(r.text, /::error::/);
  }
});

test("run: Worker がまだ無い（初回の公開）ときは通す", async () => {
  const r = await runWith(async () => new Response("", { status: 404 }));
  assert.equal(r.code, 0);
  assert.match(r.text, /初回/);
});

test("run: 確かめられなかったとき（通信・権限）は止める", async () => {
  assert.equal((await runWith(async () => new Response("", { status: 403 }))).code, 1);
  assert.equal((await runWith(async () => { throw new Error("network"); })).code, 1);
});

test("run: SKIP_MAINTENANCE_CHECK=true のときは、確かめずに通す（API も呼ばない）", async () => {
  let called = false;
  const r = await runWith(async () => { called = true; return settingsResponse([]); }, { ...ENV, SKIP_MAINTENANCE_CHECK: "true" });
  assert.equal(r.code, 0);
  assert.equal(called, false);
  assert.match(r.text, /飛ばしました/);
  const notSkipped = await runWith(async () => settingsResponse([]), { ...ENV, SKIP_MAINTENANCE_CHECK: "false" });
  assert.equal(notSkipped.code, 1);
});

test("run: 認証の情報・Worker 名が無いときは止める", async () => {
  assert.equal((await runWith(async () => settingsResponse([]), {})).code, 1);
  assert.equal((await runWith(async () => settingsResponse([]), ENV, "")).code, 2);
});

test("run: 応答にトークンを含めない", async () => {
  const r = await runWith(async () => settingsResponse(bindings("false")), { ...ENV, CLOUDFLARE_API_TOKEN: "super-secret-token" });
  assert.ok(!r.text.includes("super-secret-token"));
});
