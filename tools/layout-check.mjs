// レイアウトの自動計測（npm run layout-check）。publish:web の出力（publish/wwwroot）を kit の簡易サーバーで配り、
// layout-check.config.mjs のページを、既定の幅ごとに開いて測る。公開はしない。
// 終了コード: 0 違反なし / 1 違反あり / 2 計測できなかった（先に npm run publish:web。ブラウザ・設定の誤り）。
import { access } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { startStaticServer } from "../external/fara-blazor-component/tools/layout-check/serve-static.mjs";
import {
  LayoutCheckConfigError,
  LayoutCheckRunError,
  RULE_NAMES,
  formatViolation,
  runLayoutCheck,
} from "../external/fara-blazor-component/tools/layout-check/index.mjs";

const SITE_ROOT = resolve("publish/wwwroot");
const CONFIG_PATH = resolve("layout-check.config.mjs");
const START_PORT = 8820;

async function main() {
  await access(resolve(SITE_ROOT, "index.html")).catch(() => {
    throw new LayoutCheckConfigError(`計測する出力が無い: ${SITE_ROOT}（先に npm run publish:web）`);
  });
  const { default: config } = await import(pathToFileURL(CONFIG_PATH).href);
  const server = await startStaticServer(SITE_ROOT, START_PORT);
  try {
    const { violations, runs, config: normalized } = await runLayoutCheck({ ...config, baseURL: server.origin });
    const enabled = RULE_NAMES.filter((name) => normalized.rules[name]);
    console.log(`レイアウト計測: ${normalized.pages.length} ページ × ${normalized.viewports.length} 幅（${normalized.viewports.join(", ")}px）= ${runs} 回`);
    console.log(`ルール: ${enabled.join(", ")}`);
    if (violations.length === 0) {
      console.log("違反なし");
      return 0;
    }
    for (const v of violations) console.log(formatViolation(v));
    console.log(`違反 ${violations.length} 件`);
    return 1;
  } finally {
    await server.close();
  }
}

try {
  process.exitCode = await main();
} catch (error) {
  if (error instanceof LayoutCheckConfigError || error instanceof LayoutCheckRunError) {
    console.error(`エラー: ${error.message}`);
  } else {
    console.error(`予期しないエラー: ${error?.stack ?? error}`);
  }
  process.exitCode = 2;
}
