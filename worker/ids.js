// Worker（worker/index.js・worker/teams.js）で共有する、ID と JSON オブジェクトの検査。上限は 1 つだけ（クライアント C# の int に収まる値）。

/** ID の最大値。クライアント（C#）は int で持つので、次の ID（最大 + 1）も int に収まるようにする。 */
export const MAX_ID = 2147483646;

/** JSON のオブジェクト（配列と null は含めない）。 */
export function isPlainObject(v) {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/** 1 以上 MAX_ID 以下の整数（ID・チーム番号）。 */
export function isId(v) {
  return Number.isSafeInteger(v) && v >= 1 && v <= MAX_ID;
}
