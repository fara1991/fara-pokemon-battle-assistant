// 存在しない URL が本物の 404 になり、有効な URL は 200 で返ることの確認（wrangler dev で、本番・develop の設定どおりに動かす）。
//   npm run test:routes                                    … テンプレート（wwwroot/index.html）から build-entry-pages で作った一時フォルダで確かめる
//   node tools/not-found-test.mjs --dir publish/wwwroot   … dotnet publish + build-entry-pages の出力で確かめる（npm run publish:web の後）
// 有効なルートは、Blazor の @page と、--dir にある <route>.html から自動で列挙する（手で更新する一覧は持たない）。
// wrangler.jsonc は読み込んで、main と assets.directory だけ差し替えた一時の設定で動かす（not_found_handling などは本物のまま）。
import { spawn } from "node:child_process";
import { cpSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { run, discoverPageRoutes } from "./build-entry-pages.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dirArg = process.argv.indexOf("--dir");
const basePort = Number(process.env.PORT ?? 8811);
let failures = 0;
function check(name, ok, detail = "") {
  console.log(`${ok ? "OK  " : "FAIL"} ${name}${ok || !detail ? "" : ` — ${detail}`}`);
  if (!ok) failures++;
}

/** wrangler.jsonc を読む（行頭の // のコメントだけを外す）。 */
function readWranglerConfig() {
  const text = readFileSync(join(root, "wrangler.jsonc"), "utf8")
    .split("\n")
    .filter((line) => !/^\s*\/\//.test(line))
    .join("\n");
  return JSON.parse(text);
}

/** 公開物のフォルダを用意する。--dir があればそれ、なければテンプレートから作る。 */
function prepareAssets() {
  if (dirArg >= 0) return { dir: resolve(process.argv[dirArg + 1]), cleanup: () => {} };
  const dir = mkdtempSync(join(tmpdir(), "not-found-assets-"));
  cpSync(join(root, "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "index.html"), join(dir, "index.html"));
  run({ dir, env: "production" });
  mkdirSync(join(dir, "_framework"), { recursive: true });
  writeFileSync(join(dir, "_framework", "blazor.webassembly.js"), "// stub");
  mkdirSync(join(dir, "api", "game"), { recursive: true });
  writeFileSync(join(dir, "api", "game", "datasets.csv"), "Key,Name\nChampions,test\n");
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) };
}

async function withWrangler({ env, port, vars = [], dir, body }) {
  const config = readWranglerConfig();
  const target = env ? config.env[env] : config;
  check(`wrangler.jsonc ${env ?? "本番"} の not_found_handling は 404-page`, target.assets.not_found_handling === "404-page", String(target.assets.not_found_handling));
  const merged = { ...config, ...target, main: join(root, "worker", "index.js"), assets: { ...target.assets, directory: dir } };
  delete merged.env;
  delete merged.routes;
  const tmp = mkdtempSync(join(tmpdir(), "not-found-wrangler-"));
  const configPath = join(tmp, "wrangler.json");
  writeFileSync(configPath, JSON.stringify(merged));
  const args = ["wrangler", "dev", "--config", configPath, "--port", String(port), "--ip", "127.0.0.1", "--persist-to", join(tmp, "state"), "--show-interactive-dev-session=false"];
  for (const v of vars) args.push("--var", v);
  const wrangler = spawn(process.platform === "win32" ? "npx.cmd" : "npx", args, {
    cwd: root, stdio: ["ignore", "pipe", "pipe"], shell: process.platform === "win32", detached: process.platform !== "win32",
  });
  let output = "";
  wrangler.stdout.on("data", (d) => (output += d));
  wrangler.stderr.on("data", (d) => (output += d));
  const base = `http://127.0.0.1:${port}`;
  try {
    let ready = false;
    for (let i = 0; i < 120 && !ready; i++) {
      try { ready = (await fetch(`${base}/api/maintenance`)).status === 200; } catch {}
      if (!ready) await new Promise((r) => setTimeout(r, 500));
    }
    if (!ready) throw new Error("wrangler dev が起動しませんでした\n" + output);
    await body(base);
  } finally {
    // 自分が起動した wrangler だけを PID で止める
    if (process.platform === "win32") spawn("taskkill", ["/pid", String(wrangler.pid), "/T", "/F"], { stdio: "ignore" });
    else try { process.kill(-wrangler.pid, "SIGTERM"); } catch {}
    await new Promise((r) => setTimeout(r, 1000));
    rmSync(tmp, { recursive: true, force: true });
  }
}

const isHtml = (r) => (r.headers.get("content-type") ?? "").includes("text/html");
/** 404.html の本文か（noindex・404 用の title・読み込み表示）。 */
const isNotFoundPage = (text) => text.includes('<meta name="robots" content="noindex" />') && text.includes("ページが見つかりません") && text.includes('id="boot-indicator"');

/** 内部の入口ファイル home.html の別名（/home・/home.html・/home/）は、1 回の 301 で / になる（/ は 200 のホームのまま）。 */
async function checkHomeRedirect(base, label) {
  for (const path of ["/home", "/home.html", "/home/", "/home?x=1"]) {
    const r = await fetch(`${base}${path}`, { redirect: "manual" });
    const expected = path.includes("?") ? "/?x=1" : "/";
    check(`${label} ${path} は 301 で ${expected}`, r.status === 301 && r.headers.get("location") === expected, `${r.status} ${r.headers.get("location")}`);
    const head = await fetch(`${base}${path}`, { method: "HEAD", redirect: "manual" });
    check(`${label} HEAD ${path} も 301`, head.status === 301, String(head.status));
  }
  const followed = await fetch(`${base}/home`);
  const text = await followed.text();
  check(`${label} /home を辿った先は 200 のホーム（転送ループにならない）`, followed.status === 200 && followed.url === `${base}/` && isHtml(followed) && !isNotFoundPage(text), `${followed.status} ${followed.url}`);
  check(`${label} /homepage・/home/x・/Home は 404 のまま（別名は 3 つだけ）`, (await Promise.all(["/homepage", "/home/x", "/Home"].map((p) => fetch(`${base}${p}`)))).every((r) => r.status === 404));
}

async function checkNormal(base, label, dir) {
  const fromPages = discoverPageRoutes().filter((r) => r !== "");
  const fromFiles = readdirSync(dir).filter((f) => f.endsWith(".html") && !["index.html", "home.html", "404.html"].includes(f)).map((f) => f.slice(0, -5));
  const get = (path, init) => fetch(`${base}${path}`, { redirect: "manual", ...init });

  // 存在しない URL は 404 のステータス＋404.html（noindex・404 用の title・読み込み表示。起動後に Blazor が 404 画面を描く）
  for (const path of ["/abc", "/abc/def", "/no-such-page?x=1", "/%E3%81%82", "/speed-comparison/extra", "/no-such.json"]) {
    const r = await get(path);
    const text = await r.text();
    check(`${label} ${path} は 404 で本文は 404.html`, r.status === 404 && isHtml(r) && isNotFoundPage(text), `${r.status} ${r.headers.get("content-type")}`);
  }
  const missingFramework = await get("/_framework/no-such.js");
  check(`${label} 無い /_framework/... も 404`, missingFramework.status === 404, String(missingFramework.status));

  // 有効なルートは 200（クエリ付きも）。@page の全部に <route>.html があり、404.html の本文ではない
  const missingFiles = fromPages.filter((r) => !fromFiles.includes(r));
  check(`${label} @page の全ルート（${fromPages.length} 件）に <route>.html がある`, missingFiles.length === 0, missingFiles.join(", "));
  const routes = [...new Set([...fromPages, ...fromFiles])];
  const bad = [];
  for (const route of routes) {
    for (const path of [`/${route}`, `/${route}?a=1&b=2`]) {
      const r = await get(path);
      const text = await r.text();
      if (r.status !== 200 || !isHtml(r) || isNotFoundPage(text)) bad.push(`${path} → ${r.status}`);
    }
  }
  check(`${label} 全ての有効なルート（${routes.length} 件。クエリ付きも）が 200 の HTML`, bad.length === 0, bad.join(", "));
  const home = await get("/");
  check(`${label} / は 200（ホーム）`, home.status === 200 && isHtml(home) && !isNotFoundPage(await home.text()));
  await checkHomeRedirect(base, label);
  const head = await get(`/${routes[0]}`, { method: "HEAD" });
  check(`${label} HEAD も有効なルートは 200`, head.status === 200);
  check(`${label} HEAD /abc は 404`, (await get("/abc", { method: "HEAD" })).status === 404);

  // 大文字小文字・末尾スラッシュ・拡張子つき
  const upper = await get(`/${routes[0].toUpperCase()}`);
  console.log(`     （参考）大文字 /${routes[0].toUpperCase()} → ${upper.status}`);
  check(`${label} 大文字のパスは 404（正規の URL は小文字だけ。アプリ内の遷移は従来どおり）`, upper.status === 404, String(upper.status));
  const slash = await get(`/${routes[0]}/`);
  const slashLocation = slash.headers.get("location");
  console.log(`     （参考）末尾スラッシュ /${routes[0]}/ → ${slash.status} ${slashLocation ?? ""}`);
  const slashFinal = slash.status === 200 ? slash : await fetch(new URL(slashLocation ?? "/", base));
  check(`${label} 末尾スラッシュ付きの有効なルートは 200（または転送の先が 200）`, slashFinal.status === 200 && isHtml(slashFinal), `${slash.status} ${slashLocation}`);
  const ext = await get(`/${routes[0]}.html`);
  console.log(`     （参考）/${routes[0]}.html → ${ext.status} ${ext.headers.get("location") ?? ""}`);
  check(`${label} .html 付きは転送（重複した 200 の URL を作らない）`, [301, 302, 307, 308].includes(ext.status) && ext.headers.get("location") === `/${routes[0]}`, `${ext.status} ${ext.headers.get("location")}`);

  // 壊れていないもの
  check(`${label} /_framework/blazor.webassembly.js は 200`, (await get("/_framework/blazor.webassembly.js")).status === 200);
  check(`${label} /robots.txt は 200`, (await get("/robots.txt")).status === 200);
  check(`${label} /sitemap.xml は 200（本番の出力のとき）`, (await get("/sitemap.xml")).status === 200);
  const csv = await get("/api/game/datasets.csv");
  check(`${label} /api/game/datasets.csv は 200 で CSV`, csv.status === 200 && !isHtml(csv), `${csv.status} ${csv.headers.get("content-type")}`);
  const missingCsv = await get("/api/game/NoSuchGame/pokemon.csv");
  const missingText = await missingCsv.text();
  check(`${label} 無い /api/game/... は HTML でない 404`, missingCsv.status === 404 && !isHtml(missingCsv) && !/<html/i.test(missingText), `${missingCsv.status} ${missingCsv.headers.get("content-type")}`);
  const apiMissing = await get("/api/no-such");
  check(`${label} 無い /api/... は JSON の 404（Worker）`, apiMissing.status === 404 && !isHtml(apiMissing), `${apiMissing.status} ${apiMissing.headers.get("content-type")}`);
  const maintenance = await get("/api/maintenance");
  check(`${label} /api/maintenance は 200 の JSON`, maintenance.status === 200 && (await maintenance.json()).state !== undefined);
}

async function checkMaintenance(base, label) {
  const csv = await fetch(`${base}/api/game/datasets.csv`);
  check(`${label} MAINTENANCE_MODE=true: /api/game/* は 503`, csv.status === 503, String(csv.status));
  const api = await fetch(`${base}/api/my/pokemon`);
  check(`${label} MAINTENANCE_MODE=true: /api/my/* は 503`, api.status === 503, String(api.status));
  const state = await (await fetch(`${base}/api/maintenance`)).json();
  check(`${label} MAINTENANCE_MODE=true: /api/maintenance は state=on`, state.state === "on", JSON.stringify(state));
  // ページ本体は静的アセットのまま（メンテナンス画面はアプリが /api/maintenance を読んで出す）。存在しない URL は 404 のまま
  check(`${label} MAINTENANCE_MODE=true: 存在しない URL は 404 のまま`, (await fetch(`${base}/abc`)).status === 404);
  await checkHomeRedirect(base, "[本番] MAINTENANCE_MODE=true:");
  check(`${label} MAINTENANCE_MODE=true: / は 200 のまま`, (await fetch(`${base}/`)).status === 200);
  check(`${label} MAINTENANCE_MODE=true: 有効なルートは 200 のまま`, (await fetch(`${base}/speed-comparison`)).status === 200);
}

const assets = prepareAssets();
try {
  await withWrangler({ env: null, port: basePort, dir: assets.dir, body: (base) => checkNormal(base, "[本番]", assets.dir) });
  await withWrangler({ env: "develop", port: basePort + 1, dir: assets.dir, body: (base) => checkNormal(base, "[develop]", assets.dir) });
  await withWrangler({ env: null, port: basePort + 2, vars: ["MAINTENANCE_MODE:true"], dir: assets.dir, body: (base) => checkMaintenance(base, "[本番]") });
} finally {
  assets.cleanup();
}
console.log(failures === 0 ? "\n全て OK" : `\n${failures} 件失敗`);
process.exit(failures === 0 ? 0 : 1);
