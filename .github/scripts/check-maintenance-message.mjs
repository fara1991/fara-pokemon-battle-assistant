// 手動実行の maintenance_message を、--var（Worker の変数 MAINTENANCE_MESSAGE）に渡す前に確かめる（200 文字まで・改行なし）。
// deploy.yml / deploy-develop.yml の「Check maintenance message」から、環境変数 MAINTENANCE_MESSAGE_INPUT を渡して実行する。
// 空（変更しない）は通す。

export const MAX_MESSAGE_LENGTH = 200;

// 改行とみなす文字（CR・LF・NEL・行区切り・段落区切り）。コードポイントで持つ
const LINE_BREAK_CODE_POINTS = [0x0d, 0x0a, 0x85, 0x2028, 0x2029];

/**
 * 文面の検査。問題がなければ null、あれば理由（日本語）を返す。
 * 文字数は、サロゲートペア（絵文字など）を 1 文字として数える。
 */
export function checkMaintenanceMessage(message) {
  if (message === "") return null;
  const chars = [...message];
  if (chars.some((c) => LINE_BREAK_CODE_POINTS.includes(c.codePointAt(0)))) return "改行を含められません（1 行で入力してください）";
  if (chars.length > MAX_MESSAGE_LENGTH) return `${MAX_MESSAGE_LENGTH} 文字までです（いまは ${chars.length} 文字）`;
  return null;
}

if (process.argv[1]?.endsWith("check-maintenance-message.mjs")) {
  const problem = checkMaintenanceMessage(process.env.MAINTENANCE_MESSAGE_INPUT ?? "");
  if (problem) {
    console.error(`::error::maintenance_message: ${problem}`);
    process.exit(1);
  }
  console.log("maintenance_message: OK");
}
