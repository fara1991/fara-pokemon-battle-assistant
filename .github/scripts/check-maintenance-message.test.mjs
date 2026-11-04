// check-maintenance-message.mjs の検査。実行: node --test .github/scripts/check-maintenance-message.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { checkMaintenanceMessage, MAX_MESSAGE_LENGTH } from "./check-maintenance-message.mjs";

test("空（変更しない）と、200 文字ちょうどは通す", () => {
  assert.equal(checkMaintenanceMessage(""), null);
  assert.equal(checkMaintenanceMessage("あ".repeat(MAX_MESSAGE_LENGTH)), null);
});

test("201 文字は止める（絵文字は 1 文字として数える）", () => {
  assert.notEqual(checkMaintenanceMessage("あ".repeat(MAX_MESSAGE_LENGTH + 1)), null);
  assert.equal(checkMaintenanceMessage("😀".repeat(MAX_MESSAGE_LENGTH)), null);
  assert.notEqual(checkMaintenanceMessage("😀".repeat(MAX_MESSAGE_LENGTH + 1)), null);
});

test("改行（CR・LF・NEL・行区切り・段落区切り）を含むと止める", () => {
  const breaks = [0x0d, 0x0a, 0x85, 0x2028, 0x2029].map((cp) => String.fromCodePoint(cp));
  for (const b of breaks) assert.notEqual(checkMaintenanceMessage(`a${b}b`), null, b.codePointAt(0).toString(16));
  assert.notEqual(checkMaintenanceMessage("a\r\nb"), null);
});

test("記号を含む 1 行の文面は通す", () => {
  assert.equal(checkMaintenanceMessage("20:00 ごろ（\"試験\" & `x`）"), null);
});
