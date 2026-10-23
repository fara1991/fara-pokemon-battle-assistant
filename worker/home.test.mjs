// トップページの配信（worker/home.js）と、wrangler.jsonc の run_worker_first の確認。
//   node --test worker/home.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { serveHome } from "./home.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

/** wrangler.jsonc（コメント付き）を読む。行頭の // のコメントだけを外し、末尾のカンマを許す。 */
function readWranglerConfig() {
    const text = readFileSync(join(root, "wrangler.jsonc"), "utf8")
        .split("\n")
        .filter((line) => !line.trim().startsWith("//"))
        .join("\n")
        .replace(/,(\s*[}\]])/g, "$1");
    return JSON.parse(text);
}

test("/ は静的アセットの /home（home.html）に差し替えて返す。メソッドとヘッダーはそのまま", async () => {
    const calls = [];
    const env = {
        ASSETS: {
            fetch: async (request) => {
                calls.push(request);
                return new Response("<html>home</html>", { status: 200, headers: { "Content-Type": "text/html" } });
            },
        },
    };
    const request = new Request("https://pokemon.fara-labs.com/?x=1", { method: "HEAD", headers: { "If-None-Match": "abc" } });
    const response = await serveHome(request, env);

    assert.equal(response.status, 200);
    assert.equal(calls.length, 1);
    const asked = new URL(calls[0].url);
    assert.equal(asked.pathname, "/home");              // /home.html は /home へ転送されるので、拡張子なしで取る
    assert.equal(asked.host, "pokemon.fara-labs.com");
    assert.equal(calls[0].method, "HEAD");
    assert.equal(calls[0].headers.get("If-None-Match"), "abc");
});

test("Worker を通るのは / と /api/* だけ（それ以外は静的アセット。存在しない URL は 404.html の 404）", () => {
    const config = readWranglerConfig();
    for (const env of [config.assets, config.env.develop.assets]) {
        assert.deepEqual([...env.run_worker_first].sort(), ["/", "/api/*"]);
    }
});

test("ASSETS の応答のヘッダー（_headers の CSP・X-Robots-Tag など）を落とさず、そのまま返す", async () => {
    const assetHeaders = { "Content-Type": "text/html; charset=utf-8", "Content-Security-Policy": "default-src self", "X-Robots-Tag": "noindex, nofollow" };
    const assetResponse = new Response("<html>home</html>", { status: 200, headers: assetHeaders });
    const env = { ASSETS: { fetch: async () => assetResponse } };
    const response = await serveHome(new Request("https://pokemon-dev.fara-labs.com/"), env);
    assert.equal(response, assetResponse);                       // 組み直さず、そのまま返す
    assert.equal(response.headers.get("Content-Security-Policy"), "default-src self");
    assert.equal(response.headers.get("X-Robots-Tag"), "noindex, nofollow");
    assert.equal(response.headers.get("Content-Type"), "text/html; charset=utf-8");
});

test("本番と確認用の両方で、/ が Worker を通る（wrangler.jsonc）", () => {
    const config = readWranglerConfig();
    assert.ok(config.assets.run_worker_first.includes("/"));
    assert.ok(config.env.develop.assets.run_worker_first.includes("/"));
});

test("index.js は / のとき serveHome を返す（/api/* の処理より前）", () => {
    const source = readFileSync(join(root, "worker", "index.js"), "utf8");
    assert.match(source, /import \{ serveHome \} from "\.\/home\.js";/);
    const branch = source.indexOf('if (url.pathname === "/") return serveHome(request, env);');
    assert.ok(branch > 0);
    assert.ok(branch < source.indexOf("maintenance.state"));
});
