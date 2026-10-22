// メンテナンス表示（3 サイトで同じファイルを使う。各リポジトリの worker/ に同じ内容を置いている。直すときは 3 つともそろえること）。
//
// Cloudflare のダッシュボード（Worker の「変数とシークレット」）の変数で切り替える。デプロイ（変数の保存）の数秒〜1 分後に反映される。
//   MAINTENANCE          "false"（または未設定）= 通常 / "notice" = 通常のページの上に「更新予定」のお知らせを出す / "true" = メンテナンス画面（503）
//   MAINTENANCE_MESSAGE  お知らせ・メンテナンス画面に出す文（例: 10月25日（土）20:00〜21:00 ごろ、サイトを更新します）。200 文字まで
// お知らせは "notice" と "true" のときだけ出るので、MAINTENANCE を "false" に戻せば、お知らせも一緒に消える。
// GitHub の自動デプロイで変数が消えないよう、wrangler の設定に keep_vars = true を入れている。

const MAX_MESSAGE = 200;
const TWITTER_URL = "https://twitter.com/game_fara";

export const escapeHtml = (text) =>
  String(text).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

/** 制御文字を取り、前後の空白を取り、長さを切る（文字単位）。文字列でなければ空。 */
function cleanMessage(value) {
  if (typeof value !== "string") return "";
  // eslint-disable-next-line no-control-regex
  const text = value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, "").trim();
  const chars = Array.from(text);
  return chars.length > MAX_MESSAGE ? chars.slice(0, MAX_MESSAGE).join("").trim() : text;
}

/**
 * 変数から、いまの状態を読む。
 * @returns {{ state: "off" | "notice" | "on", message: string }}
 */
export function getMaintenance(env) {
  const raw = String(env?.MAINTENANCE ?? "").trim().toLowerCase();
  const state = raw === "true" || raw === "on" || raw === "1" ? "on" : raw === "notice" ? "notice" : "off";
  return { state, message: state === "off" ? "" : cleanMessage(env?.MAINTENANCE_MESSAGE) };
}

/** メンテナンス画面の HTML（ファイルを読み込まない、1 枚で完結する画面。docs/maintenance.html と同じデザイン）。 */
export function maintenancePageHtml(siteName, message) {
  const schedule = message ? `<p class="s">${escapeHtml(message)}</p>` : "";
  return `<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex"><title>メンテナンス中 | ${escapeHtml(siteName)}</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px;background:#0c0616;color:#f3edff;font:1rem/1.8 system-ui,sans-serif;text-align:center}h1{font-size:1.5rem;color:#b39ddb}p{color:#b4a7d1}.s{color:#f3edff;font-weight:700}a{display:inline-block;margin-top:8px;padding:8px 20px;border-radius:99px;background:#7c3aed;color:#fff;text-decoration:none;font-weight:700}</style></head>
<body><main><h1>ただいまメンテナンス中です</h1><p>サイトの調整を行っています。<br>しばらくしてから、もう一度お試しください。</p>${schedule}<p>お知らせは Twitter で</p><a href="${TWITTER_URL}" rel="noopener">@game_fara（Twitter）</a></main></body></html>
`;
}

/** お知らせのバナー（各サイトの CSS の .maintenance-notice で見た目を付ける。CSP のため、インラインの style は使わない）。 */
export function noticeHtml(message) {
  const text = message || "まもなくメンテナンスを行います。";
  return `<div class="maintenance-notice" role="status"><strong>お知らせ</strong> ${escapeHtml(text)}</div>`;
}

/**
 * メンテナンス中（state "on"）の応答。
 * - ページを開く（Accept に text/html）: メンテナンス画面
 * - /api/ や JSON を求める: JSON {"error": "maintenance", "message": ...}
 * - それ以外（画像・スクリプトなど）: 短い文字
 * どれも 503 で、キャッシュさせず、検索エンジンには載せない。Retry-After は 1 時間。
 */
export function maintenanceResponse(request, info, siteName) {
  const url = new URL(request.url);
  const accept = request.headers.get("Accept") ?? "";
  const headers = { "Cache-Control": "no-store", "Retry-After": "3600", "X-Robots-Tag": "noindex" };
  if (url.pathname.startsWith("/api/") || (accept.includes("application/json") && !accept.includes("text/html"))) {
    return new Response(JSON.stringify({ error: "maintenance", message: info.message || "メンテナンス中です" }), {
      status: 503,
      headers: { ...headers, "Content-Type": "application/json; charset=utf-8" },
    });
  }
  if (accept.includes("text/html")) {
    return new Response(request.method === "HEAD" ? null : maintenancePageHtml(siteName, info.message), {
      status: 503,
      headers: {
        ...headers,
        "Content-Type": "text/html; charset=utf-8",
        // この画面は、スタイルを中に持つので、サイトの CSP とは別に、必要なものだけ許可する
        "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
      },
    });
  }
  return new Response("メンテナンス中です", { status: 503, headers: { ...headers, "Content-Type": "text/plain; charset=utf-8" } });
}

/**
 * お知らせ（state "notice"）のとき、HTML の応答の <main> の先頭にバナーを足す。HTML でない応答（画像・スクリプトなど）はそのまま。
 * @param {Response} response
 * @param {{ state: string, message: string }} info
 * @param {(response: Response, html: string) => Response} [rewrite] テスト用の差し替え（Workers では HTMLRewriter を使う）
 */
export function withNotice(response, info, rewrite = rewriteWithHtmlRewriter) {
  if (info.state !== "notice") return response;
  const type = response.headers.get("Content-Type") ?? "";
  if (!type.includes("text/html")) return response;
  const out = rewrite(response, noticeHtml(info.message));
  // お知らせは、切り替えたらすぐ消えるように、HTML をキャッシュさせない
  const copy = new Response(out.body, out);
  copy.headers.set("Cache-Control", "no-store");
  return copy;
}

function rewriteWithHtmlRewriter(response, html) {
  // eslint-disable-next-line no-undef
  return new HTMLRewriter().on("main", { element(el) { el.prepend(html, { html: true }); } }).transform(response);
}
