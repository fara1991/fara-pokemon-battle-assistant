// js/boot-indicator.js の動きを、最小限のスタブで確かめる。
// - Blazor の描画（.mud-layout）で、オーバーレイを外す（style だけでは外さない）。エラーの表示でも外す
// - 入口ページの本文（.entry-static）は、オーバーレイのあいだ inert と aria-hidden。外すときに戻す
// - 45 秒で「再読み込み」の案内（リンク付き）に変わる / blazor.webassembly.js の読み込み失敗でも案内
// - 60 秒で、描画されていなくても外す（CSS の boot-indicator-release と同じ秒数）
// 時間は注入（フェイクのタイマー。advance で進める）。実行: node --test tests/js/boot-indicator.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = join(here, "../../src/FaraPokemonBattleAssistant.Web/wwwroot");
const source = readFileSync(join(webRoot, "js/boot-indicator.js"), "utf8");
const indexHtml = readFileSync(join(webRoot, "index.html"), "utf8");
const SLOW_MS = 45000;
const RELEASE_MS = 60000;
const SLOW_TEXT = "読み込みに時間がかかっています。ページを再読み込みしてください";

/** 要素の見本（class と属性） */
function element(classes = [], attrs = {}) {
    return {
        classList: { contains: (c) => classes.includes(c) },
        attrs: { ...attrs },
        setAttribute(k, v) { this.attrs[k] = String(v); },
        getAttribute(k) { return this.attrs[k] ?? null; },
        removeAttribute(k) { delete this.attrs[k]; },
    };
}

/** ラベル（textContent の設定で中身を置き換え、appendChild で足す） */
function makeLabel() {
    const label = {
        children: [],
        attrs: {},
        setAttribute(k, v) { this.attrs[k] = String(v); },
        getAttribute(k) { return this.attrs[k] ?? null; },
        appendChild(c) { this.children.push(c); return c; },
        set textContent(v) { this.children = v ? [{ text: v }] : []; },
        get textContent() { return this.children.map((c) => c.text ?? c.textContent ?? "").join(""); },
    };
    return label;
}

/** 画面を組み立てる。mudLayout: #app の中に MudBlazor の画面（.mud-layout）があるか */
function makeEnv({ staticMain = true, rendered = false, withObserver = true } = {}) {
    const observers = [];
    class MutationObserver {
        constructor(callback) { this.callback = callback; this.active = true; observers.push(this); }
        observe(target, options) { this.target = target; this.options = options; }
        disconnect() { this.active = false; }
    }
    const label = makeLabel();
    const indicator = {
        removed: false,
        parentNode: { removeChild: (node) => { node.removed = true; } },
        attrs: {},
        setAttribute(k, v) { this.attrs[k] = String(v); },
        getAttribute(k) { return this.attrs[k] ?? null; },
        querySelector: (sel) => (sel === ".boot-indicator-label" ? label : null),
    };
    const staticEl = element(["entry-static"]);
    const app = {
        children: [],
        querySelector: (sel) => (sel === ".mud-layout" && app.children.some((c) => c.classList.contains("mud-layout")) ? {} : null),
    };
    if (rendered) app.children.push(element(["mud-layout"]));
    else if (staticMain) app.children.push(staticEl);
    const errorUi = { display: "none" };
    const ids = { "boot-indicator": indicator, app, "blazor-error-ui": errorUi };
    const document = {
        getElementById: (id) => ids[id] ?? null,
        createElement: (tag) => ({ tagName: tag.toUpperCase(), attrs: {}, textContent: "", setAttribute(k, v) { this.attrs[k] = String(v); } }),
        createTextNode: (text) => ({ text }),
    };

    // フェイクのタイマー
    const timers = [];
    let now = 0;
    const setTimeout = (fn, ms) => { const t = { fn, due: now + ms, active: true }; timers.push(t); return t; };
    const clearTimeout = (t) => { if (t) t.active = false; };

    // フェイクの window（error の capture を覚える）
    const windowListeners = { error: [] };
    const window = {
        getComputedStyle: (el) => ({ display: el.display }),
        addEventListener(type, fn, capture) { if (type === "error") windowListeners.error.push({ fn, capture }); },
        removeEventListener(type, fn) { if (type === "error") windowListeners.error = windowListeners.error.filter((l) => l.fn !== fn); },
    };
    if (withObserver) window.MutationObserver = MutationObserver;
    vm.runInNewContext(source, { document, window, MutationObserver, setTimeout, clearTimeout });

    /** DOM の変化を通知する（監視中の observer を呼ぶ） */
    const trigger = (target) => {
        for (const o of observers) if (o.active && o.target === target) o.callback([]);
    };
    /** 経過させる。期限が来た有効なタイマーを、期限の順に実行する */
    const advance = (ms) => {
        now += ms;
        const due = timers.filter((t) => t.active && t.due <= now).sort((a, b) => a.due - b.due);
        for (const t of due) { if (t.active) { t.active = false; t.fn(); } }
    };
    /** error（capture）を window に届ける（blazor.webassembly.js の読み込み失敗など） */
    const resourceError = (src, tagName = "SCRIPT") => {
        for (const l of [...windowListeners.error]) if (l.capture) l.fn({ target: { tagName, src } });
    };
    return { indicator, label, app, staticEl, errorUi, trigger, advance, resourceError, observers, timers, windowListeners };
}

