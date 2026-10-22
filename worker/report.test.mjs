// 作者への報告の受け口（worker/report.js）のテスト。  node --test worker
// 3 サイト共通のファイルなので、このテストも 3 つのリポジトリで同じ内容にしている。
import { test } from "node:test";
import assert from "node:assert/strict";
import { handleReport, clean, cleanPage, isConfigured } from "./report.js";

const ENV = { TURNSTILE_SITE_KEY: "site-key", TURNSTILE_SECRET: "secret-value", DISCORD_WEBHOOK_URL: "https://discord.example/webhook/abc" };
const HOST = "https://site.example";

function post(body, { origin = HOST, type = "application/json", raw = false, ip = "203.0.113.5" } = {}) {
  const headers = { "Content-Type": type, "CF-Connecting-IP": ip };
  if (origin !== null) headers.Origin = origin;
  return new Request(`${HOST}/api/report`, { method: "POST", headers, body: raw ? body : JSON.stringify(body) });
}

const valid = { category: "bug", message: "ここが動きません", contact: "me@example.com", page: "/speed", turnstileToken: "tok", website: "" };

/** Turnstile と Discord への fetch を記録する偽物。 */
function fakeFetch({ turnstile = { ok: true, body: { success: true } }, discord = { ok: true } } = {}) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url: String(url), init });
    if (String(url).includes("turnstile")) {
      if (turnstile.throws) throw new Error("network");
      return new Response(JSON.stringify(turnstile.body), { status: turnstile.ok ? 200 : 500 });
    }
    if (discord.throws) throw new Error("network");
    return discord.ok ? new Response(null, { status: 204 }) : new Response("error", { status: 500 });
  };
  return { fn, calls };
}

const run = (request, env = ENV, f = fakeFetch()) => handleReport(request, env, "テストサイト", { fetch: f.fn, now: () => new Date("2026-10-22T00:00:00Z") });

test("config は、サイトキーと設定済みかを返す（シークレットは返さない）", async () => {
  const res = await run(new Request(`${HOST}/api/report/config`));
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.deepEqual(body, { enabled: true, siteKey: "site-key" });
  assert.ok(!JSON.stringify(body).includes("secret-value"));
  const notConfigured = await (await run(new Request(`${HOST}/api/report/config`), {})).json();
  assert.deepEqual(notConfigured, { enabled: false, siteKey: null });
  assert.equal((await run(new Request(`${HOST}/api/report/config`, { method: "POST" }))).status, 405);
});

test("isConfigured は 3 つの値がそろっているときだけ true", () => {
  assert.equal(isConfigured(ENV), true);
  for (const key of Object.keys(ENV)) assert.equal(isConfigured({ ...ENV, [key]: "" }), false, key);
});

test("正しい報告は、Turnstile を確認して Discord に送る", async () => {
  const f = fakeFetch();
  const res = await run(post(valid), ENV, f);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { ok: true });
  assert.equal(f.calls.length, 2);
  assert.ok(f.calls[0].url.includes("turnstile/v0/siteverify"));
  const verify = new URLSearchParams(f.calls[0].init.body);
  assert.equal(verify.get("secret"), "secret-value");
  assert.equal(verify.get("response"), "tok");
  assert.equal(verify.get("remoteip"), "203.0.113.5");
  assert.equal(f.calls[1].url, ENV.DISCORD_WEBHOOK_URL);
  const sent = JSON.parse(f.calls[1].init.body);
  assert.deepEqual(sent.allowed_mentions, { parse: [] });
  assert.equal(sent.embeds[0].title, "テストサイト への報告");
  assert.equal(sent.embeds[0].description, "ここが動きません");
  assert.deepEqual(sent.embeds[0].fields.map((x) => x.name), ["種類", "連絡先", "ページ"]);
  assert.equal(sent.embeds[0].timestamp, "2026-10-22T00:00:00.000Z");
});

test("連絡先とページは省略できる", async () => {
  const f = fakeFetch();
  const res = await run(post({ ...valid, contact: undefined, page: undefined }), ENV, f);
  assert.equal(res.status, 200);
  assert.deepEqual(JSON.parse(f.calls[1].init.body).embeds[0].fields.map((x) => x.name), ["種類"]);
});

