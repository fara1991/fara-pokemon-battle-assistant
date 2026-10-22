// 入口ページの静的 HTML の生成（tools/build-entry-pages.mjs）の確認。
//   node --test tools/build-entry-pages.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, writeFileSync, existsSync, readdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
    SITES, buildBody, buildHead, pageFile, pageUrl, renderPage, renderRobots, renderSitemap, run, validatePages,
    HEAD_START, HEAD_END, BODY_START, BODY_END,
} from "./build-entry-pages.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const pages = JSON.parse(readFileSync(join(here, "entry-pages.json"), "utf8"));
const origin = SITES.production;

/** dotnet publish の index.html に近いテンプレート（区切りは src の wwwroot/index.html と同じ） */
const TEMPLATE = `<!DOCTYPE html>
<html lang="ja">
<head>
    <meta charset="utf-8" />
    ${HEAD_START}
    <title>ホーム | Pokémon Battle Assistant</title>
    <meta name="description" content="x" />
    ${HEAD_END}
    <base href="/" />
    <link href="css/app.css" rel="stylesheet" />
</head>
<body>
    <div id="app">
        ${BODY_START}
        <div class="loading-screen"><p>読み込み中...</p></div>
        ${BODY_END}
    </div>
    <script src="_framework/blazor.webassembly.js"></script>
</body>
</html>
`;

const battleTerms = pages.find((p) => p.route === "battle-terms");
const home = pages.find((p) => p.route === "");

test("ホームは index.html、そのほかは <route>.html（拡張子なしの URL で配られる）", () => {
    assert.equal(pageFile(""), "index.html");
    assert.equal(pageFile("battle-terms"), "battle-terms.html");
    assert.equal(pageUrl(origin, ""), "https://pokemon.fara-labs.com/");
    assert.equal(pageUrl(origin, "battle-terms"), "https://pokemon.fara-labs.com/battle-terms");
});

