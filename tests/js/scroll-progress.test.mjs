// js/scroll-progress.js の動き（どの要素を測るか・0〜1 の値・対戦画面での 0・画面の切り替え）を、最小限のスタブで確かめる。
// 実行: node --test tests/js/scroll-progress.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "../../src/FaraPokemonBattleAssistant.Web/wwwroot/js/scroll-progress.js"), "utf8");
const MAIN = "main > article.content";
const PANE = MAIN + " .pane-scroll";

// 要素の見本。sel は、その要素が一致するセレクタ。children は本文の中の要素
function node({ sel = [], scrollTop = 0, scrollHeight, clientHeight, children = [] }) {
    const self = {
        scrollTop, scrollHeight, clientHeight, children, isConnected: true,
        matches: (s) => s.split(", ").some((part) => sel.includes(part)),
        contains: (x) => x === self || children.some((c) => c.contains(x)),
    };
    return self;
}

// 画面（PC: 本文の中の .content か .pane-scroll が動く / スマホ: window が動く / 対戦画面）を組み立てる
function makeEnv({ main = null, windowPage = null, battle = false } = {}) {
    const styles = {};
    let writes = 0;
    const bar = {
        isConnected: true,
        style: { setProperty: (k, v) => { styles[k] = v; writes += 1; } },
    };
    const listeners = {}, docListeners = {};
    let mutationCb = null;
    const doc = {
        documentElement: {},
        scrollingElement: windowPage || { scrollTop: 0, scrollHeight: 0, clientHeight: 800 },
        readyState: "complete",
        hidden: false,
        getElementById: (id) => (id === "app" ? { id: "app" } : null),
        querySelector: (sel) => {
            if (sel === ".scroll-progress") return bar;
            if (sel === MAIN) return main;
            if (sel === ".page.battle-active") return battle ? {} : null;
            return null;
        },
        // 本文の中の候補（.content 自身と .pane-scroll）。画面の作り直し後は、いま本文にあるものだけ
        querySelectorAll: () => {
            const list = [];
            const walk = (n) => n.children.forEach((c) => { if (c.matches(PANE) || c.matches(MAIN)) list.push(c); walk(c); });
            if (main) { list.push(main); walk(main); }
            return list.filter((el) => el.isConnected);
        },
        addEventListener: (type, fn) => { (docListeners[type] ||= []).push(fn); },
        body: {},
    };
    const win = { addEventListener: (type, fn) => { (listeners[type] ||= []).push(fn); } };
    let frames = [];
    const context = {
        document: doc,
        window: win,
        requestAnimationFrame: (fn) => { frames.push(fn); return frames.length; },
        MutationObserver: class { constructor(fn) { mutationCb = fn; } observe() { } },
    };
    vm.createContext(context);
    vm.runInContext(source, context);
    return {
        styles,
        doc,
        writes: () => writes,
        flush() { const f = frames; frames = []; f.forEach((fn) => fn(0)); },
        // target: 本文の中の要素、または document（window のスクロール）
        scroll(target) { (docListeners.scroll || []).forEach((fn) => fn({ target })); },
        resize() { (listeners.resize || []).forEach((fn) => fn({})); },
        mutate() { mutationCb && mutationCb([]); },
    };
}

test("PC: .content 自体が動いているときは、その中のスクロール量で決まる", () => {
    const main = node({ sel: [MAIN], scrollHeight: 2000, clientHeight: 1000 });
    const env = makeEnv({ main });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");

    main.scrollTop = 500;
    env.scroll(main);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");

    main.scrollTop = 1000;
    env.scroll(main);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "1.0000");
});

