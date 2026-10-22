// ページをどれだけ読んだかを、固定ヘッダーの上端の線で示す（MainLayout.razor の .scroll-progress）。
// 測る要素を選ぶ:
//  - スマホ: ページ全体（window）がスクロールするので、window を測る
//  - PC（768px 以上）: .page は 100vh で固定され、スクロールは article.content か、その中の .pane-scroll で起きる。
//    動かせる要素（.content 自体、または .pane-scroll で scrollHeight > clientHeight のもの）のうち、
//    ほかの動かせる要素の中に入っていない一番外側のものを測る（複数あれば一番背の高いもの）。
//    一覧の中の小さな入れ子スクロールは、外側が動いていれば対象外。
// 候補は、スクロールのイベント（capture で受ける）と、画面の作り直し（#app の childList）のときだけ見直す。全要素は走査しない。
// 値は線（.scroll-progress）自身の --scroll-progress（0〜1）に入れる。html に書くと文書全体のスタイルを計算し直すため。
// 前と同じ値なら書かない。対戦画面（.page.battle-active）は 0。
// motion.js は #app の style の変化を見直すので、線の style の変化は見直さない（motion.js の own の判定）。
(function () {
    "use strict";
    var MAIN = "main > article.content";
    var PANES = MAIN + ", " + MAIN + " .pane-scroll";
    var active = null;          // 最後に測った PC の要素（画面が替わったら外れるので、そのつど確かめる）
    var pending = false;
    var bar = null, shown = "";

    function content() { return document.querySelector(MAIN); }

    function scrollable(el) { return !!el && el.isConnected && el.scrollHeight - el.clientHeight > 1; }

    // 動かせる候補（.content と .pane-scroll）のうち、ほかの動かせる候補の中に入っていないもの
    function outerScrollers() {
        var list = Array.prototype.filter.call(document.querySelectorAll(PANES), scrollable);
        return list.filter(function (el) {
            return !list.some(function (o) { return o !== el && o.contains(el); });
        });
    }

    // 測る要素を決める。window が動くならスマホ、そうでなければ PC の一番背の高い候補
    function scroller() {
        var page = document.scrollingElement || document.documentElement;
        if (page.scrollHeight - page.clientHeight > 1) return page;
        if (scrollable(active)) return active;
        active = null;
        outerScrollers().forEach(function (el) {
            if (!active || el.clientHeight > active.clientHeight) active = el;
        });
        return active;
    }

    // 読んだ量（0〜1）。スクロールできないときは 0（割り算で壊れない）
    function progress() {
        if (document.querySelector(".page.battle-active")) return 0;
        var el = scroller();
        if (!el) return 0;
        var max = el.scrollHeight - el.clientHeight;
        if (!(max > 0)) return 0;
        return Math.min(1, Math.max(0, el.scrollTop / max));
    }

    function update() {
        pending = false;
        if (!bar || !bar.isConnected) bar = document.querySelector(".scroll-progress");
        if (!bar) return;
        var value = progress().toFixed(4);
        if (value === shown) return;     // 前と同じなら書かない
        shown = value;
        bar.style.setProperty("--scroll-progress", value);
    }

    // 1 フレームに 1 回だけ測る
    function queue() {
        if (pending) return;
        pending = true;
        requestAnimationFrame(update);
    }

    // スクロールの target: window（document）なら測り直す。本文の中の要素なら、大きければ測る対象に採用する
    document.addEventListener("scroll", function (e) {
        var t = e.target;
        if (t === document) { queue(); return; }
        var main = content();
        if (!main || !main.contains(t) || !t.matches || !t.matches(PANES)) return;
        if (!scrollable(t) || outerScrollers().indexOf(t) < 0) return;     // 外側の一覧の中の入れ子のスクロールは数えない
        active = t;
        queue();
    }, { capture: true, passive: true });
    window.addEventListener("resize", queue, { passive: true });
    // 画面が替わると、測っていた要素が外れる（isConnected で見直す）。描画の変化は、ここで受ける
    new MutationObserver(queue).observe(document.getElementById("app") || document.body, { childList: true, subtree: true });
    queue();
})();
