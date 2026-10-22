// worker/maintenance.js のテスト。  node --test worker/maintenance.test.mjs
// 4 つのリポジトリ（pokemon・avatar・bot・portfolio）で共通のファイルなので、このテストも同じ内容にしている。
import { test } from "node:test";
import assert from "node:assert/strict";
import { getMaintenance, maintenanceResponse, maintenancePageHtml, noticeHtml, withNotice, escapeHtml } from "./maintenance.js";

const req = (path, accept = "text/html", method = "GET") => new Request(`https://site.example${path}`, { method, headers: { Accept: accept } });

test("getMaintenance: true / on / 1 はメンテナンス、notice はお知らせ、それ以外は通常", () => {
  for (const v of ["true", "TRUE", " True ", "on", "1"]) assert.equal(getMaintenance({ MAINTENANCE: v }).state, "on", v);
  for (const v of ["notice", "NOTICE", " Notice "]) assert.equal(getMaintenance({ MAINTENANCE: v }).state, "notice", v);
  for (const v of [undefined, null, "", "false", "False", "0", "off", "yes", "maintenance"]) assert.equal(getMaintenance({ MAINTENANCE: v }).state, "off", String(v));
  assert.equal(getMaintenance(undefined).state, "off");
  assert.equal(getMaintenance({}).state, "off");
});

test("getMaintenance: 文は notice と true のときだけ。通常に戻せば、お知らせも消える", () => {
  const message = "10月25日（土）20:00ごろ、サイトを更新します";
  assert.equal(getMaintenance({ MAINTENANCE: "notice", MAINTENANCE_MESSAGE: message }).message, message);
  assert.equal(getMaintenance({ MAINTENANCE: "true", MAINTENANCE_MESSAGE: message }).message, message);
  assert.equal(getMaintenance({ MAINTENANCE: "false", MAINTENANCE_MESSAGE: message }).message, "");
  assert.equal(getMaintenance({ MAINTENANCE_MESSAGE: message }).message, "");
});

test("getMaintenance: 文は 200 文字まで・制御文字なし・文字列以外は空", () => {
  assert.equal(Array.from(getMaintenance({ MAINTENANCE: "notice", MAINTENANCE_MESSAGE: "あ".repeat(500) }).message).length, 200);
  assert.equal(getMaintenance({ MAINTENANCE: "notice", MAINTENANCE_MESSAGE: "😀".repeat(300) }).message, "😀".repeat(200));
  assert.equal(getMaintenance({ MAINTENANCE: "notice", MAINTENANCE_MESSAGE: "a\u0000b\u0007c" }).message, "abc");
  assert.equal(getMaintenance({ MAINTENANCE: "notice", MAINTENANCE_MESSAGE: 5 }).message, "");
  assert.equal(getMaintenance({ MAINTENANCE: "notice" }).message, "");
});

test("escapeHtml は HTML の特殊文字を変える", () => {
  assert.equal(escapeHtml(`<script>alert("x") & 'y'</script>`), "&lt;script&gt;alert(&quot;x&quot;) &amp; &#39;y&#39;&lt;/script&gt;");
});

test("メンテナンス画面: 文を出す（HTML は無害化）、Twitter のボタン、noindex、サイト名", () => {
  const html = maintenancePageHtml("テストサイト", "<b>20:00</b>ごろ");
  assert.match(html, /ただいまメンテナンス中です/);
  assert.match(html, /&lt;b&gt;20:00&lt;\/b&gt;ごろ/);
  assert.ok(!html.includes("<b>20:00</b>"));
  assert.match(html, /<a href="https:\/\/twitter\.com\/game_fara"/);
  assert.match(html, /name="robots" content="noindex"/);
  assert.match(html, /<title>メンテナンス中 \| テストサイト<\/title>/);
  assert.ok(!/<script/i.test(html) && !/rel="stylesheet"/.test(html));
});

test("メンテナンス画面: 文が無いときは予定の行を出さない", () => {
  assert.ok(!maintenancePageHtml("X", "").includes('class="s"'));
  assert.ok(maintenancePageHtml("X", "予定です").includes('class="s"'));
});

