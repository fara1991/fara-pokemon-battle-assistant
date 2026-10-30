// deploy.yml / deploy-develop.yml の「Deploy」ステップの run を、実際に bash で動かして、wrangler deploy に渡る --var を固定するテスト。
//   node --test .github/scripts/deploy-workflow.test.mjs
// npx は偽物に差し替える（引数を 1 行ずつ出すだけ）。分岐の表は external/fara-blazor-component/docs/maintenance-cli.md の「ワークフローの雛形」。
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const read = (name) => readFileSync(join(root, ".github", "workflows", name), "utf8").replace(/\r\n/g, "\n");

/** 「- name: <name>」のステップの run（| のブロック）を取り出す。 */
function stepRun(yml, name) {
  const lines = yml.split("\n");
  const start = lines.findIndex((l) => l.trim() === `- name: ${name}`);
  assert.ok(start >= 0, `ステップ ${name} がある`);
  const runAt = lines.findIndex((l, i) => i > start && /^\s+run: \|\s*$/.test(l));
  assert.ok(runAt > start, `${name} に run: | がある`);
  const indent = lines[runAt + 1].match(/^\s*/)[0].length;
  const body = [];
  for (let i = runAt + 1; i < lines.length && (lines[i].trim() === "" || lines[i].match(/^\s*/)[0].length >= indent); i++) body.push(lines[i].slice(indent));
  return body.join("\n");
}

/** run を bash で動かし、npx に渡った引数を返す。 */
function deployArgs(run, env) {
  const dir = mkdtempSync(join(tmpdir(), "fake-npx-"));
  const fake = join(dir, "npx");
  writeFileSync(fake, '#!/bin/sh\nfor a in "$@"; do printf "%s\n" "$a"; done\n');
  chmodSync(fake, 0o755);
  const sep = process.platform === "win32" ? ";" : ":";
  const r = spawnSync("bash", ["-c", run], { env: { ...process.env, PATH: `${dir}${sep}${process.env.PATH}`, MAINTENANCE_MODE_INPUT: "", MAINTENANCE_MESSAGE_INPUT: "", SKIP_MAINTENANCE_CHECK: "", ...env }, encoding: "utf8" });
  assert.equal(r.status, 0, r.stderr);
  return r.stdout.split("\n").filter((x) => x !== "");
}

const production = stepRun(read("deploy.yml"), "Deploy");
const develop = stepRun(read("deploy-develop.yml"), "Deploy (develop)");

test("本番: push と、何も指定しない手動実行は、公開と同時に MAINTENANCE_MODE:true（メッセージは付けない）", () => {
  const expected = ["wrangler", "deploy", "--env=", "--var", "MAINTENANCE_MODE:true"];
  assert.deepEqual(deployArgs(production, {}), expected);
  assert.deepEqual(deployArgs(production, { SKIP_MAINTENANCE_CHECK: "false" }), expected);
});

test("本番: skip_maintenance_check だけなら --var を付けない（メンテナンス画面にしない）", () => {
  assert.deepEqual(deployArgs(production, { SKIP_MAINTENANCE_CHECK: "true" }), ["wrangler", "deploy", "--env="]);
});

test("本番: mode を指定したら、その値（true / notice / false）。message があれば付ける。skip があっても mode は付く", () => {
  for (const mode of ["true", "notice", "false"]) {
    assert.deepEqual(deployArgs(production, { MAINTENANCE_MODE_INPUT: mode }), ["wrangler", "deploy", "--env=", "--var", `MAINTENANCE_MODE:${mode}`]);
  }
  assert.deepEqual(deployArgs(production, { MAINTENANCE_MODE_INPUT: "notice", MAINTENANCE_MESSAGE_INPUT: "20:00 ごろ" }), ["wrangler", "deploy", "--env=", "--var", "MAINTENANCE_MODE:notice", "--var", "MAINTENANCE_MESSAGE:20:00 ごろ"]);
  assert.deepEqual(deployArgs(production, { MAINTENANCE_MODE_INPUT: "false", SKIP_MAINTENANCE_CHECK: "true" }), ["wrangler", "deploy", "--env=", "--var", "MAINTENANCE_MODE:false"]);
});

test("本番: message だけなら MAINTENANCE_MODE を渡さない（現在の値を保持。勝手にメンテナンス画面にしない）", () => {
  assert.deepEqual(deployArgs(production, { MAINTENANCE_MESSAGE_INPUT: "お知らせ" }), ["wrangler", "deploy", "--env=", "--var", "MAINTENANCE_MESSAGE:お知らせ"]);
  assert.deepEqual(deployArgs(production, { MAINTENANCE_MESSAGE_INPUT: "お知らせ", SKIP_MAINTENANCE_CHECK: "true" }), ["wrangler", "deploy", "--env=", "--var", "MAINTENANCE_MESSAGE:お知らせ"]);
});

