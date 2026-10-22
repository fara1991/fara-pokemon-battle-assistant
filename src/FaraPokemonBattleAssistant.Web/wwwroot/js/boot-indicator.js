// 起動までの「読み込み中…」（index.html の #boot-indicator）を、Blazor の描画で外す。
// 入口ページ（tools/build-entry-pages.mjs が作る）は #app の中に .entry-static の本文を置いているので、
// それ以外の子要素（Blazor が描いた画面）が #app に入ったら、表示の役目は終わり。
// Blazor が起動に失敗したとき（#blazor-error-ui が出たとき）も、「読み込み中」を出し続けないために外す。
// #app の外に置いてあるので、描画の後も残る（そのため、ここで外す）。
(function () {
    "use strict";
    var indicator = document.getElementById("boot-indicator");
    var app = document.getElementById("app");
    var errorUi = document.getElementById("blazor-error-ui");
    if (!indicator || !app || !window.MutationObserver) return;

    var observers = [];

    function finish() {
        observers.forEach(function (o) { o.disconnect(); });
        observers = [];
        if (indicator.parentNode) indicator.parentNode.removeChild(indicator);
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

    var appObserver = new MutationObserver(checkRendered);
    appObserver.observe(app, { childList: true });
    observers.push(appObserver);

    if (errorUi) {
        var errorObserver = new MutationObserver(checkError);
        errorObserver.observe(errorUi, { attributes: true, attributeFilter: ["style", "class"] });
        observers.push(errorObserver);
    }

    checkRendered();
    checkError();
})();