const layout = () => element(["mud-layout"]);

test("入口ページの本文（.entry-static）だけのあいだは、オーバーレイを残す", () => {
    const { indicator, app, trigger } = makeEnv();
    trigger(app);
    assert.equal(indicator.removed, false);
});

test("MudBlazor の style だけが増えた時点では外さず、.mud-layout（画面の骨組み）が出たら外す", () => {
    const { indicator, app, trigger } = makeEnv();
    app.children.push(element(["mud-theme-provider"]));
    trigger(app);
    assert.equal(indicator.removed, false, "骨組みが無いあいだは外さない（空白の一瞬を出さない）");
    app.children.push(layout());
    trigger(app);
    assert.equal(indicator.removed, true);
});

test("描画で外すとき、監視・タイマーを止め、本文の inert と aria-hidden を戻す", () => {
    const { indicator, app, staticEl, trigger, observers, timers } = makeEnv();
    assert.equal(staticEl.getAttribute("inert"), "", "オーバーレイのあいだは本文を inert にする");
    assert.equal(staticEl.getAttribute("aria-hidden"), "true");
    app.children.push(layout());
    trigger(app);
    assert.equal(indicator.removed, true);
    assert.ok(observers.every((o) => !o.active), "外したあとは監視しない");
    assert.ok(timers.every((t) => !t.active), "外したあとはタイマーも止める");
    assert.equal(staticEl.getAttribute("inert"), null, "本文は操作できる（外したあとは inert を残さない）");
    assert.equal(staticEl.getAttribute("aria-hidden"), null);
});

test("本文が無いフォールバック（#app が空）でも、描画で外す", () => {
    const { indicator, app, trigger } = makeEnv({ staticMain: false });
    trigger(app);
    assert.equal(indicator.removed, false);
    app.children.push(layout());
    trigger(app);
    assert.equal(indicator.removed, true);
});

test("起動に失敗してエラーの表示（#blazor-error-ui）が出たら、外す", () => {
    const { indicator, errorUi, trigger, staticEl } = makeEnv();
    errorUi.display = "block";
    trigger(errorUi);
    assert.equal(indicator.removed, true);
    assert.equal(staticEl.getAttribute("inert"), null);
});

test("読み込み時点で画面の骨組みが既に有れば、すぐ外す", () => {
    const { indicator } = makeEnv({ rendered: true });
    assert.equal(indicator.removed, true);
});

