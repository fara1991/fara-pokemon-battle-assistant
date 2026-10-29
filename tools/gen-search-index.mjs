// サイト内検索の索引（src/FaraPokemonBattleAssistant.Web/wwwroot/search-index.json）を、各ページの Blazor のソース（.razor）から作る。
//   node tools/gen-search-index.mjs          索引を書き出す
//   node tools/gen-search-index.mjs --check  書き出した索引が今のページと合っているか確かめる（合っていなければ失敗。CI で使う）
// ページの見出し（h2・h4〜h6・カードの見出し）ごとに「節」を作り、その中の文章（段落・ラベル・表・リスト）を本文として持つ。
// 用語・略語ページの <dt>/<dd> は「用語」、チャットコマンド画面のコマンド一覧は「コマンド」として持つ。
// 入力欄の値や計算結果など、実行時に決まる文字（@(…) など）は含めない。ポケモン・わざの名前などのデータは、実行時にアプリが足す。
// 抜き出した文字に C# のコードの断片が混ざっていたら、索引を作らずに失敗する（検索結果にコードが出ないようにするため）。
import { readFileSync, writeFileSync, readdirSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const pagesDir = join(root, "src/FaraPokemonBattleAssistant.Web/Components/Pages");
const outFile = join(root, "src/FaraPokemonBattleAssistant.Web/wwwroot/search-index.json");
const SITE = " | Pokémon Battle Assistant";
// タグ（属性の値の中の > や => で途切れないようにする）
const TAG = /<\/?[A-Za-z][^<>"']*(?:(?:"[^"]*"|'[^']*')[^<>"']*)*>/g;
// 実行時に決まる文字が抜けて、見出しが中身のない括弧になったときの直し（ページに書いてある元の文字）
const TITLE_FIXES = { "相手の を推定": "相手の努力値（ポイント）を推定" };
const usedTitleFixes = new Set();
// ページの読み（ひらがな。ローマ字で探すための、題名の読み）。キーは <PageTitle> の題名。ページを足したら、ここにも足す（無いと失敗する）
// 長音は「ー」で書く。区切りの記号（・など）は入れない
const PAGE_READINGS = {
    "ホーム": "ほーむ",
    "API": "えーぴーあい",
    "更新履歴": "こうしんりれき",
    "チャットコマンド": "ちゃっとこまんど",
    "権利表記": "けんりひょうき",
    "ダメージ計算機": "だめーじけいさんき",
    "データ共有": "でーたきょうゆう",
    "用語・略語": "ようごりゃくご",
    "わざ一覧": "わざいちらん",
    "ポケモン一覧": "ぽけもんいちらん",
    "プライバシーポリシー": "ぷらいばしーぽりしー",
    "問い合わせ": "といあわせ",
    "相手チーム": "あいてちーむ",
    "育成ポケモン": "いくせいぽけもん",
    "素早さ比較": "すばやさひかく",
    "自分チーム": "じぶんちーむ",
    "タイプ相性表": "たいぷあいしょうひょう",
};
const usedReadings = new Set();
// C# の断片が残っていないかの確認
const CODE_LEAK = /\b(?:var|if|else|new|foreach|case)\b\s|\bbreak;|=>|="|\?\?|[\w.]\([^)\u3040-\u30ff\u4e00-\u9fff\uff00-\uffef]*\)/;

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

