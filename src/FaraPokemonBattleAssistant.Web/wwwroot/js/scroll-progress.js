// ページをどれだけ読んだかを、固定ヘッダーの上端の線で示す（MainLayout.razor の .scroll-progress）。
// 実際にスクロールしている要素を選ぶ:
//  - PC（768px 以上）: .page は 100vh で固定され、article.content の中がスクロールする
//  - スマホ: ページ全体（window）がスクロールする
// 値は html の --scroll-progress（0〜1）に入れ、線の幅は CSS の scaleX で決める。
// 対戦画面（.page.battle-active）はページを隠すので 0 にする。一覧の中（.pane-scroll など）のスクロールは数えない。
// html に入れるのは、#app の中の style を書き換えて motion.js の見直しを起こさないため。
(function () {
    "use strict";
    var root = document.documentElement;
    var CONTENT = "main > article.content";
    var pending = false;

    // 読んだ量（0〜1）。スクロールできない・対戦画面のときは 0
    function progress() {
        if (document.querySelector(".page.battle-active")) return 0;
        var content = document.querySelector(CONTENT);
        var el = content && getComputedStyle(content).overflowY !== "visible" ? content : document.scrollingElement || root;
        var max = el.scrollHeight - el.clientHeight;
        if (!(max > 0)) return 0;
        return Math.min(1, Math.max(0, el.scrollTop / max));
    }

    function update() {
        pending = false;
        root.style.setProperty("--scroll-progress", progress().toFixed(4));
    }

    // 1 フレームに 1 回だけ測る（スクロール・画面の大きさ・ページの中身が変わったとき）
    function queue() {
        if (pending) return;
        pending = true;
        requestAnimationFrame(update);
    }

    // 要素のスクロールはバブルしないので、捕まえる段階（capture）で受ける。window のスクロールの target は document
    document.addEventListener("scroll", function (e) {
        var t = e.target;
        if (t === document || (t.matches && t.matches(CONTENT))) queue();
    }, { capture: true, passive: true });
    window.addEventListener("resize", queue, { passive: true });
    new MutationObserver(queue).observe(document.getElementById("app") || document.body, { childList: true, subtree: true });
    queue();
})();
