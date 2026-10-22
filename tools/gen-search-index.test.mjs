// サイト内検索の索引の生成（tools/gen-search-index.mjs）の確認。
//   node --test tools/gen-search-index.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { pageDescription } from "./gen-search-index.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const index = JSON.parse(readFileSync(join(root, "src", "FaraPokemonBattleAssistant.Web", "wwwroot", "search-index.json"), "utf8"));

test("pageDescription は page-header に他のクラスが付いていても説明を取る", () => {
    const plainHeader = '<div class="page-header"><h3>見出し</h3><p class="text-muted mb-0">説明A</p></div>';
    const withClasses = '<div class="page-header page-column"><h3>見出し</h3><p class="text-muted mb-0">説明B</p></div>';
    assert.equal(pageDescription(plainHeader), "説明A");
    assert.equal(pageDescription(withClasses), "説明B");
});

test("pageDescription は page-header が無いページには空を返す", () => {
    assert.equal(pageDescription('<div class="pane-scroll"><p>本文</p></div>'), "");
    assert.equal(pageDescription('<div class="page-headerless"><p>本文</p></div>'), "");
});

test("問い合わせ・プライバシー・権利表記の説明が索引に入る", () => {
    for (const url of ["/report", "/privacy", "/copyright"]) {
        const page = index.find((e) => e.type === "page" && e.url === url);
        assert.ok(page, `${url} のページ項目が無い`);
        assert.notEqual(page.text, "", `${url} の説明が空`);
    }
});

test("--check は、索引が今のページと合っていれば成功する", () => {
    const out = execFileSync(process.execPath, [join(here, "gen-search-index.mjs"), "--check"], { encoding: "utf8" });
    assert.match(out, /最新です/);
});