test("title と description、canonical・OGP の値が入る。ホームには canonical と og:url を付けない", () => {
    const head = buildHead(battleTerms, origin);
    assert.match(head, /<title>用語・略語 \| Pokémon Battle Assistant<\/title>/);
    assert.match(head, /<meta name="description" content="ポケモン対戦で使う略語/);
    assert.match(head, /<link rel="canonical" href="https:\/\/pokemon\.fara-labs\.com\/battle-terms" \/>/);
    assert.match(head, /<meta property="og:url" content="https:\/\/pokemon\.fara-labs\.com\/battle-terms" \/>/);
    assert.match(head, /<meta property="og:locale" content="ja_JP" \/>/);
    assert.match(head, /<meta name="twitter:card" content="summary" \/>/);

    const homeHead = buildHead(home, origin);
    assert.doesNotMatch(homeHead, /canonical/);
    assert.doesNotMatch(homeHead, /og:url/);
});

test("属性と本文の文字は HTML としてエスケープする", () => {
    const tricky = { ...battleTerms, title: 'a"<b>&', description: "x<script>&\"", heading: "<h>", paragraphs: ["<i>&"] };
    const head = buildHead(tricky, origin);
    assert.doesNotMatch(head, /<script>/);
    assert.match(head, /a&quot;&lt;b&gt;&amp;/);
    const body = buildBody(tricky, pages);
    assert.match(body, /<h1>&lt;h&gt;<\/h1>/);
    assert.match(body, /<p>&lt;i&gt;&amp;<\/p>/);
});

test("本文に要約の段落と、主なページへのリンクが入る（JavaScript が無くても読める）", () => {
    const body = buildBody(battleTerms, pages);
    for (const text of battleTerms.paragraphs) assert.ok(body.includes(text.replace(/&/g, "&amp;")));
    assert.match(body, /<h1>用語・略語<\/h1>/);
    for (const p of pages) assert.ok(body.includes(`href="${p.route === "" ? "/" : `/${p.route}`}"`), p.route);
    assert.match(body, /<noscript>/);
});

test("テンプレートの区切りの間だけを書き換え、区切りと #app・Blazor の読み込みは残す", () => {
    const html = renderPage(TEMPLATE, battleTerms, pages, origin);
    assert.match(html, /<title>用語・略語/);
    assert.match(html, /<main class="entry-static">/);
    assert.doesNotMatch(html, /読み込み中/);
    assert.ok(html.includes(BODY_START) && html.includes(BODY_END));
    assert.ok(html.includes(HEAD_START) && html.includes(HEAD_END));
    assert.match(html, /_framework\/blazor\.webassembly\.js/);
    assert.match(html, /<base href="\/" \/>/);
});

test("同じテンプレートから何度作っても同じ結果（区切りが残るので、作り直しても崩れない）", () => {
    const once = renderPage(TEMPLATE, battleTerms, pages, origin);
    const twice = renderPage(once, battleTerms, pages, origin);
    assert.equal(twice, once);
});

test("区切りが無いテンプレートは、作らずに失敗する", () => {
    assert.throws(() => renderPage("<html></html>", home, pages, origin), /区切り/);
    assert.throws(() => renderPage(TEMPLATE.replace(BODY_END, BODY_END + BODY_END), home, pages, origin), /区切り/);
});

test("robots.txt: 本番は Sitemap の行と個人のページの Disallow。確認用は Sitemap も Disallow も付けない", () => {
    const prod = renderRobots("production", origin);
    assert.match(prod, /^User-agent: \*$/m);
    assert.match(prod, /^Disallow: \/my-$/m);
    assert.match(prod, /^Disallow: \/api\/my\/$/m);
    assert.match(prod, /^Sitemap: https:\/\/pokemon\.fara-labs\.com\/sitemap\.xml$/m);
    // /api/game（ゲームデータ）は、ページの描画に使うので止めない
    assert.doesNotMatch(prod, /api\/game/);

    const dev = renderRobots("develop", SITES.develop);
    assert.doesNotMatch(dev, /Sitemap:/);
    assert.doesNotMatch(dev, /Disallow/);
});

test("sitemap.xml は入口ページの正規の URL だけを並べる", () => {
    const xml = renderSitemap(pages, origin);
    assert.match(xml, /<urlset xmlns="http:\/\/www\.sitemaps\.org\/schemas\/sitemap\/0\.9">/);
    assert.equal((xml.match(/<url>/g) || []).length, pages.length);
    assert.match(xml, /<loc>https:\/\/pokemon\.fara-labs\.com\/<\/loc>/);
    assert.match(xml, /<loc>https:\/\/pokemon\.fara-labs\.com\/battle-terms<\/loc>/);
    assert.doesNotMatch(xml, /\.html|my-|data-share|glossary/);
});

test("入口ページの一覧の確認: 重複・不正なルート・空の項目は失敗する", () => {
    assert.doesNotThrow(() => validatePages(pages));
    assert.throws(() => validatePages([...pages, battleTerms]), /重複/);
    assert.throws(() => validatePages([{ ...battleTerms, route: "battle/terms" }]), /ルート/);
    assert.throws(() => validatePages([{ ...battleTerms, route: "Battle" }]), /ルート/);
    assert.throws(() => validatePages([{ ...battleTerms, title: " " }]), /title/);
    assert.throws(() => validatePages([{ ...battleTerms, paragraphs: [] }]), /paragraphs/);
    assert.throws(() => validatePages([{ ...battleTerms, description: "あ".repeat(151) }]), /長すぎ/);
});

test("run: 本番は各ページ・robots.txt・sitemap.xml を書き、確認用は sitemap.xml を書かない", () => {
    const dir = mkdtempSync(join(tmpdir(), "entry-pages-"));
    writeFileSync(join(dir, "index.html"), TEMPLATE);
    const written = run({ dir, env: "production" });
    assert.deepEqual(written, pages.map((p) => pageFile(p.route)));
    assert.ok(existsSync(join(dir, "battle-terms.html")));
    assert.ok(existsSync(join(dir, "sitemap.xml")));
    assert.ok(existsSync(join(dir, "robots.txt")));
    assert.ok(!existsSync(join(dir, "glossary.html")), "旧 URL のファイルは作らない（転送を妨げない）");
    assert.match(readFileSync(join(dir, "index.html"), "utf8"), /<title>ホーム/);

    const devDir = mkdtempSync(join(tmpdir(), "entry-pages-dev-"));
    writeFileSync(join(devDir, "index.html"), TEMPLATE);
    run({ dir: devDir, env: "develop" });
    assert.ok(existsSync(join(devDir, "battle-terms.html")));
    assert.ok(!existsSync(join(devDir, "sitemap.xml")));
    const devHtml = readFileSync(join(devDir, "battle-terms.html"), "utf8");
    assert.match(devHtml, /https:\/\/pokemon-dev\.fara-labs\.com\/battle-terms/);
    assert.ok(readdirSync(devDir).length >= pages.length + 1);
});

test("run: 不正な --env と、index.html が無いフォルダは失敗する", () => {
    const dir = mkdtempSync(join(tmpdir(), "entry-pages-none-"));
    assert.throws(() => run({ dir, env: "staging" }), /--env/);
    assert.throws(() => run({ dir, env: "production" }), /index\.html/);
});