/** Blazor のソースの C# の部分（@code ブロック・コメント・@(...) など）を取り除く。括弧の対応が取れないときは失敗する */
function stripRazor(src, file) {
    let text = src.replace(/@\*[\s\S]*?\*@/g, "");
    for (;;) {
        const m = /@code\s*\{/.exec(text);
        if (!m) break;
        const end = matching(text, m.index + m[0].length - 1, "{", "}");
        if (end < 0) throw new Error(`${file}: @code の閉じ括弧が見つかりません`);
        text = text.slice(0, m.index) + text.slice(end + 1);
    }
    // @{ ... } のコードブロックと、@(...) ・ @if (...) @foreach (...) ・ @Foo(...) などの式・条件（括弧の対応を数えて取り除く）
    for (;;) {
        const m = /@(?:\{|\(|(?:if|foreach|for|while|switch|lock|using)\s*\(|[A-Za-z_][\w.]*\()/.exec(text);
        if (!m) break;
        const from = m.index + m[0].length - 1;
        const isBrace = m[0].endsWith("{");
        const endPos = matching(text, from, isBrace ? "{" : "(", isBrace ? "}" : ")");
        if (endPos < 0) throw new Error(`${file}: ${m[0]} の閉じ括弧が見つかりません（${text.slice(m.index, m.index + 40)}…）`);
        text = text.slice(0, m.index) + " " + text.slice(endPos + 1);
    }
    // @if の続きの else if (...) { … } else { … }（括弧の対応を数えて、条件を取り除く）
    for (;;) {
        const m = /\belse\s+if\s*\(/.exec(text);
        if (!m) break;
        const endPos = matching(text, m.index + m[0].length - 1, "(", ")");
        if (endPos < 0) throw new Error(`${file}: else if の閉じ括弧が見つかりません`);
        text = text.slice(0, m.index) + " " + text.slice(endPos + 1);
    }
    return text.replace(/\belse\b(?=\s*\{)/g, " ").replace(/@[A-Za-z_][\w.:]*/g, " ");
}

const ENTITIES = { "&amp;": "&", "&lt;": "<", "&gt;": ">", "&quot;": '"', "&#39;": "'", "&nbsp;": " ", "&times;": "×", "&rarr;": "→", "&larr;": "←" };
function plain(html) {
    return html
        .replace(/<Help\b[^>]*>([\s\S]*?)<\/Help>/g, " ")       // ヘルプの吹き出しは、見出しの文字に混ぜない
        .replace(TAG, " ")
        .replace(/&[#\w]+;/g, (e) => ENTITIES[e] ?? " ")
        // 表示の繰り返し（@foreach）の中の C# の断片（宣言・条件・メソッド呼び出し・代入）が混ざったら取り除く
        .replace(/\b(?:var|int|string|bool)\s[^;<>]{0,150};/g, " ")
        .replace(/\bvar\b\s*\d*\s*\??/g, " ")
        .replace(/\bif\s*\((?:[^()]|\([^()]*\))*\)(?:\s*is\s+\w+\))?/g, " ")
        .replace(/[\w.]+\([^()\u3040-\u30ff\u4e00-\u9fff\uff00-\uffef]*\)"?/g, " ")
        .replace(/[\w.]+\s*=\s*[\w.]+;/g, " ")
        .replace(/"\s*=\s*"|"\s*>/g, " ")
        .replace(/\S*(?:="|=>)\S*/g, " ")               // 属性・ラムダの残り
        .replace(/(?:^|\s)#(?=\s|$)/g, " ")               // 番号（#@id）の # だけが残ったもの
        .replace(/[{}]/g, " ")
        // 実行時に決まる文字が抜けて、中身のなくなった括弧
        .replace(/[（(]\s*[/／・、]?\s*[体匹件]?\s*[）)]/g, " ")
        .replace(/\s+/g, " ")
        .trim();
}

/**
 * 問い合わせのページは、kit の ReportFormView が画面を出す（Report.razor には文言が無い）。検索の索引には、kit の画面が出す文言（題名・説明・ラベル・案内）を入れる。
 * 説明の既定・ラベル・送信前の案内は kit（external/fara-blazor-component/src/FaraBlazorComponent.Razor/Components/ReportFormView.razor）と同じ。連絡先の説明は Report.razor の ContactHint
 */
function reportFormView(src) {
    if (!src.includes("<ReportFormView")) return null;
    const hint = /ContactHint="([^"]*)"/.exec(src)?.[1] ?? "";
    const title = "問い合わせ";
    const body = `<div class="page-header page-column"><h3>${title}</h3><p>不具合・要望・ご意見を、作者に直接送れます。いただいた内容は、作者に通知されます（公開はされません）。</p></div>
<label>種類</label><label>内容</label><label>連絡先（任意）</label><p>${hint}</p>
<p>送信すると、プライバシーポリシーに書いたとおり、内容が作者に通知されます。個人情報（パスワード・アクセスキーなど）は書かないでください。</p>`;
    return { title, body };
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
        if (current.type === "term" || text || current.type !== "intro") out.push({ type: current.type, title: (usedTitleFixes.add(current.title), TITLE_FIXES[current.title] ?? current.title), page, url, text: text.slice(0, 1500) });
    };
    // 見出し・用語は節の区切り、それ以外の文章は今の節に足す
    const re = /<(h[2-6]|dt|dd|p|label|li|td|th|small|summary|legend|MudSwitch|MudCheckBox)\b[^>]*>([\s\S]*?)<\/\1>/g;
    let m;
    while ((m = re.exec(html)) !== null) {
        const tag = m[1], text = plain(m[2]);
        if (!text) continue;
        if (tag === "h3") continue;                       // ページの題名は、ページ自体の項目にする
        if (/^h[2456]$/.test(tag)) { push(); current = { type: "section", title: text, text: [] }; }
        else if (tag === "dt") { push(); current = { type: "term", title: text, text: [] }; }
        else current.text.push(text);
    }
    push();
    return out.filter((e) => e.type !== "intro" || e.text);
}

/** ページの説明（見出しの下の <div class="page-header …"> の中の最初の段落）。page-header に他のクラス（page-column など）が付いていても取る */
export function pageDescription(body) {
    const lead = /<div class="page-header(?:\s[^"]*)?">[\s\S]*?<\/div>/.exec(body);
    return lead ? plain((/<p\b[^>]*>([\s\S]*?)<\/p>/.exec(lead[0]) ?? [, ""])[1]) : "";
}

function build() {
    const entries = [];
    for (const file of readdirSync(pagesDir).filter((f) => f.endsWith(".razor")).sort()) {
        const raw = readFileSync(join(pagesDir, file), "utf8");
        const url = pageUrl(raw);
        if (!url || file === "LegacyUrlRedirect.razor") continue;
        const report = reportFormView(raw);
        const title = report?.title ?? pageTitle(raw) ?? file.replace(".razor", "");
        const reading = PAGE_READINGS[title];
        if (!reading) throw new Error(`${file}: ページ「${title}」の読みが PAGE_READINGS にありません（ひらがなで足してください）`);
        if (!/^[ぁ-ゖー]+$/.test(reading)) throw new Error(`${file}: 「${title}」の読みは、ひらがなと「ー」だけにしてください`);
        usedReadings.add(title);
        const body = report?.body ?? stripRazor(raw, file);
        const description = pageDescription(body);
        entries.push({ type: "page", title, page: title, url, text: description, reading });
        for (const e of sectionsOf(body, title, url)) {
            // 見出しの前の導入の文章は、ページの説明と同じなら重複するので入れない
            if (e.type === "intro") { if (e.text !== description) entries.push({ ...e, type: "section" }); }
            else entries.push(e);
        }
        if (file === "Command.razor") {
            // チャットコマンドの一覧（C# の yield return new(構文, 種類, 説明, …)。文字列は $"…" のことがある）
            // 説明が条件式のもの（add -m）は、式の中の文字列をつなげる
            const re = /yield return new\(\$?"([^"]*)",\s*\$?"([^"]*)",\s*([\s\S]*?),\s*new\[\]/g;
            let m;
            let count = 0;
            while ((m = re.exec(raw)) !== null) {
                // 説明の中の {…}（C# の式の埋め込み）は、括弧の対応を数えて取り除く
                const dropBraces = (t) => {
                    for (;;) {
                        const i = t.indexOf("{");
                        if (i < 0) return t;
                        const j = matching(t, i, "{", "}");
                        if (j < 0) throw new Error(`Command.razor: コマンドの説明の { の閉じ括弧が見つかりません（${t.slice(i, i + 40)}…）`);
                        t = t.slice(0, i) + t.slice(j + 1);
                    }
                };
                const fill = (t) => dropBraces(t.replace(/\{evLabel\}/g, "努力値（ポイント）").replace(/\{p\}/g, "!poke"));
                const syntax = fill(m[1]);
                // 式の埋め込み（{…}）を先に取り除いてから、式の中の文字列を取り出す（埋め込みの中の文字列を拾わないように）
                const description = [...fill(m[3]).matchAll(/\$?"([^"]*)"/g)].map((x) => x[1]).join(" / ");
                entries.push({ type: "command", title: syntax, page: title, url, text: `${m[2]}。${description}` });
                count++;
            }
            const expected = (raw.match(/yield return new\(/g) ?? []).length;
            if (count !== expected) throw new Error(`Command.razor: コマンドが ${expected} 件あるのに、${count} 件しか取り出せません`);
        }
    }
    // 同じ内容の重複（同じ見出し・同じ本文）は 1 件にする
    const seen = new Set();
    const result = entries.filter((e) => { const k = `${e.type}|${e.url}|${e.title}|${e.text}`; if (seen.has(k)) return false; seen.add(k); return true; });
    for (const key of Object.keys(TITLE_FIXES)) {
        if (!usedTitleFixes.has(key)) throw new Error(`TITLE_FIXES の「${key}」に当たる見出しがありません（ページの見出しが変わった？ 直すか、消してください）`);
    }
    for (const key of Object.keys(PAGE_READINGS)) {
        if (!usedReadings.has(key)) throw new Error(`PAGE_READINGS の「${key}」に当たるページがありません（ページの題名が変わった？ 直すか、消してください）`);
    }
    const leaks = result.filter((e) => CODE_LEAK.test(e.title) || CODE_LEAK.test(e.text));
    if (leaks.length > 0) {
        throw new Error("C# の断片が混ざっています:\n" + leaks.slice(0, 8).map((e) => `  ${e.page} / ${e.title}: ${e.text.slice(0, 80)}`).join("\n"));
    }
    return result;
}

function main() {
    let json;
    try {
        json = JSON.stringify(build(), null, 1) + "\n";
    } catch (e) {
        console.error(String(e.message ?? e));
        process.exit(1);
    }
    if (process.argv.includes("--check")) {
        const current = existsSync(outFile) ? readFileSync(outFile, "utf8") : "";
        if (current.replace(/\r\n/g, "\n") !== json) {
            console.error("search-index.json が、ページの内容と合っていません。`npm run gen:search` で作り直してください。");
            process.exit(1);
        }
        console.log("search-index.json は最新です");
    } else {
        writeFileSync(outFile, json, "utf8");
        const n = JSON.parse(json);
        console.log(`search-index.json を書き出しました（${n.length} 件、${(json.length / 1024).toFixed(0)} KB）`);
    }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) main();