test("別のサイトからの送信（Origin 違い・無し）は 403 で、何も外へ出さない", async () => {
  for (const origin of ["https://evil.example", "null", null]) {
    const f = fakeFetch();
    const res = await run(post(valid, { origin }), ENV, f);
    assert.equal(res.status, 403, String(origin));
    assert.equal(f.calls.length, 0);
  }
});

test("JSON 以外・壊れた JSON・大きすぎる本文を断る", async () => {
  assert.equal((await run(post("x", { type: "text/plain", raw: true }))).status, 415);
  assert.equal((await run(post("{not json", { raw: true }))).status, 400);
  assert.equal((await run(post("[1]", { raw: true }))).status, 400);
  assert.equal((await run(post("null", { raw: true }))).status, 400);
  assert.equal((await run(post("x".repeat(9000), { raw: true }))).status, 413);
});

test("種類・内容・確認の欠けを断る", async () => {
  const cases = [
    [{ ...valid, category: "x" }, "invalid_category"],
    [{ ...valid, category: undefined }, "invalid_category"],
    [{ ...valid, category: "__proto__" }, "invalid_category"],
    [{ ...valid, message: "あ" }, "invalid_message"],
    [{ ...valid, message: "   \n  " }, "invalid_message"],
    [{ ...valid, message: 123 }, "invalid_message"],
    [{ ...valid, turnstileToken: "" }, "missing_token"],
    [{ ...valid, turnstileToken: undefined }, "missing_token"],
  ];
  for (const [body, error] of cases) {
    const f = fakeFetch();
    const res = await run(post(body), ENV, f);
    assert.equal(res.status, 400, error);
    assert.equal((await res.json()).error, error);
    assert.equal(f.calls.length, 0, "確認の前に断るので、外へは出ない");
  }
});

test("罠の欄（website）が入っていたら、何も送らず成功したように返す", async () => {
  const f = fakeFetch();
  const res = await run(post({ ...valid, website: "http://spam.example" }), ENV, f);
  assert.equal(res.status, 200);
  assert.equal(f.calls.length, 0);
});

test("設定が足りないときは 503（何も送らない）", async () => {
  for (const key of Object.keys(ENV)) {
    const f = fakeFetch();
    const res = await run(post(valid), { ...ENV, [key]: undefined }, f);
    assert.equal(res.status, 503, key);
    assert.equal((await res.json()).error, "not_configured");
    assert.equal(f.calls.length, 0);
  }
});

test("Turnstile の確認に失敗したら 400、通信できなければ 502。Discord には送らない", async () => {
  let f = fakeFetch({ turnstile: { ok: true, body: { success: false } } });
  assert.equal((await run(post(valid), ENV, f)).status, 400);
  assert.equal(f.calls.length, 1);
  f = fakeFetch({ turnstile: { ok: false, body: {} } });   // Turnstile 側の障害（5xx）
  assert.equal((await run(post(valid), ENV, f)).status, 502);
  f = fakeFetch({ turnstile: { throws: true } });
  assert.equal((await run(post(valid), ENV, f)).status, 502);
  assert.equal(f.calls.length, 1);
});

test("Discord への送信に失敗したら 502", async () => {
  assert.equal((await run(post(valid), ENV, fakeFetch({ discord: { ok: false } }))).status, 502);
  assert.equal((await run(post(valid), ENV, fakeFetch({ discord: { throws: true } }))).status, 502);
});

test("応答にシークレットと Webhook の URL を含めない", async () => {
  for (const f of [fakeFetch(), fakeFetch({ discord: { ok: false } }), fakeFetch({ turnstile: { ok: true, body: { success: false } } })]) {
    const text = await (await run(post(valid), ENV, f)).text();
    assert.ok(!text.includes("secret-value") && !text.includes("discord.example"), text);
  }
});

test("clean は制御文字を取り、長さを切る。文字列以外は空", () => {
  assert.equal(clean("  a\u0000b\u0007c\nd\te  ", 100), "abc\nd\te");
  assert.equal(clean("あいうえお", 3), "あいう");
  assert.equal(clean(undefined, 10), "");
  assert.equal(clean(5, 10), "");
  assert.equal(clean({}, 10), "");
});

