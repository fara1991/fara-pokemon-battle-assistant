// 「作者へ報告」ページ（Components/Pages/Report.razor）の、Cloudflare Turnstile（ボット判定）の部品。
// 外部のスクリプトは https://challenges.cloudflare.com だけ（_headers の CSP で許可している）。
// サイトキーは公開してよい値で、GET /api/report/config から読む（worker/report.js）。
(function () {
    "use strict";
    var TURNSTILE_SRC = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";
    var widgetId = null;
    var token = "";
    var loading = null;

    function loadTurnstile() {
        if (window.turnstile) return Promise.resolve();
        if (loading) return loading;
        loading = new Promise(function (resolve, reject) {
            var s = document.createElement("script");
            s.src = TURNSTILE_SRC;
            s.async = true;
            s.onload = function () { resolve(); };
            s.onerror = function () { loading = null; reject(new Error("turnstile")); };
            document.head.appendChild(s);
        });
        return loading;
    }

    window.faraReport = {
        // 確認の欄を、指定した要素に出す。受け付けていない（設定が無い）ときは false
        mount: async function (containerId) {
            var res = await fetch("api/report/config", { headers: { Accept: "application/json" } });
            if (!res.ok) return false;
            var cfg = await res.json();
            if (!cfg.enabled || !cfg.siteKey) return false;
            await loadTurnstile();
            var el = document.getElementById(containerId);
            if (!el) return false;
            token = "";
            widgetId = window.turnstile.render(el, {
                sitekey: cfg.siteKey,
                callback: function (t) { token = t; },
                "expired-callback": function () { token = ""; },
                "error-callback": function () { token = ""; },
            });
            return true;
        },
        // 確認が済んだときのトークン（まだなら空）
        token: function () { return token; },
        // 確認のトークンが届くまで、最大 timeoutMs 待つ（読み込み直後や、送信に失敗して作り直した直後は、数秒かかることがある）。届かなければ空
        waitToken: function (timeoutMs) {
            return new Promise(function (resolve) {
                var waited = 0;
                (function check() {
                    if (token) { resolve(token); return; }
                    if (waited >= timeoutMs) { resolve(""); return; }
                    waited += 200;
                    setTimeout(check, 200);
                })();
            });
        },
        // 確認は 1 回で使い切りなので、送信に失敗したあとなどに作り直す
        reset: function () {
            token = "";
            if (window.turnstile && widgetId !== null) { try { window.turnstile.reset(widgetId); } catch (e) { /* すでに無いとき */ } }
        },
        dispose: function () {
            token = "";
            if (window.turnstile && widgetId !== null) { try { window.turnstile.remove(widgetId); } catch (e) { /* すでに無いとき */ } }
            widgetId = null;
        },
    };
})();
