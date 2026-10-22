// js/scroll-progress.js の動き（どの要素を測るか・0〜1 の値・対戦画面での 0）を、最小限のスタブで確かめる。
// 実行: node --test tests/js/scroll-progress.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "../../src/FaraPokemonBattleAssistant.Web/wwwroot/js/scroll-progress.js"), "utf8");

// 画面の作り（PC: .content が動く / スマホ: window が動く / 対戦画面）を、数値だけで組み立てる
function makeEnv({ content = null, battle = false, windowScroll = null } = {}) {
    if (content) content.matches = (sel) => sel === "main > article.content";
    const styles = {};
    const root = { style: { setProperty: (k, v) => { styles[k] = v; } } };
    const listeners = {};
    const docListeners = {};
    const doc = {
        documentElement: root,
        scrollingElement: windowScroll ? { ...windowScroll } : { scrollTop: 0, scrollHeight: 0, clientHeight: 800 },
        readyState: "complete",
        hidden: false,
        getElementById: (id) => (id === "app" ? { id: "app" } : null),
        querySelector: (sel) => {
            if (sel === "main > article.content") return content;
            if (sel === ".page.battle-active") return battle ? {} : null;
            return null;
        },
        addEventListener: (type, fn) => { (docListeners[type] ||= []).push(fn); },
        body: {},
    };
    const win = {
        addEventListener: (type, fn) => { (listeners[type] ||= []).push(fn); },
    };
    let frames = [];
    const context = {
        document: doc,
        window: win,
        getComputedStyle: (el) => ({ overflowY: el.overflowY ?? "visible" }),
        requestAnimationFrame: (fn) => { frames.push(fn); return frames.length; },
        MutationObserver: class { constructor(fn) { this.fn = fn; } observe() { } },
    };
    vm.createContext(context);
    vm.runInContext(source, context);
    return {
        styles,
        doc,
        // 1 フレーム分を進める（rAF で積まれた測り直しを実行する）
        flush() { const f = frames; frames = []; f.forEach((fn) => fn(0)); },
        scroll(target) { (docListeners.scroll || []).forEach((fn) => fn({ target })); },
        resize() { (listeners.resize || []).forEach((fn) => fn({})); },
    };
}

test("PC: .content が動いているときは、その中のスクロール量で決まる", () => {
    const content = { overflowY: "auto", scrollTop: 0, scrollHeight: 2000, clientHeight: 1000 };
    const env = makeEnv({ content });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");

    content.scrollTop = 500;
    env.scroll(content);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");

    content.scrollTop = 1000;
    env.scroll(content);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "1.0000");
});

test("スマホ: .content は overflow が visible なので、window のスクロールで決まる", () => {
    const content = { overflowY: "visible", scrollTop: 0, scrollHeight: 900, clientHeight: 900 };
    const windowScroll = { scrollTop: 300, scrollHeight: 1300, clientHeight: 700 };
    const env = makeEnv({ content, windowScroll });
    env.scroll(env.doc);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("スクロールできない画面は 0（割り算で壊れない）", () => {
    const content = { overflowY: "auto", scrollTop: 0, scrollHeight: 500, clientHeight: 500 };
    const env = makeEnv({ content });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");
});

test("対戦画面（.page.battle-active）は、中身が動いていても 0", () => {
    const content = { overflowY: "auto", scrollTop: 800, scrollHeight: 2000, clientHeight: 1000 };
    const env = makeEnv({ content, battle: true });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");
});

test("一覧の中（.content 以外）のスクロールは数えない", () => {
    const content = { overflowY: "auto", scrollTop: 0, scrollHeight: 2000, clientHeight: 1000 };
    const env = makeEnv({ content });
    env.flush();
    content.scrollTop = 1000;
    env.scroll({ matches: () => false });   // .pane-scroll など
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");
});

test("画面の大きさが変わったら、測り直す", () => {
    const content = { overflowY: "auto", scrollTop: 500, scrollHeight: 2000, clientHeight: 1000 };
    const env = makeEnv({ content });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
    content.clientHeight = 500;              // 窓を縮めると、残りの量が増える
    env.resize();
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.3333");
});
