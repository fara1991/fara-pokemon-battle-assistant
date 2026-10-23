// トップページ（/）の配信。
// ホームの本文入りの HTML（home.html。tools/build-entry-pages.mjs が作る）は、/ のときだけこのモジュールが返す
// （存在しない URL は 404.html を 404 のステータスで返す設定なので、index.html は / には使わない）（wrangler.jsonc の run_worker_first に "/" を入れている）。
// 拡張子なし（/home）で取る。/home.html は /home へ転送されるため。

/** / のリクエストを、静的アセットの /home（home.html）に差し替えて返す。メソッドとヘッダーはそのまま渡す。 */
export function serveHome(request, env) {
    const url = new URL(request.url);
    return env.ASSETS.fetch(new Request(new URL("/home", url.origin), request));
}
