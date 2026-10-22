// 作者への報告の受け口（POST /api/report）。Pokémon Battle Assistant・Avatar Modding Guide・FaraBotModerator の 3 サイトで同じファイルを使う
// （各リポジトリの worker/ に同じ内容を置いている。直すときは 3 つともそろえること）。
//
//   GET  /api/report/config → 200 {"enabled": bool, "siteKey": "Turnstile のサイトキー" | null}
//   POST /api/report        ← {"category": "bug" | "request" | "other", "message": "...", "contact"?: "...", "page"?: "...", "turnstileToken": "...", "website": ""}
//                           → 200 {"ok": true} / 400 / 403 / 413 / 415 / 502 / 503
//
// 流れ: 同じサイトのページからか確認 → 本文の検証（長さ・種類） → Turnstile（Cloudflare のボット判定）の確認 → Discord の Webhook に送る。
// 設定（Cloudflare のダッシュボードの Worker の「変数とシークレット」。docs/report-and-maintenance.md）:
//   TURNSTILE_SITE_KEY   サイトキー（公開してよい。変数）
//   TURNSTILE_SECRET     シークレットキー（シークレット）
//   DISCORD_WEBHOOK_URL  通知を送る Discord の Webhook の URL（シークレット）
// どれかが無いときは 503 を返す（報告ページには「いまは受け付けていません」と出る）。
// シークレットと Webhook の URL は、ログ・エラーメッセージ・応答に出さない（このファイルでは console.* を使わない）。
// 報告の本文は個人情報になりうるので、保存しない（Discord に送るだけ）。

const MAX_BODY_CHARS = 8 * 1024;
/** Content-Length（バイト）の上限。日本語 2000 文字（1 文字 3 バイト）と余白を入れても、これを超えない。 */
const MAX_BODY_BYTES = 16 * 1024;
/** 外へ出る通信（Turnstile・Discord）を待つ最大の時間。 */
const FETCH_TIMEOUT_MS = 5000;
const MAX_MESSAGE = 2000;
const MAX_CONTACT = 200;
const MAX_PAGE = 300;
const MIN_MESSAGE = 5;
const TURNSTILE_URL = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
const CATEGORIES = { bug: "不具合", request: "要望・提案", other: "その他" };

