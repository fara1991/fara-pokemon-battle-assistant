// 検索の入口になるページの、静的な HTML を作る（公開用の dotnet publish の後に実行する）。
//   node tools/build-entry-pages.mjs [--env production|develop] [--dir publish/wwwroot]
//
// 作るもの（--dir の中）:
//   home.html                ホーム（テンプレートの head・#app の中を、ホームの内容に置き換える）。Worker が / のときだけ返す（worker/home.js）
//   index.html               SPA のフォールバック（存在しない URL）。汎用の title と読み込み表示だけ。ホームの本文は入れない（soft 404 にしないため）
//   <route>.html             そのほかの入口ページ（例: battle-terms.html）。URL は /battle-terms（Cloudflare の静的アセットは .html を外して配る）
//   robots.txt               本番は Sitemap の行を付ける。確認用は Sitemap も Disallow も付けない（確認用は noindex のヘッダーで止める）
//   sitemap.xml              本番だけ
//
// テンプレートは dotnet publish が出した index.html。head の SEO-HEAD と、#app の中の SEO-BODY の区切りの間を書き換える。
// 中身は、ブラウザで WebAssembly が起動すると Blazor が #app の中身ごと置き換える（検索エンジン・JavaScript が無い環境には、この本文が見える）。
// 入口ページの一覧と文言は tools/entry-pages.json。ページの title は、Razor の <PageTitle> と同じ文字にしておく（tests で確かめる）。
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const DEFAULT_PAGES_FILE = join(root, "tools", "entry-pages.json");

/** 公開先のオリジン（canonical・sitemap に使う）。確認用は noindex なので、正規の URL も確認用のホストにする。 */
export const SITES = {
    production: "https://pokemon.fara-labs.com",
    develop: "https://pokemon-dev.fara-labs.com",
};
const SITE_NAME = "Pokémon Battle Assistant";
/** 検索に出してほしくない・クローラーが見ても意味のないページ（個人のデータ・API）。本番の robots.txt で止める。 */
const DISALLOW = ["/my-", "/data-share", "/api/my/"];

const HEAD_START = "<!-- SEO-HEAD:START -->";
const HEAD_END = "<!-- SEO-HEAD:END -->";
const BODY_START = "<!-- SEO-BODY:START -->";
const BODY_END = "<!-- SEO-BODY:END -->";
const ROUTE_PATTERN = /^[a-z][a-z-]*$/;
const MAX_DESCRIPTION = 150;

