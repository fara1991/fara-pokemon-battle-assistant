// 起動までの読み込み表示（index.html の #boot-indicator。全画面のオーバーレイ）を管理する。
// - Blazor の描画（.mud-layout が #app に入る）で外す。入口ページの本文（.entry-static）は、それまでオーバーレイの下にある。
//   描画の途中（MudBlazor の style だけ）では外さない。.mud-layout が出た同じタスクで外すので、空白の一瞬は出ない。
// - #blazor-error-ui が出たら外す（起動に失敗した。読み込み表示を出し続けない）。
// - 45 秒たっても描かれない、または blazor.webassembly.js の読み込みに失敗したら、「再読み込み」の案内に変える（data-state="slow"。スピナーを止める）。
// - 60 秒たったら、オーバーレイを外す（描画されていなくても。本文を見せて、操作できるようにする）。CSS の boot-indicator-release も同じ 60 秒で隠す（JS が動かないときの保険）。
// - 静的な本文（.entry-static）は、オーバーレイがあるあいだ inert と aria-hidden="true" にして、読み上げ・Tab の対象から外す。外すとき（描画・60 秒）に戻す。
// 通常の文言は生の HTML に入れず、CSS（.boot-indicator-label:empty::before）で出す。案内に変わるときだけ JS が文字と再読み込みのリンクを入れる。
(function () {
    "use strict";
    var SLOW_MS = 45000;
    var RELEASE_MS = 60000;   // index.html の boot-indicator-release（CSS）と同じ秒数にする（tests で確かめる）
    var SLOW_TEXT = "読み込みに時間がかかっています。ページを再読み込みしてください";
    var indicator = document.getElementById("boot-indicator");
    var app = document.getElementById("app");
    var errorUi = document.getElementById("blazor-error-ui");
    // 要素が無いときは何もしない（CSS の 60 秒で隠れる）。MutationObserver が無い古いブラウザも同じく、JS では外さず CSS の 60 秒で外れる
    if (!indicator || !app || !window.MutationObserver) return;
    var label = indicator.querySelector(".boot-indicator-label");

    var observers = [];
    var slowTimer = null;
    var releaseTimer = null;
    var finished = false;
    var staticMains = [];

    function markStatic() {
        staticMains = Array.prototype.filter.call(app.children, function (el) {
            return el.classList.contains("entry-static");
        });
        staticMains.forEach(function (el) {
            el.setAttribute("inert", "");
            el.setAttribute("aria-hidden", "true");
        });
    }

    function releaseStatic() {
        staticMains.forEach(function (el) {
            el.removeAttribute("inert");
            el.removeAttribute("aria-hidden");
        });
        staticMains = [];
    }

    function stopWatching() {
        observers.forEach(function (o) { o.disconnect(); });
        observers = [];
        clearTimeout(slowTimer);
        clearTimeout(releaseTimer);
        slowTimer = null;
        releaseTimer = null;
        window.removeEventListener("error", onResourceError, true);
    }

    function finish() {
        if (finished) return;
        finished = true;
        stopWatching();
        releaseStatic();
        if (indicator.parentNode) indicator.parentNode.removeChild(indicator);
    }

    // 45 秒の案内。オーバーレイは残し、再読み込みのリンクだけ押せるようにする（本文は inert のまま）
    function markSlow() {
        if (finished || indicator.getAttribute("data-state") === "slow") return;
        indicator.setAttribute("data-state", "slow");
        if (label) {
            label.textContent = "";
            label.appendChild(document.createTextNode(SLOW_TEXT + " "));
            var link = document.createElement("a");
            link.setAttribute("href", "");
            link.textContent = "再読み込み";
            label.appendChild(link);
            label.setAttribute("aria-label", SLOW_TEXT + " 再読み込み");
        }
    }

    function rendered() {
        return !!app.querySelector(".mud-layout");
    }

    function checkRendered() {
        if (rendered()) finish();
    }

    function checkError() {
        if (errorUi && window.getComputedStyle(errorUi).display !== "none") finish();
    }

    // blazor.webassembly.js の読み込み失敗（script の error は bubble しないので、capture で受ける）
    function onResourceError(e) {
        var t = e && e.target;
        if (t && t.tagName === "SCRIPT" && typeof t.src === "string" && t.src.indexOf("blazor.webassembly.js") >= 0) markSlow();
    }

    var appObserver = new MutationObserver(checkRendered);
    appObserver.observe(app, { childList: true, subtree: true });
    observers.push(appObserver);

    if (errorUi) {
        var errorObserver = new MutationObserver(checkError);
        errorObserver.observe(errorUi, { attributes: true, attributeFilter: ["style", "class"] });
        observers.push(errorObserver);
    }

    window.addEventListener("error", onResourceError, true);
    slowTimer = setTimeout(markSlow, SLOW_MS);
    releaseTimer = setTimeout(finish, RELEASE_MS);

    markStatic();
    checkRendered();
    checkError();
})();