const json = (status, body, headers = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", ...headers } });

/**
 * 制御文字（改行・タブ以外）と、対になっていないサロゲートを取り、前後の空白を取り、長さ（文字数。絵文字は 1 文字）を切る。
 * 文字列でなければ空。（UTF-16 のまま切ると絵文字が割れて、Discord が受け付けないことがあるため、文字単位で数える。）
 */
export function clean(value, max) {
  if (typeof value !== "string") return "";
  const text = value
    // eslint-disable-next-line no-control-regex
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, "")
    .replace(/[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/g, "")
    .trim();
  const chars = Array.from(text);
  return chars.length > max ? chars.slice(0, max).join("").trim() : text;
}

/** 報告のきっかけのページ（パスだけ）。フッターのリンク・report.js と同じ条件。そうでなければ空。 */
export function cleanPage(value) {
  return typeof value === "string" && /^\/[A-Za-z0-9\-_/.]{0,299}$/.test(value) && !value.startsWith("//") ? value : "";
}

/** Origin ヘッダーのホスト。読めないもの（"null" など）は null。 */
function originHost(origin) {
  try { return new URL(origin).host; } catch { return null; }
}

/** 報告の受け口が使える設定か（3 つの値がそろっているか）。 */
export function isConfigured(env) {
  return Boolean(env.TURNSTILE_SITE_KEY && env.TURNSTILE_SECRET && env.DISCORD_WEBHOOK_URL);
}

/**
 * /api/report と /api/report/config を処理する。
 * @param {Request} request
 * @param {Record<string, string | undefined>} env
 * @param {string} siteName 通知に出すサイト名
 * @param {{ fetch?: typeof fetch, now?: () => Date }} [deps] テスト用の差し替え
 */
export async function handleReport(request, env, siteName, deps = {}) {
  const doFetch = deps.fetch ?? fetch;
  const url = new URL(request.url);

  if (url.pathname === "/api/report/config") {
    if (request.method !== "GET") return json(405, { error: "method_not_allowed" }, { Allow: "GET" });
    // サイトキーは公開してよい値。未設定のときは null（ページ側が「受け付けていません」と出す）
    return json(200, { enabled: isConfigured(env), siteKey: env.TURNSTILE_SITE_KEY || null });
  }
  if (url.pathname !== "/api/report") return json(404, { error: "not_found" });
  if (request.method !== "POST") return json(405, { error: "method_not_allowed" }, { Allow: "POST" });

  // 別のサイトのページから送られないように、同じホストのページからだけ受け付ける
  const origin = request.headers.get("Origin");
  if (!origin || originHost(origin) !== url.host) return json(403, { error: "forbidden" });
  if (!(request.headers.get("Content-Type") ?? "").toLowerCase().startsWith("application/json")) return json(415, { error: "unsupported_media_type" });

  // 大きな本文は、読み込む前に断る（Content-Length がうそでも、読んだあとの長さで再度確かめる）
  const declared = Number(request.headers.get("Content-Length"));
  if (Number.isFinite(declared) && declared > MAX_BODY_BYTES) return json(413, { error: "too_large" });
  const text = await request.text();
  if (text.length > MAX_BODY_CHARS) return json(413, { error: "too_large" });
  let body;
  try { body = JSON.parse(text); } catch { return json(400, { error: "invalid_json", message: "送信内容を読めませんでした" }); }
  if (body === null || typeof body !== "object" || Array.isArray(body)) return json(400, { error: "invalid_body", message: "送信内容を読めませんでした" });

  // ボット対策の罠（人には見えない欄）。入っていたら、何も送らずに成功したように返す
  if (typeof body.website === "string" && body.website.length > 0) return json(200, { ok: true });

  const category = typeof body.category === "string" && Object.hasOwn(CATEGORIES, body.category) ? body.category : null;
  if (!category) return json(400, { error: "invalid_category", message: "種類を選んでください" });
  const message = clean(body.message, MAX_MESSAGE);
  if (message.length < MIN_MESSAGE) return json(400, { error: "invalid_message", message: `内容を ${MIN_MESSAGE} 文字以上で書いてください` });
  const contact = clean(body.contact, MAX_CONTACT);
  const page = cleanPage(body.page);
  const token = typeof body.turnstileToken === "string" ? body.turnstileToken.slice(0, 2048) : "";
  if (!token) return json(400, { error: "missing_token", message: "「ロボットではありません」の確認をしてください" });

  if (!isConfigured(env)) return json(503, { error: "not_configured", message: "いまは報告を受け付けていません" });

  // Turnstile（ボット判定）
  const form = new URLSearchParams({ secret: env.TURNSTILE_SECRET, response: token });
  const ip = request.headers.get("CF-Connecting-IP");
  if (ip) form.set("remoteip", ip);
  let result;
  try {
    const res = await doFetch(TURNSTILE_URL, { method: "POST", body: form, signal: AbortSignal.timeout(FETCH_TIMEOUT_MS) });
    // Turnstile 側の障害（5xx）は、こちらの入力の問題ではないので 502
    if (res.status >= 500) return json(502, { error: "verify_failed", message: "確認に失敗しました。もう一度お試しください" });
    result = res.ok ? await res.json() : null;
  } catch {
    return json(502, { error: "verify_failed", message: "確認に失敗しました。もう一度お試しください" });
  }
  // 確認が通ったページが、いま送っているこのサイトのものか（別のサイトで取ったトークンを使い回されないように）。hostname が無いときは確かめない
  const hostOk = typeof result?.hostname !== "string" || result.hostname === url.hostname;
  if (result?.success !== true || !hostOk) return json(400, { error: "turnstile_failed", message: "「ロボットではありません」の確認に失敗しました。もう一度お試しください" });

  // Discord へ。@everyone などのメンションは無効にする
  const fields = [{ name: "種類", value: CATEGORIES[category], inline: true }];
  if (contact) fields.push({ name: "連絡先", value: contact, inline: true });
  if (page) fields.push({ name: "ページ", value: page });
  const country = request.cf?.country;
  if (country) fields.push({ name: "国", value: String(country), inline: true });
  const payload = {
    username: "サイトからの報告",
    allowed_mentions: { parse: [] },
    embeds: [{
      title: `${siteName} への報告`,
      description: message,
      color: category === "bug" ? 0xdc3545 : category === "request" ? 0x7c3aed : 0x6b5f8a,
      fields,
      timestamp: (deps.now?.() ?? new Date()).toISOString(),
    }],
  };
  try {
    const res = await doFetch(env.DISCORD_WEBHOOK_URL, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload), signal: AbortSignal.timeout(FETCH_TIMEOUT_MS) });
    if (!res.ok) return json(502, { error: "notify_failed", message: "送信に失敗しました。しばらくしてからもう一度お試しください" });
  } catch {
    return json(502, { error: "notify_failed", message: "送信に失敗しました。しばらくしてからもう一度お試しください" });
  }
  return json(200, { ok: true });
}
