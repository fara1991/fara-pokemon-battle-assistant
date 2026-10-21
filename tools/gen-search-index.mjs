// サイト内検索の索引（src/FaraPokemonBattleAssistant.Web/wwwroot/search-index.json）を、各ページの Razor から作る。
//   node tools/gen-search-index.mjs          索引を書き出す
//   node tools/gen-search-index.mjs --check  書き出した索引が今のページと合っているか確かめる（合っていなければ失敗。CI で使う）
// ページの見出し（h3〜h6・カードの見出し）ごとに「節」を作り、その中の文章（段落・ラベル・表・リスト）を本文として持つ。
// 用語・略語ページの <dt>/<dd> は「用語」、チャットコマンド画面のコマンド一覧は「コマンド」として持つ。
// 入力欄の値や計算結果など、実行時に決まる文字（@(…) など）は含めない。ポケモン・わざの名前などのデータは、実行時にアプリが足す。
import { readFileSync, writeFileSync, readdirSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const pagesDir = join(root, "src/FaraPokemonBattleAssistant.Web/Components/Pages");
const outFile = join(root, "src/FaraPokemonBattleAssistant.Web/wwwroot/search-index.json");
const SITE = " | Pokémon Battle Assistant";
// タグ（属性の値の中の > や => で途切れないようにする）
const TAG = /<\/?[A-Za-z][^<>"']*(?:(?:"[^"]*"|'[^']*')[^<>"']*)*>/g;

/** 対応する閉じ括弧まで（文字列リテラルの中の括弧は数えない）。start は開き括弧の位置。見つからなければ -1 */
function matching(text, start, open, close) {
    let depth = 0, quote = "";
    for (let i = start; i < text.length; i++) {
        const c = text[i];
        if (quote) {
            if (c === "\\") i++;
            else if (c === quote) quote = "";
            continue;
        }
        if (c === '"' || c === "'") { quote = c; continue; }
        if (c === open) depth++;
        else if (c === close && --depth === 0) return i;
    }
    return -1;
}

