// 検索の入口になるページの静的な HTML・robots.txt・sitemap.xml・_headers を作る（公開用の dotnet publish の後に実行する）。
//   node tools/build-entry-pages.mjs [--env production|develop] [--dir publish/wwwroot]
//
// 作り方（head・区切り・robots・sitemap・_headers の中身）は kit の build/build-pages.mjs と csp/render-headers.mjs。
// ここに残るのは、pokemon の設定（tools/site.json）・入口ページの一覧（tools/entry-pages.json）・本文（tools/entry-body.mjs）と、テンプレートの扱い。
//
// 作るもの（--dir の中）: home.html（ホーム。Worker が / で返す）・<route>.html・index.html（SPA のフォールバック）・robots.txt・sitemap.xml（本番だけ）・_headers
// テンプレートは --dir の index.html（dotnet publish の出力）。先に obj/entry-template へ写してから読む（kit は出力先で上書きする設定を拒否するため）。
import { readFileSync, writeFileSync, existsSync, mkdirSync, copyFileSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildPages, writeFiles, assertTemplateNotOverwritten } from "../external/fara-site-kit/build/build-pages.mjs";
import { buildBody } from "./entry-body.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const SITE_FILE = join(root, "tools", "site.json");
const ENTRIES_FILE = join(root, "tools", "entry-pages.json");
const TEMPLATE_DIR = join(root, "obj", "entry-template");
const ROUTE_PATTERN = /^[a-z][a-z-]*$/;
const ENVIRONMENTS = ["production", "develop"];

/** 入口ページの一覧を確かめる。おかしければ例外（kit が見ない項目。ページの一覧の形は pokemon のもの）。 */
export function validateEntries(entries) {
    if (!Array.isArray(entries) || entries.length === 0) throw new Error("入口ページが空です");
    const routes = new Set();
    for (const entry of entries) {
        const where = `route "${entry.route}"`;
        if (typeof entry.route !== "string" || (entry.route !== "" && !ROUTE_PATTERN.test(entry.route))) {
            throw new Error(`${where}: 小文字・ハイフンだけのルートにしてください`);
        }
        if (routes.has(entry.route)) throw new Error(`${where}: 重複しています`);
        routes.add(entry.route);
        for (const key of ["title", "heading", "label", "description"]) {
            if (typeof entry[key] !== "string" || entry[key].trim() === "") throw new Error(`${where}: ${key} がありません`);
        }
        if (!Array.isArray(entry.paragraphs) || entry.paragraphs.length === 0) throw new Error(`${where}: paragraphs がありません`);
    }
}

/** 入口ページの一覧を、kit の入力（path・title・description・bodyHtml）にする。 */
export function toKitPages(entries) {
    return entries.map((entry) => ({
        path: entry.route === "" ? "/" : `/${entry.route}`,
        title: entry.title,
        description: entry.description,
        bodyHtml: buildBody(entry, entries),
    }));
}

/** dir（dotnet publish の wwwroot）に、入口ページ・robots.txt・sitemap.xml・_headers を書き出す。書いたファイル名を返す。 */
export function run({ dir, env, siteFile = SITE_FILE, entriesFile = ENTRIES_FILE }) {
    if (!ENVIRONMENTS.includes(env)) throw new Error(`--env は ${ENVIRONMENTS.join(" か ")} を指定してください`);
    const indexPath = join(dir, "index.html");
    if (!existsSync(indexPath)) throw new Error(`${indexPath} がありません（先に dotnet publish してください）`);
    // テンプレートは出力先の外に写してから読む（同じフォルダの index.html は書き換えられるため）
    mkdirSync(TEMPLATE_DIR, { recursive: true });
    const templatePath = join(TEMPLATE_DIR, "index.html");
    copyFileSync(indexPath, templatePath);
    const entries = JSON.parse(readFileSync(entriesFile, "utf8"));
    validateEntries(entries);
    const site = JSON.parse(readFileSync(siteFile, "utf8"));
    const template = readFileSync(templatePath, "utf8");
    const files = buildPages({ site, pages: toKitPages(entries), template, env });
    assertTemplateNotOverwritten(templatePath, dir, files);
    return writeFiles(resolve(dir), files);
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
    try {
        const written = run(parseArgs(process.argv.slice(2)));
        console.log(`入口ページを書き出しました: ${written.join(", ")}`);
    } catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}
