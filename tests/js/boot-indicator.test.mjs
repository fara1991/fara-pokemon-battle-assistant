// js/boot-indicator.js の動き（Blazor が #app の中身を描いたら読み込み表示を外す / 入口ページの本文だけのあいだは残す / エラーで外す）を、最小限のスタブで確かめる。
// 実行: node --test tests/js/boot-indicator.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "../../src/FaraPokemonBattleAssistant.Web/wwwroot/js/boot-indicator.js"), "utf8");

function element(classes = []) {
    return { classList: { contains: (c) => classes.includes(c) } };
}

/** 画面を組み立てる。observers は MutationObserver の登録（trigger で呼び直す） */
function makeEnv({ staticMain = true, rendered = false } = {}) {
    const observers = [];
    class MutationObserver {
        constructor(callback) { this.callback = callback; this.active = true; observers.push(this); }
        observe(target, options) { this.target = target; this.options = options; }
        disconnect() { this.active = false; }
    }
    const indicator = { removed: false, parentNode: null };
    indicator.parentNode = { removeChild: (node) => { node.removed = true; } };
    const app = { children: rendered ? [element(["mud-layout"])] : staticMain ? [element(["entry-static"])] : [] };
    const errorUi = { display: "none" };
    const ids = { "boot-indicator": indicator, app, "blazor-error-ui": errorUi };
    const document = { getElementById: (id) => ids[id] ?? null };
    const window = { MutationObserver, getComputedStyle: (el) => ({ display: el.display }) };
    vm.runInNewContext(source, { document, window, MutationObserver });
    // 監視中の observer を、対象ごとに呼び直す（ブラウザが DOM の変化を通知するのに当たる）
    const trigger = (target) => {
        for (const o of observers) if (o.active && o.target === target) o.callback([]);
    };
    return { indicator, app, errorUi, trigger, observers };
}

test("入口ページの本文（.entry-static）だけのあいだは、読み込み表示を残す", () => {
    const { indicator, trigger, app } = makeEnv();
    trigger(app);
    assert.equal(indicator.removed, false);
});

test("Blazor が #app の中身を描いたら、読み込み表示を外し、監視も止める", () => {
    const { indicator, app, trigger, observers } = makeEnv();
    app.children = [element(["mud-layout"])];
    trigger(app);
    assert.equal(indicator.removed, true);
    assert.ok(observers.every((o) => !o.active), "外したあとは監視しない");
});

test("本文が無いフォールバック（#app が空）でも、Blazor が描いたら外す", () => {
    const { indicator, app, trigger } = makeEnv({ staticMain: false });
    trigger(app);
    assert.equal(indicator.removed, false);
    app.children = [element(["mud-layout"])];
    trigger(app);
    assert.equal(indicator.removed, true);
});

test("起動に失敗してエラーの表示（#blazor-error-ui）が出たら、読み込み表示を外す", () => {
    const { indicator, errorUi, trigger } = makeEnv();
    errorUi.display = "block";
    trigger(errorUi);
    assert.equal(indicator.removed, true);
});

test("#app に Blazor の描いた画面がもう有るときに読まれたら、読み込んだ時点ですぐ外す", () => {
    const { indicator } = makeEnv({ staticMain: false, rendered: true });
    assert.equal(indicator.removed, true);
});
