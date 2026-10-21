// 読み込み中の画面でもテーマを合わせる（保存済みの設定を先に反映する）
try {
    // 名前を変える前のキー（fara-pokemon-assistance.*）の設定・登録ポケモンは引き継がずに削除する
    localStorage.removeItem("fara-pokemon-assistance.settings");
    localStorage.removeItem("fara-pokemon-assistance.roster");
    var s = JSON.parse(localStorage.getItem("fara-pokemon-battle-assistant.settings") || "{}");
    if (s.Theme === "dark") document.documentElement.setAttribute("data-bs-theme", "dark");
} catch (e) { }
// サイドバーを隠す設定（PC）。描画の前に反映して、開いたときのちらつきを防ぐ（切り替えは page.js）
try {
    if (localStorage.getItem("fara-pokemon-battle-assistant.sidebar-hidden") === "1") document.documentElement.classList.add("sidebar-hidden");
} catch (e) { }