/** HTML の文字として安全にする（属性の値・本文のどちらにも使う）。 */
export function escapeHtml(text) {
    return String(text)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

/** ルート（先頭の / なし。ホームは空）→ ファイル名。ホームは home.html（Worker が / で返す。index.html は SPA のフォールバック用）。 */
export function pageFile(route) {
    return route === "" ? "home.html" : `${route}.html`;
}

/** 汎用の head（SPA のフォールバック用の index.html）。存在しない URL に、ホームの title・本文を出さないため。 */
export function buildFallbackHead() {
    return [`<title>${escapeHtml(SITE_NAME)}</title>`].join("\n    ");
}

/** ルート → 正規の URL（ホームは末尾の / 付き）。 */
export function pageUrl(origin, route) {
    return route === "" ? `${origin}/` : `${origin}/${route}`;
}

/** 入口ページの一覧を確かめる。おかしければ例外。 */
export function validatePages(pages) {
    if (!Array.isArray(pages) || pages.length === 0) throw new Error("入口ページが空です");
    const routes = new Set();
    for (const page of pages) {
        const where = `route "${page.route}"`;
        if (typeof page.route !== "string" || (page.route !== "" && !ROUTE_PATTERN.test(page.route))) {
            throw new Error(`${where}: 小文字・ハイフンだけのルートにしてください`);
        }
        if (routes.has(page.route)) throw new Error(`${where}: 重複しています`);
        routes.add(page.route);
        for (const key of ["title", "heading", "label", "description"]) {
            if (typeof page[key] !== "string" || page[key].trim() === "") throw new Error(`${where}: ${key} がありません`);
        }
        if (page.description.length > MAX_DESCRIPTION) throw new Error(`${where}: description が長すぎます`);
        if (!Array.isArray(page.paragraphs) || page.paragraphs.length === 0) throw new Error(`${where}: paragraphs がありません`);
    }
}

/** head の中身（title・description・canonical・OGP・Twitter カード）。ホームも、/ で返す home.html として canonical を付ける。 */
export function buildHead(page, origin) {
    const url = pageUrl(origin, page.route);
    return [
        `<title>${escapeHtml(page.title)}</title>`,
        `<meta name="description" content="${escapeHtml(page.description)}" />`,
        `<link rel="canonical" href="${escapeHtml(url)}" />`,
        `<meta property="og:site_name" content="${escapeHtml(SITE_NAME)}" />`,
        `<meta property="og:locale" content="ja_JP" />`,
        `<meta property="og:type" content="website" />`,
        `<meta property="og:title" content="${escapeHtml(page.title)}" />`,
        `<meta property="og:description" content="${escapeHtml(page.description)}" />`,
        `<meta property="og:url" content="${escapeHtml(url)}" />`,
        `<meta name="twitter:card" content="summary" />`,
    ].join("\n    ");
}

/** #app の中の本文（検索エンジンが読める実テキスト。Blazor が起動すると置き換わる）。主なページへのリンクを付ける。 */
export function buildBody(page, pages) {
    const paragraphs = page.paragraphs.map((text) => `<p>${escapeHtml(text)}</p>`).join("\n                ");
    const links = pages
        .map((p) => `<li><a href="${p.route === "" ? "/" : `/${p.route}`}">${escapeHtml(p.label)}</a></li>`)
        .join("");
    // アプリのヘッダー（濃い紫の帯）と、背景・カードの色に近づける（app.css の .entry-static。見た目は起動の前後でなるべく変えない）
    return [
        `<main class="entry-static" data-route="${page.route}">`,
        `        <div class="entry-static-bar">${escapeHtml(SITE_NAME)}</div>`,
        `        <div class="entry-static-body">`,
        `            <div class="entry-static-card">`,
        `                <h1>${escapeHtml(page.heading)}</h1>`,
        `                ${paragraphs}`,
        `            </div>`,
        `            <nav class="entry-static-nav" aria-label="ページ一覧"><ul role="list">${links}</ul></nav>`,
        `        </div>`,
        `        <noscript><p class="entry-static-note">この画面を使うには JavaScript を有効にしてください。</p></noscript>`,
        `    </main>`,
    ].join("\n");
}

/** start と end の間を content に置き換える。区切りは 1 つだけあること。 */
function replaceBetween(text, start, end, content) {
    const s = text.indexOf(start);
    const e = text.indexOf(end);
    if (s < 0 || e < 0 || e < s || text.indexOf(start, s + 1) >= 0 || text.indexOf(end, e + 1) >= 0) {
        throw new Error(`テンプレートの区切り ${start} / ${end} が 1 つずつ見つかりません（wwwroot/index.html を確かめてください）`);
    }
    return text.slice(0, s + start.length) + "\n    " + content + "\n    " + text.slice(e);
}

/** テンプレート（index.html）から、1 ページ分の HTML を作る。 */
export function renderPage(template, page, pages, origin) {
    const withHead = replaceBetween(template, HEAD_START, HEAD_END, buildHead(page, origin));
    return replaceBetween(withHead, BODY_START, BODY_END, buildBody(page, pages));
}

/** robots.txt。本番だけ Sitemap を書く。確認用は全体を止めない（noindex のヘッダーで止める）。 */
export function renderRobots(env, origin) {
    if (env === "develop") return "User-agent: *\nAllow: /\n";
    return [
        "User-agent: *",
        ...DISALLOW.map((path) => `Disallow: ${path}`),
        "",
        `Sitemap: ${origin}/sitemap.xml`,
        "",
    ].join("\n");
}

/** sitemap.xml（本番だけ）。入口ページの正規の URL。 */
export function renderSitemap(pages, origin) {
    const urls = pages.map((p) => `  <url><loc>${escapeHtml(pageUrl(origin, p.route))}</loc></url>`).join("\n");
    return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`;
}

/** 公開用のフォルダ（dir）に、入口ページ・robots.txt・sitemap.xml を書き出す。 */
export function run({ dir, env, pagesFile = DEFAULT_PAGES_FILE }) {
    if (!Object.hasOwn(SITES, env)) throw new Error(`--env は ${Object.keys(SITES).join(" か ")} を指定してください`);
    const origin = SITES[env];
    const pages = JSON.parse(readFileSync(pagesFile, "utf8"));
    validatePages(pages);
    const indexPath = join(dir, "index.html");
    if (!existsSync(indexPath)) throw new Error(`${indexPath} がありません（先に dotnet publish してください）`);
    // テンプレートは最初に 1 回だけ読む（index.html はホームの内容で書き換えるので、後から読み直さない）
    const template = readFileSync(indexPath, "utf8");
    const outputs = pages.map((page) => ({ file: pageFile(page.route), html: renderPage(template, page, pages, origin) }));
    // SPA のフォールバック（index.html）は、汎用の title と読み込み表示だけにする（ホームの本文を、存在しない URL に出さない）
    outputs.push({ file: "index.html", html: replaceBetween(template, HEAD_START, HEAD_END, buildFallbackHead()) });
    for (const { file, html } of outputs) writeFileSync(join(dir, file), html);
    writeFileSync(join(dir, "robots.txt"), renderRobots(env, origin));
    if (env === "production") writeFileSync(join(dir, "sitemap.xml"), renderSitemap(pages, origin));
    return outputs.map((o) => o.file);
}

function parseArgs(argv) {
    const args = { env: "production", dir: join(root, "publish", "wwwroot") };
    for (let i = 0; i < argv.length; i++) {
        if (argv[i] === "--env") args.env = argv[++i];
        else if (argv[i] === "--dir") args.dir = resolve(argv[++i]);
        else throw new Error(`知らない引数: ${argv[i]}`);
    }
    return args;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = parseArgs(process.argv.slice(2));
    const files = run(args);
    const extra = args.env === "production" ? "robots.txt, sitemap.xml" : "robots.txt";
    console.log(`入口ページを書き出しました: ${files.join(", ")}, ${extra}`);
}

export { HEAD_START, HEAD_END, BODY_START, BODY_END };