test("メッセージは 2000 文字までに切って送る", async () => {
  const f = fakeFetch();
  await run(post({ ...valid, message: "あ".repeat(5000) }), ENV, f);
  assert.equal(JSON.parse(f.calls[1].init.body).embeds[0].description.length, 2000);
});

test("存在しないパスと、使えないメソッド", async () => {
  assert.equal((await run(new Request(`${HOST}/api/report/x`, { method: "POST" }))).status, 404);
  assert.equal((await run(new Request(`${HOST}/api/report`, { method: "GET" }))).status, 405);
});

test("Turnstile が返した hostname が、このサイトと違えば断る（別のサイトで取ったトークンの使い回し）", async () => {
  const other = fakeFetch({ turnstile: { ok: true, body: { success: true, hostname: "other.example" } } });
  assert.equal((await run(post(valid), ENV, other)).status, 400);
  assert.equal(other.calls.length, 1, "Discord には送らない");
  const same = fakeFetch({ turnstile: { ok: true, body: { success: true, hostname: "site.example" } } });
  assert.equal((await run(post(valid), ENV, same)).status, 200);
  const none = fakeFetch({ turnstile: { ok: true, body: { success: true } } });
  assert.equal((await run(post(valid), ENV, none)).status, 200, "hostname が無いときは確かめない");
});

test("Turnstile 側の障害（5xx）は 502、4xx は 400", async () => {
  assert.equal((await run(post(valid), ENV, fakeFetch({ turnstile: { ok: false, body: {} } }))).status, 502);
});

test("宣言された本文の大きさが上限を超えたら、読む前に 413", async () => {
  let read = false;
  const req = post(valid);
  const big = new Request(req, { headers: new Headers({ "Content-Type": "application/json", Origin: HOST, "Content-Length": String(20 * 1024) }) });
  big.text = async () => { read = true; return "{}"; };
  assert.equal((await run(big)).status, 413);
  assert.equal(read, false);
});

test("外への通信には、時間切れの signal が付く", async () => {
  const f = fakeFetch();
  await run(post(valid), ENV, f);
  assert.ok(f.calls[0].init.signal instanceof AbortSignal);
  assert.ok(f.calls[1].init.signal instanceof AbortSignal);
});

test("絵文字を含む文字列を切っても、文字が割れない", () => {
  const text = "😀".repeat(10);
  const cut = clean(text, 3);
  assert.equal(Array.from(cut).length, 3);
  assert.equal(cut, "😀😀😀");
  assert.doesNotMatch(cut, /[\ud800-\udbff](?![\udc00-\udfff])/);
  assert.equal(clean("a\ud83db", 10), "ab", "対になっていないサロゲートは取る");
});

test("2000 文字の絵文字でも、Discord に送る本文は 2000 文字（割れない）", async () => {
  const f = fakeFetch();
  await run(post({ ...valid, message: "😀".repeat(2500) }), ENV, f);
  const description = JSON.parse(f.calls[1].init.body).embeds[0].description;
  assert.equal(Array.from(description).length, 2000);
});

test("ページ（page）は、サーバー側でも、パスだけを通す", async () => {
  assert.equal(cleanPage("/speed-comparison"), "/speed-comparison");
  assert.equal(cleanPage("/"), "/");
  for (const bad of ["", "speed", "//evil.example", "https://evil.example", "/a b", "/[x](https://evil.example)", "/x?y=1", 5, null, undefined, "/" + "a".repeat(300)]) {
    assert.equal(cleanPage(bad), "", String(bad));
  }
  const f = fakeFetch();
  await run(post({ ...valid, page: "[クリック](https://evil.example)" }), ENV, f);
  assert.deepEqual(JSON.parse(f.calls[1].init.body).embeds[0].fields.map((x) => x.name), ["種類", "連絡先"], "不正なページは載せない");
});

test("Discord の上限（title 256・description 4096・field 値 1024）を超えない", async () => {
  const f = fakeFetch();
  await run(post({ ...valid, message: "あ".repeat(3000), contact: "い".repeat(1000), page: "/" + "a".repeat(299) }), ENV, f);
  const embed = JSON.parse(f.calls[1].init.body).embeds[0];
  assert.ok(embed.title.length <= 256);
  assert.ok(embed.description.length <= 4096);
  for (const field of embed.fields) assert.ok(field.value.length <= 1024 && field.name.length <= 256, field.name);
});
