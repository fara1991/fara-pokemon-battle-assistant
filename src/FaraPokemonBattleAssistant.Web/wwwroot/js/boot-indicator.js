// 起動までの「読み込み中…」（index.html の #boot-indicator）を管理する。
// - Blazor が #app に画面を描いたら外す（入口ページは #app の中に .entry-static の本文を置いているので、それ以外の子要素が出たら起動済み）。
// - #blazor-error-ui が出たら外す（起動に失敗した。読み込み表示を出し続けない）。
// - 一定時間（SLOW_MS）たっても描かれない、または blazor.webassembly.js の読み込みに失敗したら、
//   「再読み込みしてください」の文言に切り替える（data-state="slow"。スピナーは止める）。
// 通常の文言は生の HTML に入れず、CSS（.boot-indicator-label:empty::before）で出す。切り替えのときだけ JS が文字を入れる。
// #app の外に置いてあるので、描画の後も残る（そのため、ここで外す）。
(function () {
    "use strict";
    var SLOW_MS = 45000;
    var SLOW_TEXT = "読み込みに時間がかかっています。ページを再読み込みしてください";
    var indicator = document.getElementById("boot-indicator");
    var app = document.getElementById("app");
    var errorUi = document.getElementById("blazor-error-ui");
    if (!indicator || !app || !window.MutationObserver) return;
    var label = indicator.querySelector(".boot-indicator-label");

    var observers = [];
    var timer = null;
    var finished = false;

    function stopWatching() {
        observers.forEach(function (o) { o.disconnect(); });
        observers = [];
        clearTimeout(timer);
        timer = null;
        window.removeEventListener("error", onResourceError, true);
    }

    function finish() {
        if (finished) return;
        finished = true;
        stopWatching();
        if (indicator.parentNode) indicator.parentNode.removeChild(indicator);
    }

    function markSlow() {
        if (finished || indicator.getAttribute("data-state") === "slow") return;
        clearTimeout(timer);
        timer = null;
        indicator.setAttribute("data-state", "slow");
        if (label) {
            label.textContent = SLOW_TEXT;
            label.setAttribute("aria-label", SLOW_TEXT);
        }
    }

    function rendered() {
        return Array.prototype.some.call(app.children, function (el) {
            return !el.classList.contains("entry-static");
        });
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
    appObserver.observe(app, { childList: true });
    observers.push(appObserver);

    if (errorUi) {
        var errorObserver = new MutationObserver(checkError);
        errorObserver.observe(errorUi, { attributes: true, attributeFilter: ["style", "class"] });
        observers.push(errorObserver);
    }

    window.addEventListener("error", onResourceError, true);
    timer = setTimeout(markSlow, SLOW_MS);

    checkRendered();
    checkError();
})();
