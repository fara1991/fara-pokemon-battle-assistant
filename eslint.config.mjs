// JavaScript の静的解析（npm run lint）。共通の中身は kit の tools/lint/eslint.config.shared.mjs（docs/lint.md の 1）。
import shared, { createSharedConfig } from "./external/fara-blazor-component/tools/lint/eslint.config.shared.mjs";

export default [
  {
    // Windows と WSL の出力フォルダー（Directory.Build.props の obj.unix・bin.unix）と、公開の出力は対象外
    ignores: ["**/bin.unix/**", "**/obj.unix/**", "publish/**", "artifacts/**"],
  },
  // Node の JS（tools・.github/scripts・テスト など）
  ...shared,
  // Worker（worker/）: Node に、Cloudflare Workers の実行環境だけのグローバルを足す
  ...createSharedConfig({
    files: ["worker/**/*.js"],
    environments: ["node"],
    extraGlobals: { HTMLRewriter: "readonly" },
  }),
  // 画面の JS（Blazor の index.html から読む古い形の script。import も module もしない）
  ...createSharedConfig({
    files: ["src/FaraPokemonBattleAssistant.Web/wwwroot/js/**/*.js"],
    environments: ["browser"],
    sourceType: "script",
  }),
];