test("45 秒たっても描かれないと、「再読み込み」のリンク付きの案内に変わる（スピナーは CSS で止める。本文は inert のまま）", () => {
    const { indicator, label, advance, timers, staticEl } = makeEnv();
    assert.equal(timers[0].due, SLOW_MS);
    assert.equal(timers[1].due, RELEASE_MS);
    advance(SLOW_MS - 1);
    assert.equal(indicator.getAttribute("data-state"), null, "45 秒の前はまだ変えない");
    advance(1);
    assert.equal(indicator.getAttribute("data-state"), "slow");
    assert.equal(label.textContent, SLOW_TEXT + " 再読み込み");
    const link = label.children[1];
    assert.equal(link.tagName, "A");
    assert.equal(link.attrs.href, "", "再読み込み（現在のページ）");
    assert.equal(link.textContent, "再読み込み");
    assert.equal(label.getAttribute("aria-label"), SLOW_TEXT + " 再読み込み");
    assert.equal(indicator.removed, false, "案内のあいだはオーバーレイを残す");
    assert.equal(staticEl.getAttribute("inert"), "", "案内のあいだも本文は操作させない（再読み込みだけ押せる）");
});

test("60 秒たったら、描画されていなくても外し、本文の inert を戻す（案内のあとでも）", () => {
    const { indicator, advance, staticEl, timers } = makeEnv();
    advance(SLOW_MS);
    assert.equal(indicator.getAttribute("data-state"), "slow");
    advance(RELEASE_MS - SLOW_MS - 1);
    assert.equal(indicator.removed, false);
    advance(1);
    assert.equal(indicator.removed, true);
    assert.equal(staticEl.getAttribute("inert"), null);
    assert.ok(timers.every((t) => !t.active));
});

test("描画されたあとは、案内・解除のタイマーは動かない（ラベルも変わらない）", () => {
    const { indicator, app, label, advance, trigger } = makeEnv();
    app.children.push(layout());
    trigger(app);
    advance(RELEASE_MS + SLOW_MS);
    assert.equal(indicator.getAttribute("data-state"), null);
    assert.equal(label.textContent, "");
});

test("blazor.webassembly.js の読み込みに失敗したら、すぐ案内に変わる（ほかの資源の失敗では変わらない）", () => {
    const { indicator, resourceError } = makeEnv();
    resourceError("https://pokemon.fara-labs.com/css/app.css");
    assert.equal(indicator.getAttribute("data-state"), null, "ほかの資源は無視する");
    resourceError("https://pokemon.fara-labs.com/_framework/blazor.webassembly.js");
    assert.equal(indicator.getAttribute("data-state"), "slow");
});

test("blazor.webassembly.js の読み込み失敗のあとに描画されたら、外し、error の監視も外す", () => {
    const { indicator, app, resourceError, trigger, windowListeners } = makeEnv();
    resourceError("/_framework/blazor.webassembly.js");
    app.children.push(layout());
    trigger(app);
    assert.equal(indicator.removed, true);
    assert.equal(windowListeners.error.length, 0);
});

test("MutationObserver が無いブラウザでは何もしない（CSS の 60 秒で外れる。本文は inert にしない）", () => {
    const { indicator, staticEl, timers } = makeEnv({ withObserver: false });
    assert.equal(indicator.removed, false);
    assert.equal(staticEl.getAttribute("inert"), null);
    assert.equal(timers.length, 0);
});

test("CSS の保険（boot-indicator-release）は、JS と同じ 60 秒で隠れる規則になっている", () => {
    assert.match(indexHtml, /animation: boot-indicator-release 0s linear 60s forwards;/);
    assert.match(indexHtml, /@keyframes boot-indicator-release \{ to \{ visibility: hidden; \} \}/);
    assert.match(source, /var RELEASE_MS = 60000;/);
    assert.match(source, /var SLOW_MS = 45000;/);
    // 動きを減らす設定は、スピナーだけを止める（60 秒の保険は止めない）
    const reduced = indexHtml.slice(indexHtml.indexOf("@media (prefers-reduced-motion: reduce)"));
    assert.doesNotMatch(reduced.slice(0, reduced.indexOf("</style>")), /boot-indicator-release|#boot-indicator \{/);
});

test("テンプレート（index.html）には inert を直接書かない（JS が外す・戻す）", () => {
    assert.doesNotMatch(indexHtml, /inert/);
});
