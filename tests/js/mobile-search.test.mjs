// js/page.js のスマホの検索ボタン（押した瞬間に欄を出して、フォーカスする）と、検索欄の開閉（faraSearchSheet）を、最小限のスタブで確かめる。
// page.js は全体を読むと画面の初期化も走るので、この部分だけを取り出して動かす（区切りの見出しで切り出す）。
// 実行: node --test tests/js/mobile-search.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "../../src/FaraPokemonBattleAssistant.Web/wwwroot/js/page.js"), "utf8");
const START = "// スマホの検索ボタン";
const END = "// ヘッダーの検索欄へ移る";
const block = source.slice(source.indexOf(START), source.indexOf(END));

/** 画面の見本。html のクラス・検索の入力欄・検索ボタン・クリックの受け取り */
function makeDocument() {
    const html = { classes: new Set(), classList: null };
    html.classList = {
        toggle(c, on) { if (on === undefined) on = !html.classes.has(c); on ? html.classes.add(c) : html.classes.delete(c); return on; },
        contains: (c) => html.classes.has(c),
    };
    const input = { id: "site-search-input", focused: 0, focus() { this.focused++; doc.activeElement = this; } };
    const button = { focused: 0, focus() { this.focused++; doc.activeElement = this; } };
    const doc = {
        documentElement: html,
        activeElement: null,
        listeners: {},
        addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); },
        getElementById: (id) => (id === input.id ? input : null),
        querySelector: (sel) => (sel === ".header-search-toggle" ? button : null),
        input,
        button,
    };
    return doc;
}

/** click を送る。target は、closest で探すセレクタに一致するかどうかだけ見る */
function click(doc, matchesSelector) {
    const target = { closest: (sel) => (sel === matchesSelector ? {} : null) };
    for (const fn of doc.listeners.click || []) fn({ target });
}

function load() {
    const doc = makeDocument();
    const sandbox = { document: doc, window: {} };
    sandbox.window = sandbox;
    vm.runInNewContext(block, sandbox);
    return { doc, faraSearchSheet: sandbox.faraSearchSheet };
}

test("スマホの検索ボタンを押すと、欄が開いて、その場で入力欄にフォーカスする", () => {
    const { doc } = load();
    click(doc, ".header-search-toggle");
    assert.equal(doc.documentElement.classes.has("search-open"), true);
    assert.equal(doc.input.focused, 1, "押した流れの中で、入力欄にフォーカスする（iOS はこれが無いとキーボードが出ない）");
});

test("検索ボタン以外を押しても、欄は開かない", () => {
    const { doc } = load();
    click(doc, ".sidebar-toggle");
    assert.equal(doc.documentElement.classes.has("search-open"), false);
    assert.equal(doc.input.focused, 0);
});

test("faraSearchSheet(false, true) は欄を閉じて、検索ボタンにフォーカスを戻す", () => {
    const { doc, faraSearchSheet } = load();
    faraSearchSheet(true, true);
    faraSearchSheet(false, true);
    assert.equal(doc.documentElement.classes.has("search-open"), false);
    assert.equal(doc.button.focused, 1);
});

test("faraSearchSheet(false, false) は閉じるだけで、フォーカスは動かさない（ページを移るときなど）", () => {
    const { doc, faraSearchSheet } = load();
    faraSearchSheet(true, false);
    assert.equal(doc.input.focused, 0, "開くだけのときは、フォーカスしない");
    faraSearchSheet(false, false);
    assert.equal(doc.documentElement.classes.has("search-open"), false);
    assert.equal(doc.button.focused, 0);
});
