// トップページ（/）の配信。
// ホームの本文入りの HTML（home.html。tools/build-entry-pages.mjs が作る）は、/ のときだけこのモジュールが返す
// （存在しない URL は 404.html を 404 のステータスで返す設定なので、index.html は / には使わない）（wrangler.jsonc の run_worker_first に "/" を入れている）。
// 拡張子なし（/home）で取る。/home.html は /home へ転送されるため。
// 外から来る /home・/home.html・/home/ は、このモジュールの redirectHomeAlias が / へ 301 で転送する（worker/index.js。run_worker_first にも入れる）。

/** / のリクエストを、静的アセットの /home（home.html）に差し替えて返す。メソッドとヘッダーはそのまま渡す。 */
export function serveHome(request, env) {
    const url = new URL(request.url);
    return env.ASSETS.fetch(new Request(new URL("/home", url.origin), request));
}

/** 内部の入口ファイル（home.html）が配信され得る URL。/ へ転送する（URL を / に統一する。home.html の canonical は / のまま）。 */
export const HOME_ALIASES = ["/home", "/home.html", "/home/"];

/** HOME_ALIASES のとき、/ への 301（クエリは引き継ぐ）を返す。それ以外は null。 */
export function redirectHomeAlias(request) {
    const url = new URL(request.url);
    if (!HOME_ALIASES.includes(url.pathname)) return null;
    return new Response(null, { status: 301, headers: { Location: `/${url.search}`, "Cache-Control": "public, max-age=3600" } });
}
