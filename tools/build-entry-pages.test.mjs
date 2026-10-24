// 入口ページの静的 HTML・robots.txt・sitemap.xml・_headers の生成（tools/build-entry-pages.mjs）の確認。
//   node --test tools/build-entry-pages.test.mjs
// 作り方そのもの（head・区切り・CSP の形）は kit の検査（external/fara-site-kit/tests）が見る。ここでは pokemon の入力と、呼び出しの配線を見る。
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { run, validateEntries, toKitPages, discoverPageRoutes } from "./build-entry-pages.mjs";
import { checkRoutes } from "./check-routes.mjs";
import { buildBody, escapeHtml } from "./entry-body.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const pages = JSON.parse(readFileSync(join(here, "entry-pages.json"), "utf8"));
const site = JSON.parse(readFileSync(join(here, "site.json"), "utf8"));
// テンプレートは Blazor の wwwroot/index.html（dotnet publish の元。区切りや読み込み表示がずれないように、そのまま読む）
const TEMPLATE = readFileSync(join(here, "..", "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "index.html"), "utf8");
const BOOT_INDICATOR = 'id="boot-indicator"';

const battleTerms = pages.find((p) => p.route === "battle-terms");

/** 一時フォルダに、テンプレートを index.html として置いて run() を実行し、出力の名前 → 中身 を返す。 */
/** HTML のページ（読み込み表示・起動前の覆いを確かめる対象）だけ。 */
function htmlOutputs(outputs) {
    return [...outputs.entries()].filter(([name]) => name.endsWith(".html")).map(([, html]) => html);
}

function generate(env) {
    const dir = mkdtempSync(join(tmpdir(), `entry-pages-${env}-`));
    writeFileSync(join(dir, "index.html"), TEMPLATE);
    const written = run({ dir, env });
    const out = new Map(readdirSync(dir).sort().map((name) => [name, readFileSync(join(dir, name), "utf8")]));
    return { dir, written, out };
}

const prod = generate("production");
const dev = generate("develop");

test("ホームは home.html（Worker が / で返す）、そのほかは <route>.html。kit の path は / と /<route>", () => {
    assert.ok(prod.out.has("home.html"));
    assert.ok(prod.out.has("battle-terms.html"));
    assert.deepEqual(toKitPages(pages).map((p) => p.path).slice(0, 2), ["/", "/damage-calculator"]);
});

test("production: canonical と title は本番のホスト。develop は確認用のホスト（noindex なので、正規の URL も確認用）", () => {
    assert.match(prod.out.get("battle-terms.html"), /<link rel="canonical" href="https:\/\/pokemon\.fara-labs\.com\/battle-terms" \/>/);
    assert.match(prod.out.get("home.html"), /<link rel="canonical" href="https:\/\/pokemon\.fara-labs\.com\/" \/>/);
    assert.match(dev.out.get("battle-terms.html"), /<link rel="canonical" href="https:\/\/pokemon-dev\.fara-labs\.com\/battle-terms" \/>/);
    assert.match(prod.out.get("battle-terms.html"), /<title>用語・略語 \| Pokémon Battle Assistant<\/title>/);
});

test("本文（entry-body）: 属性と本文の文字はエスケープし、段落と主なページへのリンクを入れる", () => {
    const tricky = { ...battleTerms, heading: "<h>", paragraphs: ["<i>&"] };
    const body = buildBody(tricky, pages);
    assert.match(body, /<h1>&lt;h&gt;<\/h1>/);
    assert.match(body, /<p>&lt;i&gt;&amp;<\/p>/);
    assert.equal(escapeHtml('a"<b>&'), "a&quot;&lt;b&gt;&amp;");
    const plain = buildBody(battleTerms, pages);
    for (const text of battleTerms.paragraphs) assert.ok(plain.includes(escapeHtml(text)));
    for (const p of pages) assert.ok(plain.includes(`href="${p.route === "" ? "/" : `/${p.route}`}"`), p.route);
    assert.match(plain, /<noscript>/);
    // list-style: none で list の意味が消えるため（Safari・VoiceOver）、ul に role="list" を付ける
    assert.match(plain, /<ul role="list">/);
});

test("テンプレートの区切りの中だけを書き換え、#app・Blazor の読み込みは残す", () => {
    for (const html of [prod.out.get("battle-terms.html"), prod.out.get("home.html")]) {
        assert.match(html, /<main class="entry-static" data-route="/);
        assert.match(html, /_framework\/blazor\.webassembly\.js/);
        assert.match(html, /<base href="\/" \/>/);
    }
});

test("読み込み表示（#boot-indicator）は #app の外・本文の前に 1 つだけ（本文の中には入らない）", () => {
    for (const html of htmlOutputs(prod.out)) {
        assert.equal(html.split(BOOT_INDICATOR).length - 1, 1);
        const indicator = html.indexOf(BOOT_INDICATOR);
        assert.ok(indicator < html.indexOf('<div id="app">'), "#app より前（#app の外）にある");
        const main = html.indexOf("<main");
        if (main >= 0) {
            assert.ok(indicator < main, "本文より前にある");
            assert.doesNotMatch(html.slice(main, html.indexOf("</main>", main)), /読み込み中|boot-indicator/);
        }
    }
});

test("起動前のオーバーレイのスタイルはインラインで、最初の stylesheet より前にある。inert は直接書かない", () => {
    for (const html of htmlOutputs(prod.out)) {
        const style = html.indexOf("<style>");
        assert.ok(style >= 0 && style < html.indexOf('<link href="_content/MudBlazor/MudBlazor.min.css"'));
        assert.match(html, /animation: boot-indicator-release 0s linear 60s forwards;/);
        assert.doesNotMatch(html, /inert/);
    }
});

test("SPA のフォールバック（index.html）は汎用の title と読み込み表示だけ（ホームの本文・説明を出さない）", () => {
    const fallback = prod.out.get("index.html");
    assert.match(fallback, /<title>Pokémon Battle Assistant<\/title>/);
    assert.doesNotMatch(fallback, /ホーム|entry-static|description/);
    assert.match(fallback, /id="boot-indicator"/);
});

test("robots.txt: 本番は Sitemap と個人のページの Disallow。確認用は Disallow も Sitemap も付けない（noindex のヘッダーで止める）", () => {
    const robots = prod.out.get("robots.txt");
    assert.match(robots, /^Disallow: \/my-$/m);
    assert.match(robots, /^Disallow: \/api\/my\/$/m);
    assert.match(robots, /^Sitemap: https:\/\/pokemon\.fara-labs\.com\/sitemap\.xml$/m);
    assert.doesNotMatch(robots, /api\/game/);
    assert.equal(dev.out.get("robots.txt"), "User-agent: *\nAllow: /\n");
});

test("sitemap.xml は本番だけ。入口ページの正規の URL だけを並べる", () => {
    const xml = prod.out.get("sitemap.xml");
    assert.equal((xml.match(/<url>/g) || []).length, pages.length);
    assert.match(xml, /<loc>https:\/\/pokemon\.fara-labs\.com\/battle-terms<\/loc>/);
    assert.doesNotMatch(xml, /\.html|my-|data-share/);
    assert.ok(!dev.out.has("sitemap.xml"));
});

test("_headers: 本番の CSP に Cloudflare・Turnstile・Google Fonts の配信元が入り、script の 'unsafe-inline' は無い。確認用は noindex を付ける", () => {
    const headers = prod.out.get("_headers");
    assert.match(headers, /script-src 'self' 'wasm-unsafe-eval' https:\/\/static\.cloudflareinsights\.com https:\/\/challenges\.cloudflare\.com;/);
    assert.match(headers, /style-src 'self' 'unsafe-inline' https:\/\/fonts\.googleapis\.com;/);
    assert.match(headers, /font-src 'self' https:\/\/fonts\.gstatic\.com;/);
    assert.match(headers, /img-src 'self' data: https:\/\/raw\.githubusercontent\.com;/);
    const csp = /^\s*Content-Security-Policy: (.*)$/m.exec(headers)?.[1] ?? "";
    assert.ok(csp.includes("script-src"), "CSP の行がある");
    assert.doesNotMatch(csp, /script-src[^;]*'unsafe-inline'/);
    assert.match(dev.out.get("_headers"), /X-Robots-Tag: noindex/);
    assert.doesNotMatch(headers, /X-Robots-Tag/);
    assert.equal(site.csp.scriptSrc.length, 2);
});

test("同じフォルダで作り直しても同じ結果（出力の index.html を次の実行のテンプレートにしても崩れない）", () => {
    const again = run({ dir: prod.dir, env: "production" });
    assert.deepEqual(again, prod.written);
    for (const name of prod.written) {
        assert.equal(readFileSync(join(prod.dir, name), "utf8"), prod.out.get(name), name);
    }
});

test("入口ページの一覧の確認: 重複・不正なルート・空の項目は失敗する", () => {
    assert.doesNotThrow(() => validateEntries(pages));
    assert.throws(() => validateEntries([...pages, battleTerms]), /重複/);
    assert.throws(() => validateEntries([{ ...battleTerms, route: "battle/terms" }]), /ルート/);
    assert.throws(() => validateEntries([{ ...battleTerms, route: "Battle" }]), /ルート/);
    assert.throws(() => validateEntries([{ ...battleTerms, title: " " }]), /title/);
    assert.throws(() => validateEntries([{ ...battleTerms, paragraphs: [] }]), /paragraphs/);
});

test("run: 不正な --env と、index.html が無いフォルダは失敗する", () => {
    const dir = mkdtempSync(join(tmpdir(), "entry-pages-none-"));
    assert.throws(() => run({ dir, env: "staging" }), /--env/);
    assert.throws(() => run({ dir, env: "production" }), /index\.html/);
});

test("CI・deploy・package.json の build-entry-pages の呼び出しは、直前の dotnet publish の出力の wwwroot を渡している", () => {
    const files = [".github/workflows/build.yml", ".github/workflows/deploy.yml", ".github/workflows/deploy-develop.yml", "package.json"];
    let checked = 0;
    for (const file of files) {
        const lines = readFileSync(join(here, "..", file), "utf8").split("\n");
        lines.forEach((line, i) => {
            if (!line.includes("node tools/build-entry-pages.mjs")) return;
            const dirArg = /--dir\s+(\S+)/.exec(line)?.[1] ?? "publish/wwwroot";
            let publishOutput = null;
            for (let j = i; j >= 0 && publishOutput === null; j--) {
                if (lines[j].includes("dotnet publish") && lines[j].includes("FaraPokemonBattleAssistant.Web")) {
                    publishOutput = /(?:--output|-o)\s+(\S+)/.exec(lines[j])?.[1] ?? null;
                }
            }
            assert.ok(publishOutput, `${file}:${i + 1} の前に dotnet publish（出力先）がありません`);
            assert.equal(dirArg.replace(/\\/g, "/").replace(/\/$/, ""), `${publishOutput}/wwwroot`, `${file}:${i + 1} の --dir が publish の出力と違います`);
            checked++;
        });
    }
    assert.equal(checked, 4, "build-entry-pages の呼び出しを 4 か所（build・deploy・deploy-develop・package.json）確かめる");
});

// ---- 存在しない URL は 404（wrangler.jsonc の not_found_handling: "404-page"）。有効なルートには必ず静的 HTML がある ----

/** wrangler.jsonc を読む（行頭の // のコメントだけを外す）。 */
function readWranglerConfig() {
    const text = readFileSync(join(here, "..", "wrangler.jsonc"), "utf8").split("\n").filter((line) => !/^\s*\/\//.test(line)).join("\n");
    return JSON.parse(text);
}

/** 一時の Components フォルダに、指定した内容の .razor を置いて返す。 */
function razorDirWith(files) {
    const dir = mkdtempSync(join(tmpdir(), "razor-"));
    for (const [name, text] of Object.entries(files)) {
        mkdirSync(join(dir, name, ".."), { recursive: true });
        writeFileSync(join(dir, name), text);
    }
    return dir;
}

test("wrangler.jsonc: 本番も develop も not_found_handling は 404-page（SPA のフォールバックに戻さない）。keep_vars と run_worker_first はそのまま", () => {
    const config = readWranglerConfig();
    assert.equal(config.assets.not_found_handling, "404-page");
    assert.equal(config.env.develop.assets.not_found_handling, "404-page");
    assert.equal(config.keep_vars, true);
    const expected = ["/", "/api/*", "/home", "/home.html", "/home/"].sort();   // /home の別名は Worker が / へ 301（worker/home.js）
    assert.deepEqual([...config.assets.run_worker_first].sort(), expected);
    assert.deepEqual([...config.env.develop.assets.run_worker_first].sort(), expected);
});

test("home.html は内部の入口ファイルのまま: canonical は /、sitemap は / だけ（/home を載せない）、静的な _redirects は作らない", () => {
    for (const out of [prod.out, dev.out]) {
        assert.ok(out.has("home.html"));
        assert.ok(!out.has("_redirects"), "_redirects があると Worker の env.ASSETS.fetch(\"/home\") まで転送され、/ が転送ループになる");
    }
    assert.match(prod.out.get("home.html"), /<link rel="canonical" href="https:\/\/pokemon\.fara-labs\.com\/" \/>/);
    const sitemap = prod.out.get("sitemap.xml");
    assert.match(sitemap, /<loc>https:\/\/pokemon\.fara-labs\.com\/<\/loc>/);
    assert.doesNotMatch(sitemap, /\/home/);
    assert.doesNotMatch(prod.out.get("robots.txt"), /home/);
});

test("404.html: noindex・404 用の title・読み込み表示だけ（本文なし）。本番にも develop にも作る", () => {
    for (const out of [prod.out, dev.out]) {
        const html = out.get("404.html");
        assert.ok(html, "404.html がある");
        assert.match(html, /<title>ページが見つかりません \| Pokémon Battle Assistant<\/title>/);
        assert.match(html, /<meta name="robots" content="noindex" \/>/);
        assert.match(html, /id="boot-indicator"/);
        assert.doesNotMatch(html, /canonical/);
    }
    assert.doesNotMatch(prod.out.get("sitemap.xml"), /404/);
});

test("@page のルートは全部、静的 HTML がある（ホームは home.html、入口ページは <route>.html、それ以外は index.html と同じ殻）", () => {
    const routes = discoverPageRoutes();
    assert.ok(routes.includes("") && routes.includes("speed-comparison") && routes.includes("my-pokemon"), "代表的なルートを拾っている");
    const entryRoutes = new Set(pages.map((p) => p.route));
    for (const out of [prod.out, dev.out]) {
        for (const route of routes) {
            if (route === "") assert.ok(out.has("home.html"));
            else assert.ok(out.has(`${route}.html`), `/${route} の ${route}.html がありません（このページが 404 になります）`);
            if (route !== "" && !entryRoutes.has(route)) assert.equal(out.get(`${route}.html`), out.get("index.html"), `${route}.html は殻`);
        }
    }
    // 入口ページは本文つきの別ファイル（殻で上書きしない）
    assert.match(prod.out.get("battle-terms.html"), /<main class="entry-static"/);
});

test("入口ページは全部 @page のルートにある（Blazor に無いページを 200 で返さない）。殻は sitemap に入らない", () => {
    const routes = new Set(discoverPageRoutes());
    for (const entry of pages) assert.ok(routes.has(entry.route), `/${entry.route} の @page がありません`);
    assert.equal((prod.out.get("sitemap.xml").match(/<url>/g) || []).length, pages.length);
    assert.doesNotMatch(prod.out.get("sitemap.xml"), /my-pokemon|data-share|speed</);
    const dir = razorDirWith({ "Pages/A.razor": '@page "/a"\n' });
    const entries = JSON.parse(readFileSync(join(here, "entry-pages.json"), "utf8"));
    const out = mkdtempSync(join(tmpdir(), "entry-pages-bad-"));
    writeFileSync(join(out, "index.html"), TEMPLATE);
    assert.throws(() => run({ dir: out, env: "production", razorDir: dir }), /@page がありません/);
    assert.ok(entries.length > 0);
});

test("discoverPageRoutes: 複数の @page・入れ子のフォルダを拾う。静的 HTML を作れない形（{パラメータ}・大文字・予約名）と @page なしは失敗する", () => {
    const dir = razorDirWith({
        "Pages/A.razor": '@page "/"\n@page "/a"\n@page "/b-c"\n',
        "Pages/Sub/B.razor": '@page "/sub/page"\n',
        "Shared/Layout.razor": "<div></div>",
        "obj/Generated.razor": '@page "/ignored"\n',
    });
    assert.deepEqual(discoverPageRoutes(dir), ["", "a", "b-c", "sub/page"]);
    for (const bad of ['@page "/item/{id}"', '@page "/Upper"', '@page "/index"', '@page "/home"', '@page "/a/"', '@page "/a.b"']) {
        assert.throws(() => discoverPageRoutes(razorDirWith({ "Pages/X.razor": bad })), /静的 HTML/, bad);
    }
    assert.throws(() => discoverPageRoutes(razorDirWith({ "Shared/Layout.razor": "<div></div>" })), /@page が見つかりません/);
});

test("check-routes（CI・公開の前の確認）: 揃っていれば問題なし。ページの HTML や 404.html が欠ける・noindex が無いと検出する", () => {
    assert.deepEqual(checkRoutes(prod.dir), []);
    const dir = mkdtempSync(join(tmpdir(), "check-routes-"));
    writeFileSync(join(dir, "index.html"), TEMPLATE);
    run({ dir, env: "production" });
    rmSync(join(dir, "my-pokemon.html"));
    rmSync(join(dir, "home.html"));
    const problems = checkRoutes(dir).join("\n");
    assert.match(problems, /my-pokemon\.html/);
    assert.match(problems, /home\.html/);
    rmSync(join(dir, "404.html"));
    assert.match(checkRoutes(dir).join("\n"), /404\.html がありません/);
    writeFileSync(join(dir, "404.html"), "<html>no robots</html>");
    assert.match(checkRoutes(dir).join("\n"), /noindex/);
});

test("404.html の <noscript> に、ページが見つかりません・ホームへのリンク・JavaScript の案内がある（JS 無しで空白にならない）", () => {
    for (const out of [prod.out, dev.out]) {
        const html = out.get("404.html");
        const noscript = /<noscript>(<h1>[\s\S]*?)<\/noscript>/.exec(html)?.[1] ?? "";
        assert.match(noscript, /ページが見つかりません/);
        assert.match(noscript, /<a href="\/">ホームへ戻る<\/a>/);
        assert.match(noscript, /JavaScript/);
    }
});

test("ルート収集の取りこぼしがない: AdditionalAssemblies・@attribute [Route]・.razor.cs の [Route]・.cshtml・MapFallback が無い（@page 以外のルート定義は collect できない）", () => {
    const web = join(here, "..", "src", "FaraPokemonBattleAssistant.Web");
    const found = [];
    const walk = (dir) => {
        for (const item of readdirSync(dir, { withFileTypes: true })) {
            const path = join(dir, item.name);
            if (item.isDirectory()) {
                if (item.name !== "bin" && item.name !== "obj" && item.name !== "wwwroot") walk(path);
            } else if (/\.(razor|cs|cshtml)$/.test(item.name)) {
                if (item.name.endsWith(".cshtml")) found.push(`${path}: .cshtml`);
                const text = readFileSync(path, "utf8");
                if (/AdditionalAssemblies/.test(text)) found.push(`${path}: AdditionalAssemblies`);
                if (/@attribute\s+\[\s*Route/.test(text) || /^\s*\[\s*Route\s*\(/m.test(text)) found.push(`${path}: [Route] 属性`);
                if (/MapFallback|AddAdditionalAssemblies/.test(text)) found.push(`${path}: MapFallback 等`);
            }
        }
    };
    walk(web);
    assert.deepEqual(found, [], "@page 以外のルート定義は静的 HTML の収集（discoverPageRoutes）に入りません。収集を拡張してから使ってください");
    assert.match(readFileSync(join(web, "App.razor"), "utf8"), /<Router AppAssembly="@typeof\(App\)\.Assembly">/);
});

test("viewport に viewport-fit=cover がある（ノッチ端末で安全領域の余白を効かせる）。テンプレートと、生成した各 HTML（本番・develop）の両方", () => {
    const VIEWPORT = /<meta name="viewport" content="[^"]*viewport-fit=cover[^"]*"/;
    assert.match(TEMPLATE, VIEWPORT, "wwwroot/index.html の viewport");
    for (const [label, result] of [["production", prod], ["develop", dev]]) {
        const names = [...result.out.keys()].filter((name) => name.endsWith(".html"));
        assert.ok(names.length > 5, `${label}: 生成した HTML が少なすぎます`);
        for (const name of names) {
            assert.match(result.out.get(name), VIEWPORT, `${label}/${name} の viewport に viewport-fit=cover がありません`);
        }
    }
});

test("ファビコンの link は、生成した全 HTML（本番・develop。home.html・404.html を含む）に残り、参照先のファイルが wwwroot にある（/favicon.ico が無いと 404 になる）", () => {
    const wwwroot = join(here, "..", "src", "FaraPokemonBattleAssistant.Web", "wwwroot");
    const LINKS = [
        /<link rel="icon" href="\/favicon\.ico\?v=3" sizes="16x16 32x32 48x48" \/>/,
        /<link rel="icon" href="\/favicon\.svg\?v=3" type="image\/svg\+xml" \/>/,
        /<link rel="icon" href="\/favicon-32x32\.png\?v=3" type="image\/png" sizes="32x32" \/>/,
        /<link rel="icon" href="\/favicon-16x16\.png\?v=3" type="image\/png" sizes="16x16" \/>/,
        /<link rel="apple-touch-icon" href="\/apple-touch-icon\.png\?v=3" \/>/,
    ];
    for (const [label, result] of [["production", prod], ["develop", dev]]) {
        for (const name of ["home.html", "index.html", "404.html", "damage-calculator.html", "privacy.html"]) {
            for (const link of LINKS) assert.match(result.out.get(name), link, `${label}/${name} にファビコンの link がありません`);
        }
    }
    for (const file of ["favicon.ico", "favicon.svg", "favicon-32x32.png", "favicon-16x16.png", "apple-touch-icon.png"]) {
        assert.ok(readFileSync(join(wwwroot, file)).length > 100, `wwwroot/${file} がありません`);
    }
    // SVG は prefers-color-scheme で切り替えない（Safari 非対応。PNG・ICO と見た目をそろえる）
    assert.doesNotMatch(readFileSync(join(wwwroot, "favicon.svg"), "utf8"), /prefers-color-scheme/);
});

test("OGP: 入口ページ（home.html を含む）に og:image（絶対 URL・版つき）と twitter:card=summary_large_image。type・width・height・alt は SEO-HEAD の外（全ページ共通）で、画像は 1200x630 の PNG", () => {
    const wwwroot = join(here, "..", "src", "FaraPokemonBattleAssistant.Web", "wwwroot");
    const image = readFileSync(join(wwwroot, "img", "og-v1.png"));
    assert.equal(image.subarray(1, 4).toString("latin1"), "PNG");
    assert.equal(image.readUInt32BE(16), 1200);
    assert.equal(image.readUInt32BE(20), 630);
    for (const [label, result, origin] of [["production", prod, "https://pokemon.fara-labs.com"], ["develop", dev, "https://pokemon-dev.fara-labs.com"]]) {
        for (const entry of pages) {
            const name = entry.route === "" ? "home.html" : `${entry.route}.html`;
            const html = result.out.get(name);
            assert.ok(html.includes(`<meta property="og:image" content="${origin}/img/og-v1.png" />`), `${label}/${name} に og:image がありません`);
            assert.match(html, /<meta name="twitter:card" content="summary_large_image" \/>/, `${label}/${name} の twitter:card`);
        }
        for (const name of ["home.html", "index.html", "404.html", "damage-calculator.html", "privacy.html"]) {
            const html = result.out.get(name);
            const outside = html.slice(html.indexOf("<!-- SEO-HEAD:END -->"));
            for (const meta of ['og:image:type" content="image/png"', 'og:image:width" content="1200"', 'og:image:height" content="630"', 'og:image:alt" content="']) {
                assert.ok(outside.includes(meta), `${label}/${name} の SEO-HEAD の外に ${meta} がありません`);
            }
        }
    }
});

test("OGP: ogImage が無い・/ で始まらない入口ページは検証で止まる", () => {
    assert.throws(() => validateEntries([{ ...battleTerms, ogImage: undefined }]), /ogImage/);
    assert.throws(() => validateEntries([{ ...battleTerms, ogImage: "img/og-v1.png" }]), /ogImage/);
    assert.doesNotThrow(() => validateEntries([battleTerms]));
});
