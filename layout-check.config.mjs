// レイアウトの自動計測（npm run layout-check）の設定。ルールと書き方は external/fara-site-kit/docs/layout-check.md。
// baseURL は tools/layout-check.mjs が、配信を始めたポートに差し替える（ここの値は使われない）。
const SMOOTH_SCROLL =
  "Bootstrap の :root（smooth スクロール）で、Tab で入った直後の計測がスクロールの途中の位置になる（kit の計測は待たない）。" +
  "スクロールが終わると画面内に入ることを、Tab の 400ms 後の位置で確かめた（確認用の計測）。";

export default {
  baseURL: "http://127.0.0.1:8820",
  pages: [
    { path: "/", name: "ホーム（アプリの殻）" },
    { path: "/damage-calculator", name: "ダメージ計算" },
    { path: "/pokemon-list", name: "ポケモン一覧（/pokedex の転送先）" },
    { path: "/data-share", name: "データ共有" },
    { path: "/report", name: "問い合わせ" },
    { path: "/privacy", name: "プライバシー" },
    { path: "/type-chart", name: "タイプ相性" },
    { path: "/speed-comparison", name: "素早さ比較" },
    { path: "/my-team", name: "自分のチーム" },
    { path: "/my-pokemon", name: "育成済みポケモン" },
    // 存在しないパスは計測できない（kit は HTTP 200 のページだけを測る）。404 の見た目は、配られる 404.html で測る
    { path: "/404.html", name: "404 ページ" },
  ],
  readySelector: ".mud-layout",
  menuEsc: { buttonSelector: ".fsk-menu-toggle" },
  exclusions: [
    // --- kit の部品（SiteFooter・SocialLinks・skip link）。直すには kit を変える（kit の課題として残す）
    {
      rule: "tap-target",
      selector: "a.fsk-footer-social",
      reason: "kit のフッターの SNS アイコン（見た目 22px）。押せる範囲を広げると見た目が変わるため、kit で直す",
    },
    {
      rule: "tap-target",
      selector: ".fsk-footer a:not(.fsk-footer-social)",
      reason: "kit のフッターの「Fara」など（高さは 44px、幅は 25px）。押せる範囲は kit で直す",
    },
    {
      rule: "no-offscreen-focus",
      selector: ".fsk-footer a",
      reason: SMOOTH_SCROLL,
    },
    {
      rule: "no-offscreen-focus",
      selector: "a.fsk-skip-link",
      reason: "kit の skip link。フォーカスすると画面の上に出る（top の移動に transition がある）。計測の瞬間は移動の途中",
    },
    // --- 片付けの対象外（見た目は直す）
    {
      rule: "focus-visible",
      selector: "input.form-control",
      reason: "入力欄（Bootstrap の .form-control）。枠と影を 0.15s かけて変えるため、計測の瞬間は変化の前。400ms 後は影 4px と紫の枠になる（タイプ相性の入力欄で確認）",
    },
    {
      rule: "tap-target",
      selector: "input.mud-switch-input",
      reason: "MudBlazor の切り替え。入力は見た目の上に透明に重なる。中心は押せるが、判定の端（±21.5px）が隣（ヘッダーの下の本文）に当たる",
    },
    {
      rule: "tap-target",
      selector: "button#scroll-top.scroll-top",
      reason: "スクロールすると出る「いちばん上へ」（表示前は opacity 0・pointer-events: none）。表示中は 46px。隠れている間は下の要素に当たる",
    },
    {
      rule: "focus-visible",
      selector: ".home-tool-link",
      page: "/",
      reason: "ホームのカード（.home-tool-link::after がカード全体を覆う）。見える表示は ::after の outline（app.css の .home-tools .home-tool-link:focus-visible::after）。計測は要素自身の outline しか見ない",
    },
    {
      rule: "no-offscreen-focus",
      selector: ".home-tool-link",
      page: "/",
      reason: SMOOTH_SCROLL,
    },
    {
      rule: "no-offscreen-focus",
      selector: "a.mud-button-root.mud-button",
      page: "/",
      reason: SMOOTH_SCROLL,
    },
    {
      rule: "no-offscreen-focus",
      selector: "button.mud-button-root.mud-icon-button",
      page: "/",
      reason: SMOOTH_SCROLL,
    },
    {
      rule: "no-offscreen-focus",
      selector: ".dex-clear",
      page: "/pokemon-list",
      reason: SMOOTH_SCROLL,
    },
    {
      rule: "no-offscreen-focus",
      selector: "a[href='report']",
      page: "/privacy",
      reason: SMOOTH_SCROLL,
    },
    {
      rule: "tap-target",
      selector: "div.mud-card-content button.mud-button-root",
      page: "/report",
      reason: "送信ボタンは、入力が空のあいだ無効（pointer-events: none）。押せない状態を測るため除外。入力すると有効になる",
    },
    {
      rule: "tap-target",
      selector: ".sync-join button.mud-button-root",
      page: "/data-share",
      reason: "「アクセスキーを入力して共有」。アクセスキーの欄が空のあいだ無効（pointer-events: none）。押せない状態を測るため除外。入力すると有効になる",
    },
  ],
};