/** Razor の C# の部分（@code ブロック・コメント・@(...) など）を取り除く */
function stripRazor(src) {
    let text = src.replace(/@\*[\s\S]*?\*@/g, "");
    for (;;) {
        const m = /@code\s*\{/.exec(text);
        if (!m) break;
        const end = matching(text, m.index + m[0].length - 1, "{", "}");
        text = text.slice(0, m.index) + (end < 0 ? "" : text.slice(end + 1));
    }
    // @{ ... } のコードブロックと、@(...) ・ @if (...) @foreach (...) ・ @Foo(...) などの式・条件（括弧の対応を数えて取り除く）
    for (;;) {
        const m = /@(?:\{|\(|(?:if|foreach|for|while|switch|lock|using)\s*\(|[A-Za-z_][\w.]*\()/.exec(text);
        if (!m) break;
        const from = m.index + m[0].length - 1;
        const isBrace = m[0].endsWith("{");
        const endPos = matching(text, from, isBrace ? "{" : "(", isBrace ? "}" : ")");
        text = text.slice(0, m.index) + " " + (endPos < 0 ? "" : text.slice(endPos + 1));
    }
    return text.replace(/@[A-Za-z_][\w.:]*/g, " ");
}

const ENTITIES = { "&amp;": "&", "&lt;": "<", "&gt;": ">", "&quot;": '"', "&#39;": "'", "&nbsp;": " ", "&times;": "×", "&rarr;": "→", "&larr;": "←" };
function plain(html) {
    return html
        .replace(/<Help\b[^>]*>([\s\S]*?)<\/Help>/g, " ")       // ヘルプの吹き出しは、見出しの文字に混ぜない
        .replace(TAG, " ")
        .replace(/&[#\w]+;/g, (e) => ENTITIES[e] ?? " ")
        // 表示の繰り返し（@foreach）の中の C# の断片（宣言・条件・メソッド呼び出し・代入）が混ざったら取り除く
        .replace(/(?:var|int|string|bool)\s[^;<>]{0,150};/g, " ")
        .replace(/if\s*\([^)]*\)(?:\s*is\s+\w+\))?/g, " ")
        .replace(/[\w.]+\([^()]*\)"?/g, " ")
        .replace(/[\w.]+\s*=\s*[\w.]+;/g, " ")
        .replace(/"\s*=\s*"|"\s*>/g, " ")
        .replace(/[{}]/g, " ")
        .replace(/\s+/g, " ")
        .trim();
}

function pageUrl(src) {
    const m = /^@page\s+"([^"]+)"/m.exec(src);
    return m ? m[1] : null;
}

function pageTitle(src) {
    const m = /<PageTitle>([\s\S]*?)<\/PageTitle>/.exec(src);
    if (!m) return null;
    const t = plain(m[1]);
    return t.endsWith(SITE) ? t.slice(0, -SITE.length) : t;
}

/** 要素を、文書の中の並びで取り出して、節に分ける */
function sectionsOf(html, page, url) {
    const out = [];
    let current = { type: "intro", title: page, text: [] };
    const push = () => {
        const text = current.text.join(" ").replace(/\s+/g, " ").trim();
        if (current.type === "term" || text || current.type !== "intro") out.push({ type: current.type, title: current.title, page, url, text: text.slice(0, 1500) });
    };
    // 見出し・用語は節の区切り、それ以外の文章は今の節に足す
    const re = /<(h[3-6]|dt|dd|p|label|li|td|th|small|summary|legend)\b[^>]*>([\s\S]*?)<\/\1>/g;
    let m;
    while ((m = re.exec(html)) !== null) {
        const tag = m[1], text = plain(m[2]);
        if (!text) continue;
        if (tag === "h3") continue;                       // ページの題名は、ページ自体の項目にする
        if (/^h[4-6]$/.test(tag)) { push(); current = { type: "section", title: text, text: [] }; }
        else if (tag === "dt") { push(); current = { type: "term", title: text, text: [] }; }
        else current.text.push(text);
    }
    push();
    return out.filter((e) => e.type !== "intro" || e.text);
}

function build() {
    const entries = [];
    for (const file of readdirSync(pagesDir).filter((f) => f.endsWith(".razor")).sort()) {
        const raw = readFileSync(join(pagesDir, file), "utf8");
        const url = pageUrl(raw);
        if (!url || file === "LegacyUrlRedirect.razor") continue;
        const title = pageTitle(raw) ?? file.replace(".razor", "");
        const body = stripRazor(raw);
        const lead = /<div class="page-header">[\s\S]*?<\/div>/.exec(body);
        const description = lead ? plain((/<p\b[^>]*>([\s\S]*?)<\/p>/.exec(lead[0]) ?? [, ""])[1]) : "";
        entries.push({ type: "page", title, page: title, url, text: description });
        for (const e of sectionsOf(body, title, url)) {
            // 見出しの前の導入の文章は、ページの説明と同じなら重複するので入れない
            if (e.type === "intro") { if (e.text !== description) entries.push({ ...e, type: "section" }); }
            else entries.push(e);
        }
        if (file === "Command.razor") {
            // チャットコマンドの一覧（C# の yield return new(構文, 種類, 説明, …)）
            const re = /yield return new\(\$?"([^"]*)",\s*"([^"]*)",\s*"([^"]*)"/g;
            let m;
            while ((m = re.exec(raw)) !== null) {
                const syntax = m[1].replace(/\{p\}/g, "!poke");
                entries.push({ type: "command", title: syntax, page: title, url, text: `${m[2]}。${m[3]}` });
            }
        }
    }
    // 同じ内容の重複（同じ見出し・同じ本文）は 1 件にする
    const seen = new Set();
    return entries.filter((e) => { const k = `${e.type}|${e.url}|${e.title}|${e.text}`; if (seen.has(k)) return false; seen.add(k); return true; });
}

const json = JSON.stringify(build(), null, 1) + "\n";
if (process.argv.includes("--check")) {
    const current = existsSync(outFile) ? readFileSync(outFile, "utf8") : "";
    if (current.replace(/\r\n/g, "\n") !== json) {
        console.error("search-index.json が、ページの内容と合っていません。`node tools/gen-search-index.mjs` で作り直してください。");
        process.exit(1);
    }
    console.log("search-index.json は最新です");
} else {
    writeFileSync(outFile, json, "utf8");
    const n = JSON.parse(json);
    console.log(`search-index.json を書き出しました（${n.length} 件、${(json.length / 1024).toFixed(0)} KB）`);
}
