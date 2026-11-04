// 入口ページの本文（#app の中の、検索エンジンが読める実テキスト）。pokemon 固有の文言と見た目の組み立て。
// 静的 HTML の作り方（head・区切り・robots.txt・sitemap.xml）は kit の build/build-pages.mjs（external/fara-blazor-component）が行い、ここでは本文の HTML だけを作る。
// Blazor が起動すると #app の中身ごと置き換わる。見た目は app.css の .entry-static（起動の前後でなるべく変えない）。

/** サイトの表示名（ヘッダーの帯に出す）。site.json の name と同じにする。 */
export const SITE_NAME = "Pokémon Battle Assistant";

/** HTML の文字として安全にする（属性の値・本文のどちらにも使う）。 */
export function escapeHtml(text) {
    return String(text)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

/** 1 ページ分の本文。主なページへのリンク（pages の一覧）を付ける。先頭の要素は <main class="entry-static">（site.json の bodyRootClass）。 */
export function buildBody(page, pages) {
    const paragraphs = page.paragraphs.map((text) => `<p>${escapeHtml(text)}</p>`).join("\n                ");
    const links = pages
        .map((p) => `<li><a href="${p.route === "" ? "/" : `/${p.route}`}">${escapeHtml(p.label)}</a></li>`)
        .join("");
    return [
        `<main class="entry-static" data-route="${page.route}">`,
        `        <div class="entry-static-bar">${escapeHtml(SITE_NAME)}</div>`,
        `        <div class="entry-static-body">`,
        `            <div class="entry-static-card">`,
        `                <h1>${escapeHtml(page.heading)}</h1>`,
        `                ${paragraphs}`,
        `            </div>`,
        `            <nav class="entry-static-nav" aria-label="ページ一覧"><ul role="list">${links}</ul></nav>`,
        `        </div>`,
        `        <noscript><p class="entry-static-note">この画面を使うには JavaScript を有効にしてください。</p></noscript>`,
        `    </main>`,
    ].join("\n");
}