test("確認用: push（入力が空）は --var を付けない。指定した分だけ付ける。記号を含む文面もそのまま 1 つの引数", () => {
  assert.deepEqual(deployArgs(develop, {}), ["wrangler", "deploy", "--env", "develop"]);
  assert.deepEqual(deployArgs(develop, { MAINTENANCE_MODE_INPUT: "notice" }), ["wrangler", "deploy", "--env", "develop", "--var", "MAINTENANCE_MODE:notice"]);
  const tricky = "a \"b\" $(x) `y` ; 'z' & 20:00";
  assert.deepEqual(deployArgs(develop, { MAINTENANCE_MESSAGE_INPUT: tricky }), ["wrangler", "deploy", "--env", "develop", "--var", `MAINTENANCE_MESSAGE:${tricky}`]);
});

test("ワークフロー: 入力・ゲートの条件と順序・環境変数経由・旧名を使わない", () => {
  const prod = read("deploy.yml");
  const dev = read("deploy-develop.yml");
  for (const yml of [prod, dev]) {
    assert.match(yml, /maintenance_mode:\n\s+description: .*\n\s+type: choice\n\s+options: \["", "true", "notice", "false"\]\n\s+default: ""/);
    assert.match(yml, /maintenance_message:\n\s+description: .*\n\s+type: string\n\s+default: ""/);
    assert.match(yml, /MAINTENANCE_MODE_INPUT: \$\{\{ inputs\.maintenance_mode \}\}/);
    assert.match(yml, /MAINTENANCE_MESSAGE_INPUT: \$\{\{ inputs\.maintenance_message \}\}/);
    assert.doesNotMatch(yml, /(?<![_A-Za-z])MAINTENANCE(?![_A-Za-z])/, "旧名 MAINTENANCE を使わない");
    assert.match(yml, /- name: Summary \(maintenance\)\n\s+if: \$\{\{ always\(\) && steps\.message\.outcome == 'success' \}\}/);
  }
  assert.match(prod, /WORKER_NAME: fara-pokemon\n/);
  assert.match(dev, /WORKER_NAME: fara-pokemon-dev\n/);
  assert.match(prod, /inputs:\n\s+skip_maintenance_check:/);
  const gate = prod.indexOf("- name: Require MAINTENANCE_MODE=notice");
  assert.ok(gate > prod.indexOf("- name: Check routes") && gate < prod.indexOf("- name: Deploy\n"), "ゲートは Check routes の後・Deploy の前");
  assert.match(prod, /if: \$\{\{ !contains\(fromJSON\('\["true","notice","false"\]'\), inputs\.maintenance_mode\) \}\}/);
  assert.match(prod, /SKIP_MAINTENANCE_CHECK: \$\{\{ inputs\.skip_maintenance_check \}\}/);
  assert.match(prod, /if: github\.ref == 'refs\/heads\/master'/);
  assert.match(prod, /environment:\n\s+name: production/);
  assert.doesNotMatch(dev, /require-maintenance/);
});

test("ワークフロー: 初回の公開（first_publish）だけが 404 の確認を通す。文面は --var の前に 200 文字・改行なしを確かめる", () => {
  const prod = read("deploy.yml");
  const dev = read("deploy-develop.yml");
  assert.match(prod, /first_publish:\n\s+description: .*\n\s+type: boolean\n\s+default: false/);
  assert.match(prod, /ALLOW_MISSING_WORKER: \$\{\{ inputs\.first_publish \}\}/);
  assert.doesNotMatch(dev, /ALLOW_MISSING_WORKER/);
  for (const yml of [prod, dev]) {
    // 空の文面は、スクリプトが通す。if で飛ばすと、Summary の条件（steps.message.outcome）が skipped になり、概要が書かれなくなる
    assert.match(yml, /- name: Check maintenance message\n\s+id: message\n\s+run: node \.github\/scripts\/check-maintenance-message\.mjs\n\s+env:\n\s+MAINTENANCE_MESSAGE_INPUT: \$\{\{ inputs\.maintenance_message \}\}/);
  }
  const check = prod.indexOf("- name: Check maintenance message");
  assert.ok(check > prod.indexOf("- name: Require MAINTENANCE_MODE=notice") && check < prod.indexOf("- name: Deploy\n"), "文面の確認は Deploy の前");
});

test("ワークフロー: 404 の説明（コメント）は今の挙動（止める。初回の公開だけ first_publish で通す）と合う", () => {
  const prod = read("deploy.yml");
  assert.doesNotMatch(prod, /Worker がまだ無い（初回の公開）ときは通す/);
  assert.match(prod, /Worker がまだ無い（404）ときも止める（初回の公開だけ first_publish で通す）/);
});
