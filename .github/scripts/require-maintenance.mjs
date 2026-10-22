// 本番の公開（デプロイ）の前に、Cloudflare の Worker の変数 MAINTENANCE が "notice"（または "true"）になっているかを確かめる。
// 3 サイト（pokemon・avatar・bot）とポートフォリオで同じファイルを使う（各リポジトリの .github/scripts/ に同じ内容を置いている）。
//
//   node .github/scripts/require-maintenance.mjs <Worker 名>
//   必要な環境変数: CLOUDFLARE_API_TOKEN, CLOUDFLARE_ACCOUNT_ID（公開に使っているものと同じ。Workers Scripts の読み取りができること）
//   SKIP_MAINTENANCE_CHECK=true のときは、確かめずに通す（緊急の修正を、お知らせなしで公開したいとき。ワークフローの手動実行の入力から渡す）
//
// 目的: 本番の公開の前に、お知らせ（notice）を出しておく運用にする。お知らせなしに本番が更新されるのを防ぐ。
//   notice … 通す（お知らせを出している）
//   true   … 通す（すでにメンテナンス中。公開をやり直すとき）
//   false・未設定・そのほか … 止める
//   Worker がまだ無い（初回の公開。404）… 通す
//   確認できなかった（通信・権限の問題）… 止める（確かめずに公開しない）
// 変数は、ダッシュボードで「テキスト」にしておく（シークレットは値を読めないため、確かめられない）。

/** Worker の設定（bindings）から、MAINTENANCE の値を読む。無い・文字列でないときは空。 */
export function readMaintenanceValue(bindings) {
  const binding = Array.isArray(bindings) ? bindings.find((b) => b && b.name === "MAINTENANCE") : undefined;
  if (!binding) return "";
  if (binding.type === "plain_text" && typeof binding.text === "string") return binding.text.trim();
  return binding.type === "secret_text" ? "(secret)" : "";
}

/**
 * 公開してよいか。
 * @param {string} value MAINTENANCE の値（readMaintenanceValue）
 * @returns {{ ok: boolean, state: "notice" | "on" | "off" | "secret", message: string }}
 */
export function evaluate(value) {
  const v = String(value ?? "").trim().toLowerCase();
  if (v === "notice") return { ok: true, state: "notice", message: "MAINTENANCE=notice（お知らせを出しています）。公開します" };
  if (v === "true" || v === "on" || v === "1") return { ok: true, state: "on", message: "MAINTENANCE=true（すでにメンテナンス中です）。公開します" };
  if (v === "(secret)") {
    return { ok: false, state: "secret", message: "MAINTENANCE が「シークレット」になっていて、値を確かめられません。ダッシュボードで「テキスト」の変数にしてください" };
  }
  return {
    ok: false,
    state: "off",
    message: `MAINTENANCE が notice になっていません（いまの値: ${v === "" ? "未設定" : v}）。先に Cloudflare の Worker の変数 MAINTENANCE を notice にして、更新予定のお知らせを出してから、公開してください`,
  };
}

/** Cloudflare の API から、Worker の設定を読む。 */
export async function fetchSettings(workerName, { token, accountId, fetchImpl = fetch }) {
  const url = `https://api.cloudflare.com/client/v4/accounts/${encodeURIComponent(accountId)}/workers/scripts/${encodeURIComponent(workerName)}/settings`;
  const res = await fetchImpl(url, { headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }, signal: AbortSignal.timeout(15000) });
  if (res.status === 404) return { missing: true, bindings: [] };
  if (!res.ok) throw new Error(`Cloudflare の API が ${res.status} を返しました（API トークンに Workers Scripts の読み取り権限があるか確かめてください）`);
  const body = await res.json();
  if (body?.success === false) throw new Error("Cloudflare の API がエラーを返しました");
  return { missing: false, bindings: body?.result?.bindings ?? [] };
}

/** 本体。通すなら 0、止めるなら 1 を返す（テストできるよう、終了はここでしない）。 */
export async function run({ workerName, env, fetchImpl, log = console.log, error = console.error }) {
  if (!workerName) { error("使い方: node .github/scripts/require-maintenance.mjs <Worker 名>"); return 2; }
  if (String(env.SKIP_MAINTENANCE_CHECK ?? "").toLowerCase() === "true") {
    log("::warning::MAINTENANCE の確認を飛ばしました（SKIP_MAINTENANCE_CHECK=true）");
    return 0;
  }
  const token = env.CLOUDFLARE_API_TOKEN;
  const accountId = env.CLOUDFLARE_ACCOUNT_ID;
  if (!token || !accountId) { error("::error::CLOUDFLARE_API_TOKEN / CLOUDFLARE_ACCOUNT_ID がありません"); return 1; }
  let settings;
  try {
    settings = await fetchSettings(workerName, { token, accountId, fetchImpl });
  } catch (e) {
    error(`::error::MAINTENANCE を確かめられませんでした。公開を止めます: ${e.message}`);
    return 1;
  }
  if (settings.missing) {
    log(`Worker ${workerName} がまだありません（初回の公開）。確認を飛ばして公開します`);
    return 0;
  }
  const result = evaluate(readMaintenanceValue(settings.bindings));
  if (result.ok) { log(result.message); return 0; }
  error(`::error::${result.message}`);
  return 1;
}

// コマンドラインから実行されたとき
if (process.argv[1]?.endsWith("require-maintenance.mjs")) {
  run({ workerName: process.argv[2], env: process.env }).then((code) => process.exit(code));
}
