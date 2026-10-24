// このサイト固有の起動時の処理（head で、描画の前に読む）。テーマ（ライト／ダーク）と PC のサイドバーを隠す設定の反映は、kit の theme-init.js が行う。
try {
    // 名前を変える前のキー（fara-pokemon-assistance.*）の設定・登録ポケモンは引き継がずに削除する
    localStorage.removeItem("fara-pokemon-assistance.settings");
    localStorage.removeItem("fara-pokemon-assistance.roster");
} catch (e) { }
// 検索付きセレクトで、選択中の候補が見えるところまでスクロールする
window.faraFocus = function (id) { var el = document.getElementById(id); if (el) el.focus({ preventScroll: true }); };
window.faraScrollIntoView = function (id) { var el = document.getElementById(id); if (el) el.scrollIntoView({ block: "nearest" }); };