test("PC: 本文の中の大きな .pane-scroll が動くページ（Home・一覧など）でも、その動きで線が伸びる", () => {
    const pane = node({ sel: [PANE], scrollHeight: 3000, clientHeight: 800 });
    const main = node({ sel: [MAIN], scrollHeight: 800, clientHeight: 800, children: [pane] });
    const env = makeEnv({ main });
    env.flush();
    pane.scrollTop = 1100;            // 3000 - 800 = 2200 の半分
    env.scroll(pane);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("PC: 外側の一覧の中の入れ子の小さな一覧のスクロールは数えない（外側が動くなら外側を測る）", () => {
    const inner = node({ sel: [PANE], scrollHeight: 900, clientHeight: 200 });
    const outer = node({ sel: [PANE], scrollHeight: 3000, clientHeight: 800, children: [inner] });
    const main = node({ sel: [MAIN], scrollHeight: 800, clientHeight: 800, children: [outer] });
    const env = makeEnv({ main });
    env.flush();
    inner.scrollTop = 700;
    env.scroll(inner);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");
    outer.scrollTop = 1100;
    env.scroll(outer);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("PC: 内側の一覧しか動かないときは、その一覧を測る", () => {
    const inner = node({ sel: [PANE], scrollHeight: 900, clientHeight: 200 });
    const main = node({ sel: [MAIN], scrollHeight: 800, clientHeight: 800, children: [inner] });
    const env = makeEnv({ main });
    env.flush();
    inner.scrollTop = 350;           // 900 - 200 = 700 の半分
    env.scroll(inner);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("PC: 低い画面で一覧が小さく見えても、外側の一覧を測る", () => {
    const pane = node({ sel: [PANE], scrollHeight: 18000, clientHeight: 46 });
    const main = node({ sel: [MAIN], scrollHeight: 389, clientHeight: 389, children: [pane] });
    const env = makeEnv({ main });
    env.flush();
    pane.scrollTop = 8977;           // 18000 - 46 = 17954 の半分
    env.scroll(pane);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("PC: 候補が複数あるとき、一番背の高い動かせる要素を測る", () => {
    const tall = node({ sel: [PANE], scrollHeight: 2000, clientHeight: 700 });
    const other = node({ sel: [PANE], scrollHeight: 1000, clientHeight: 300 });
    const main = node({ sel: [MAIN], scrollHeight: 800, clientHeight: 800, children: [other, tall] });
    const env = makeEnv({ main });
    env.flush();
    tall.scrollTop = 650;            // 2000 - 700 = 1300 の半分
    env.scroll(tall);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("画面が替わったら、測る要素を選び直す（前のページの要素は使わない）", () => {
    const oldPane = node({ sel: [PANE], scrollHeight: 2000, clientHeight: 800 });
    const main = node({ sel: [MAIN], scrollHeight: 800, clientHeight: 800, children: [oldPane] });
    const env = makeEnv({ main });
    env.flush();
    oldPane.scrollTop = 600;
    env.scroll(oldPane);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");

    // 別のページに替わる: 前の要素は外れ、新しい要素（今は途中）を測る
    oldPane.isConnected = false;
    const newPane = node({ sel: [PANE], scrollHeight: 1000, clientHeight: 800, scrollTop: 100 });
    main.children = [newPane];
    env.mutate();
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("スマホ: ページ全体（window）が動くときは window で決まる", () => {
    const main = node({ sel: [MAIN], scrollHeight: 900, clientHeight: 900 });
    const windowPage = { scrollTop: 300, scrollHeight: 1300, clientHeight: 700 };
    const env = makeEnv({ main, windowPage });
    env.scroll(env.doc);
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
});

test("スクロールできない画面は 0（割り算で壊れない）", () => {
    const main = node({ sel: [MAIN], scrollHeight: 500, clientHeight: 500 });
    const env = makeEnv({ main });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");
});

test("対戦画面（.page.battle-active）は、中身が動いていても 0", () => {
    const main = node({ sel: [MAIN], scrollHeight: 2000, clientHeight: 1000, scrollTop: 800 });
    const env = makeEnv({ main, battle: true });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.0000");
});

test("画面の大きさが変わったら、測り直す", () => {
    const main = node({ sel: [MAIN], scrollHeight: 2000, clientHeight: 1000, scrollTop: 500 });
    const env = makeEnv({ main });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
    main.clientHeight = 500;         // 窓を縮めると、残りの量が増える
    env.resize();
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.3333");
});

test("同じ値なら、線の style には書かない（スクロールしていない間は書き込まない）", () => {
    const main = node({ sel: [MAIN], scrollHeight: 2000, clientHeight: 1000, scrollTop: 500 });
    const env = makeEnv({ main });
    env.flush();
    const after = env.writes();
    env.scroll(main);                // 位置が変わっていないイベント
    env.flush();
    env.resize();
    env.flush();
    assert.equal(env.writes(), after);
});

test("html（文書全体）には書かない。値は線自身の style に入る", () => {
    const main = node({ sel: [MAIN], scrollHeight: 2000, clientHeight: 1000, scrollTop: 500 });
    const env = makeEnv({ main });
    env.flush();
    assert.equal(env.styles["--scroll-progress"], "0.5000");
    assert.equal(env.doc.documentElement.style, undefined);
});