test("maintenanceResponse: ページを開くと 503 のメンテナンス画面（キャッシュ・検索エンジンに載せない）", async () => {
  const res = maintenanceResponse(req("/learn"), { state: "on", message: "20:00ごろ" }, "サイト");
  assert.equal(res.status, 503);
  assert.equal(res.headers.get("Content-Type"), "text/html; charset=utf-8");
  assert.equal(res.headers.get("Cache-Control"), "no-store");
  assert.equal(res.headers.get("Retry-After"), "3600");
  assert.equal(res.headers.get("X-Robots-Tag"), "noindex");
  assert.match(res.headers.get("Content-Security-Policy"), /style-src 'unsafe-inline'/);
  assert.match(await res.text(), /20:00ごろ/);
});

test("maintenanceResponse: /api/ と JSON を求める要求には JSON", async () => {
  for (const r of [req("/api/my/team", "*/*"), req("/api/report", "application/json"), req("/x", "application/json")]) {
    const res = maintenanceResponse(r, { state: "on", message: "" }, "サイト");
    assert.equal(res.status, 503);
    assert.match(res.headers.get("Content-Type"), /application\/json/);
    assert.equal((await res.json()).error, "maintenance");
  }
});

test("maintenanceResponse: 画像・スクリプトなどには短い文字", async () => {
  const res = maintenanceResponse(req("/css/site.css", "text/css,*/*;q=0.1"), { state: "on", message: "" }, "サイト");
  assert.equal(res.status, 503);
  assert.match(res.headers.get("Content-Type"), /text\/plain/);
});

test("maintenanceResponse: HEAD は本文なし", async () => {
  const res = maintenanceResponse(req("/", "text/html", "HEAD"), { state: "on", message: "" }, "サイト");
  assert.equal(res.status, 503);
  assert.equal(await res.text(), "");
});

test("noticeHtml: 文が無いときの既定の文、HTML は無害化", () => {
  assert.match(noticeHtml(""), /まもなくメンテナンスを行います/);
  assert.match(noticeHtml("<i>x</i>"), /&lt;i&gt;x&lt;\/i&gt;/);
  assert.match(noticeHtml("x"), /class="maintenance-notice" role="status"/);
  assert.ok(!/style=/.test(noticeHtml("x")), "CSP のため、インラインの style は使わない");
});

test("noticeHtml: 1 行目に見出し「【お知らせ】」、本文は次の要素（2 行目以降に出す）", () => {
  const html = noticeHtml("2026年10月25日（日）20:00〜21:00頃、サイトの更新を行います。");
  assert.match(html, /<strong class="maintenance-notice-title">【お知らせ】<\/strong>/);
  assert.match(html, /<span class="maintenance-notice-text">2026年10月25日（日）20:00〜21:00頃、サイトの更新を行います。<\/span>/);
  assert.ok(html.indexOf("【お知らせ】") < html.indexOf("2026年10月25日"), "見出しが先");
});

test("noticeHtml: 本文は見出しとは別の要素で、本文の HTML も無害化", () => {
  const html = noticeHtml("<b>x</b>");
  assert.match(html, /<span class="maintenance-notice-text">&lt;b&gt;x&lt;\/b&gt;<\/span>/);
  assert.equal((html.match(/【お知らせ】/g) ?? []).length, 1);
});

// HTMLRewriter は Workers にだけあるので、<main> の直後に足すだけの代わりを渡す
const rewrite = (response, banner) => new Response(banner, { headers: response.headers });
const html = (body = "<main>x</main>", type = "text/html; charset=utf-8") => new Response(body, { headers: { "Content-Type": type } });

test("withNotice: notice のときだけ、HTML にバナーを足す", async () => {
  const noticed = withNotice(html(), { state: "notice", message: "20:00ごろ" }, rewrite);
  assert.match(await noticed.text(), /maintenance-notice/);
  for (const state of ["off", "on"]) {
    const untouched = withNotice(html(), { state, message: "x" }, rewrite);
    assert.equal(await untouched.text(), "<main>x</main>", state);
  }
});

test("withNotice: HTML でない応答には足さない。足した HTML はキャッシュさせない", async () => {
  const css = withNotice(html("body{}", "text/css"), { state: "notice", message: "x" }, rewrite);
  assert.equal(await css.text(), "body{}");
  const page = withNotice(html(), { state: "notice", message: "x" }, rewrite);
  assert.equal(page.headers.get("Cache-Control"), "no-store");
});
