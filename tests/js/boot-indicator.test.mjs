// js/boot-indicator.js の動きを、最小限のスタブで確かめる。
// - Blazor が #app の中身を描いたら読み込み表示を外す / 入口ページの本文だけのあいだは残す / エラーの表示で外す
// - 45 秒たっても描かれない、または blazor.webassembly.js の読み込みに失敗したら、再読み込みの案内に変える（描画されたら外す）
// 時間は注入（フェイクのタイマー。advance で進める）。
// 実行: node --test tests/js/boot-indicator.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "../../src/FaraPokemonBattleAssistant.Web/wwwroot/js/boot-indicator.js"), "utf8");
const SLOW_MS = 45000;
const SLOW_TEXT = "読み込みに時間がかかっています。ページを再読み込みしてください";

function element(classes = []) {
    return { classList: { contains: (c) => classes.includes(c) } };
}

/** 画面を組み立てる。timers は時間の注入（advance で経過させる）。windowListeners は window の error（capture）。 */
function makeEnv({ staticMain = true, rendered = false } = {}) {
    const observers = [];
    class MutationObserver {
        constructor(callback) { this.callback = callback; this.active = true; observers.push(this); }
        observe(target, options) { this.target = target; this.options = options; }
        disconnect() { this.active = false; }
    }
    const attrs = {};
    const label = { textContent: "", attrs: {}, setAttribute(k, v) { this.attrs[k] = v; }, getAttribute(k) { return this.attrs[k] ?? null; } };
    const indicator = {
        removed: false,
        parentNode: { removeChild: (node) => { node.removed = true; } },
        setAttribute(k, v) { attrs[k] = v; },
        getAttribute(k) { return attrs[k] ?? null; },
        querySelector: (sel) => (sel === ".boot-indicator-label" ? label : null),
    };
    const app = { children: rendered ? [element(["mud-layout"])] : staticMain ? [element(["entry-static"])] : [] };
    const errorUi = { display: "none" };
    const ids = { "boot-indicator": indicator, app, "blazor-error-ui": errorUi };
    const document = { getElementById: (id) => ids[id] ?? null };

    // フェイクのタイマー
    const timers = [];
    let now = 0;
    const setTimeout = (fn, ms) => { const t = { fn, due: now + ms, active: true }; timers.push(t); return t; };
    const clearTimeout = (t) => { if (t) t.active = false; };

    // フェイクの window（addEventListener の capture で登録された error を覚える）
    const windowListeners = { error: [] };
    const window = {
        MutationObserver,
        getComputedStyle: (el) => ({ display: el.display }),
        addEventListener(type, fn, capture) { if (type === "error") windowListeners.error.push({ fn, capture }); },
        removeEventListener(type, fn) { if (type === "error") windowListeners.error = windowListeners.error.filter((l) => l.fn !== fn); },
    };
    vm.runInNewContext(source, { document, window, MutationObserver, setTimeout, clearTimeout });

    /** DOM の変化を通知する（監視中の observer を呼ぶ） */
    const trigger = (target) => {
        for (const o of observers) if (o.active && o.target === target) o.callback([]);
    };
    /** 経過させる。期限が来た有効なタイマーを実行する */
    const advance = (ms) => {
        now += ms;
        for (const t of timers) if (t.active && t.due <= now) { t.active = false; t.fn(); }
    };
    /** blazor.webassembly.js の読み込み失敗（capture の error を、window に登録されたものへ渡す） */
    const resourceError = (src, tagName = "SCRIPT") => {
        for (const l of windowListeners.error) if (l.capture) l.fn({ target: { tagName, src } });
    };
    return { indicator, label, app, errorUi, trigger, advance, resourceError, observers, timers, windowListeners };
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

test("45 秒たっても描かれないと、再読み込みの案内に変わる（文字は JS で入れ、スピナーは止まる）", () => {
    const { indicator, label, advance, timers } = makeEnv();
    assert.equal(timers.length, 1);
    assert.equal(timers[0].due, SLOW_MS, "45 秒で切り替える");
    advance(SLOW_MS - 1);
    assert.equal(indicator.getAttribute("data-state"), null, "45 秒の前はまだ変えない");
    advance(1);
    assert.equal(indicator.getAttribute("data-state"), "slow");
    assert.equal(label.textContent, SLOW_TEXT);
    assert.equal(label.getAttribute("aria-label"), SLOW_TEXT, "読み上げも案内に変える");
    assert.equal(indicator.removed, false, "描かれていなければ、残したまま案内を出す");
});

test("案内に変わったあと Blazor が描いたら、通常どおり外す", () => {
    const { indicator, app, advance, trigger } = makeEnv();
    advance(SLOW_MS);
    app.children = [element(["mud-layout"])];
    trigger(app);
    assert.equal(indicator.removed, true);
});

test("45 秒の前に描かれたら、タイマーも止める（後から案内に変わらない）", () => {
    const { indicator, app, advance, trigger, timers, label } = makeEnv();
    app.children = [element(["mud-layout"])];
    trigger(app);
    assert.equal(indicator.removed, true);
    assert.equal(timers[0].active, false, "タイマーを止めている");
    advance(SLOW_MS);
    assert.equal(label.textContent, "", "外したあとは文字も変えない");
});

test("blazor.webassembly.js の読み込みに失敗したら、すぐ再読み込みの案内に変わる（ほかの資源の失敗では変わらない）", () => {
    const { indicator, label, resourceError } = makeEnv();
    resourceError("https://pokemon.fara-labs.com/css/app.css");
    assert.equal(indicator.getAttribute("data-state"), null, "ほかの資源は無視する");
    resourceError("https://pokemon.fara-labs.com/_framework/blazor.webassembly.js");
    assert.equal(indicator.getAttribute("data-state"), "slow");
    assert.equal(label.textContent, SLOW_TEXT);
});

test("blazor.webassembly.js の読み込み失敗のあとに Blazor が描いたら、外す", () => {
    const { indicator, app, resourceError, trigger } = makeEnv();
    resourceError("/_framework/blazor.webassembly.js");
    app.children = [element(["mud-layout"])];
    trigger(app);
    assert.equal(indicator.removed, true);
});

test("外したあとは window の error の監視も外す", () => {
    const { indicator, app, trigger, windowListeners } = makeEnv();
    app.children = [element(["mud-layout"])];
    trigger(app);
    assert.equal(indicator.removed, true);
    assert.equal(windowListeners.error.length, 0);
});
