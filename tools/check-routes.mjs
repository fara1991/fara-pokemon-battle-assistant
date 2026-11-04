// 公開物（dotnet publish + build-entry-pages の出力）に、有効なルートの静的 HTML が全部あるかを確かめる。
//   node tools/check-routes.mjs [--dir publish/wwwroot]
// wrangler.jsonc は not_found_handling: "404-page" なので、HTML が無い URL は 404 のステータスで返る。
// 有効なルート（Blazor の @page）に HTML が無いと、そのページが 404 になるので、公開の前にここで止める。
import { existsSync, readFileSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { discoverPageRoutes } from "./build-entry-pages.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

/** 公開物の dir を調べて、問題の文を配列で返す（空なら問題なし）。 */
export function checkRoutes(dir, routes = discoverPageRoutes()) {
    const problems = [];
    for (const route of routes) {
        const file = route === "" ? "home.html" : `${route}.html`;
        if (!existsSync(join(dir, file))) problems.push(`/${route} の ${file} がありません（このページが 404 になります）`);
    }
    for (const file of ["index.html", "404.html"]) {
        if (!existsSync(join(dir, file))) problems.push(`${file} がありません`);
    }
    const notFound = join(dir, "404.html");
    if (existsSync(notFound) && !/<meta name="robots" content="noindex" \/>/.test(readFileSync(notFound, "utf8"))) {
        problems.push("404.html に noindex がありません");
    }
    return problems;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const i = process.argv.indexOf("--dir");
    const dir = i >= 0 ? resolve(process.argv[i + 1]) : join(root, "publish", "wwwroot");
    const problems = checkRoutes(dir);
    if (problems.length > 0) {
        console.error(problems.join("\n"));
        process.exitCode = 1;
    } else {
        console.log(`有効なルート（${discoverPageRoutes().length} 件）の静的 HTML と 404.html があります: ${dir}`);
    }
}
