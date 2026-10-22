// 入口ページの静的な本文（main.entry-static。tools/build-entry-pages.mjs が作る）を、そのページの URL のときだけ残す。
// 静的ファイルが無い URL（SPA のフォールバック）は index.html（ホームの本文）を返すので、ホームの本文が一瞬出ないように、
// 読み込み表示に替える。WebAssembly が起動すると、Blazor が #app の中身ごと置き換える。
(function () {
    var main = document.querySelector("main.entry-static[data-route]");
    if (!main) return;
    var path = location.pathname.replace(/^\/+|\/+$/g, "").replace(/\.html$/, "");
    if (path === "index") path = "";
    if (path === main.getAttribute("data-route")) return;
    main.outerHTML = '<div class="loading-screen"><div class="spinner-border text-primary" role="status"></div><p>読み込み中...</p></div>';
})();
