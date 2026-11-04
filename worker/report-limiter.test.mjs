// 報告の 1 日の送信回数の制限（REPORT_LIMITER・ReportRateLimiter）の配線の検査: wrangler.jsonc（本番と確認用の両方）と worker/index.js の export。
//   node --test worker/report-limiter.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { ReportRateLimiter } from "../external/fara-blazor-component/worker/report-rate-limit.js";

const readText = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
// wrangler.jsonc は JSONC（行全体の // コメントだけを使う）。コメントの行を外してから読む
const wranglerText = readText("wrangler.jsonc").split("\n").filter((line) => !/^\s*\/\//.test(line)).join("\n");
const wrangler = JSON.parse(wranglerText);
const environments = { production: wrangler, develop: wrangler.env.develop };

test("kit の ReportRateLimiter は、クラスとして読める（report-rate-limit.js の export）", () => {
  assert.equal(typeof ReportRateLimiter, "function");
});

test("worker/index.js は、ReportRateLimiter を kit から export する（Durable Object のクラスを Worker に公開）", () => {
  assert.match(readText("worker/index.js"), /^export \{ ReportRateLimiter \} from "\.\.\/external\/fara-blazor-component\/worker\/report-rate-limit\.js";$/m);
});

for (const [name, env] of Object.entries(environments)) {
  test(`wrangler.jsonc（${name}）: REPORT_LIMITER が ReportRateLimiter を指す`, () => {
    const bindings = env.durable_objects?.bindings ?? [];
    assert.deepEqual(bindings.filter((b) => b.name === "REPORT_LIMITER"), [{ name: "REPORT_LIMITER", class_name: "ReportRateLimiter" }]);
  });

  test(`wrangler.jsonc（${name}）: 既存のバインディング（ROSTER → RosterSlot）と migrations（v1）を残す`, () => {
    assert.ok(env.durable_objects.bindings.some((b) => b.name === "ROSTER" && b.class_name === "RosterSlot"));
    assert.ok(env.migrations.some((m) => m.tag === "v1" && m.new_sqlite_classes.includes("RosterSlot")));
  });

  test(`wrangler.jsonc（${name}）: migrations の tag は重複せず、ReportRateLimiter は 1 つの migration で作られる`, () => {
    const tags = env.migrations.map((m) => m.tag);
    assert.equal(new Set(tags).size, tags.length, `tag が重複: ${tags.join(", ")}`);
    const created = env.migrations.filter((m) => (m.new_sqlite_classes ?? []).includes("ReportRateLimiter"));
    assert.equal(created.length, 1);
    // 後から足した migration（v2 など）は、v1 より後ろに置く（tag の番号が大きいほど新しい）
    assert.equal(created[0], env.migrations.at(-1));
  });

  test(`wrangler.jsonc（${name}）: 束縛しているすべての Durable Object のクラスは、migrations で作られている`, () => {
    const created = new Set(env.migrations.flatMap((m) => m.new_sqlite_classes ?? []));
    for (const binding of env.durable_objects.bindings) {
      assert.ok(created.has(binding.class_name), `${binding.name} → ${binding.class_name} の migration が無い`);
    }
  });
}

test("wrangler.jsonc: 本番と確認用の migrations は同じ内容（確認用は環境ごとに書くため、ずれを防ぐ）", () => {
  assert.deepEqual(environments.develop.migrations, environments.production.migrations);
  assert.deepEqual(environments.develop.durable_objects.bindings, environments.production.durable_objects.bindings);
});
