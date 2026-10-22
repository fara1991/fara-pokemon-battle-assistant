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
const startAt = source.indexOf(START);
const endAt = source.indexOf(END);
test("page.js に、切り出す区切りの見出しがある（見つからなければ、ここで失敗する）", () => {
    assert.notEqual(startAt, -1, `見出しが無い: ${START}`);
    assert.notEqual(endAt, -1, `見出しが無い: ${END}`);
    assert.ok(endAt > startAt, "見出しの順番が逆");
});
const block = source.slice(startAt, endAt);

/** 画面の見本。html のクラス・検索欄（sheet の中の入力欄と × ボタン）・検索ボタン・フォーカス */
function makeDocument() {
    const html = { classes: new Set() };
    html.classList = {
        toggle(c, on) { if (on === undefined) on = !html.classes.has(c); on ? html.classes.add(c) : html.classes.delete(c); return on; },
        contains: (c) => html.classes.has(c),
    };
    const focusable = (name) => ({
        name, focused: 0, blurred: 0, sheetOpenAtFocus: null,
        focus() { this.focused++; this.sheetOpenAtFocus = html.classes.has("search-open"); doc.activeElement = this; },
        blur() { this.blurred++; if (doc.activeElement === this) doc.activeElement = null; },
    });
    const input = focusable("input");
    const closeBtn = focusable("close");
    const button = focusable("toggle");
    const other = focusable("other");
    const sheet = { contains: (x) => x === input || x === closeBtn };
    const doc = {
        documentElement: html,
        activeElement: null,
        listeners: {},
        addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); },
        getElementById: (id) => (id === "site-search-input" ? input : id === "site-search-sheet" ? sheet : null),
        querySelector: (sel) => (sel === ".header-search-toggle" ? button : null),
        input, closeBtn, button, other, sheet,
    };
    return doc;
}

/** click を送る。target は、closest で探すセレクタに一致するかどうかだけ見る */
function click(doc, matchesSelector) {
    const target = { closest: (sel) => (sel === matchesSelector ? {} : null) };
    for (const fn of doc.listeners.click || []) fn({ target });
}

function load(doc = makeDocument()) {
    const sandbox = { document: doc };
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

test("欄の中（× ボタン）にフォーカスがあって閉じると、検索ボタンへ移す", () => {
    const { doc, faraSearchSheet } = load();
    faraSearchSheet(true, true);
    doc.closeBtn.focus();
    faraSearchSheet(false, true);
    assert.equal(doc.documentElement.classes.has("search-open"), false);
    assert.equal(doc.button.focused, 1);
});

test("フォーカスが欄の中（入力欄）にあるまま閉じると、欄が隠れる前に検索ボタンへ移す（body に落ちない）", () => {
    const { doc, faraSearchSheet } = load();
    faraSearchSheet(true, true);
    assert.equal(doc.activeElement, doc.input);
    faraSearchSheet(false, false);
    assert.equal(doc.button.focused, 1, "欄から離れて、検索ボタンへ移る");
    assert.equal(doc.button.sheetOpenAtFocus, true, "移すのは、欄が隠れる（search-open を外す）前");
    assert.equal(doc.documentElement.classes.has("search-open"), false);
});

test("フォーカスが欄の外にあるときは、閉じても、フォーカスは動かさない", () => {
    const { doc, faraSearchSheet } = load();
    faraSearchSheet(true, true);
    doc.other.focus();
    faraSearchSheet(false, false);
    assert.equal(doc.button.focused, 0);
    assert.equal(doc.activeElement, doc.other);
});

test("検索ボタンが無い（PC の幅）で、欄の中にフォーカスがあって閉じると、フォーカスを外す", () => {
    const doc = makeDocument();
    doc.querySelector = () => null;
    const { faraSearchSheet } = load(doc);
    faraSearchSheet(true, true);
    faraSearchSheet(false, false);
    assert.equal(doc.input.blurred, 1);
    assert.equal(doc.activeElement, null);
});

test("faraSearchSheet(true, false) は開くだけで、フォーカスは動かさない", () => {
    const { doc, faraSearchSheet } = load();
    faraSearchSheet(true, false);
    assert.equal(doc.documentElement.classes.has("search-open"), true);
    assert.equal(doc.input.focused, 0);
});
