# CLAUDE.md

Pokémon Battle Assistant（本番 https://pokemon.fara-labs.com/ ・確認用 https://pokemon-dev.fara-labs.com/ ）。ダメージ計算・素早さ比較・登録とチームの管理を、ブラウザ（.NET 10 / Blazor WebAssembly + MudBlazor を Cloudflare Workers に公開）と Twitch チャットのコマンド（FaraBotModerator が `Core` を参照）の両方で使う。サーバーは無い。詳細は README.md、配信用 BOT への組み込みは docs/bot-integration.md。

## ブランチ運用

- 作業ブランチは `develop` から作成し、PR は `develop` 向けに作る。develop へは、CI と差分のレビューのあと Claude がマージしてよい
- `master` へは `develop` からのみマージする。master へのマージはユーザーが行う。Claude は develop → master の PR を作り、本文に確認ポイントをまとめる
- master への push で `deploy.yml` が本番に、develop への push で `deploy-develop.yml` が確認用に公開する。`update-data.yml` は毎週データを作り直して develop にコミットする

## 構成メモ

- `src/FaraPokemonBattleAssistant.Core`: 計算・CSV の読み込み・名前の解決・登録とチーム・チャットコマンド（`Battle/`・`Csv/`・`Data/`・`Roster/`・`Text/`）。Web と配信用 BOT の両方が使うので、公開の型を変えるときは BOT 側も考える
- `src/FaraPokemonBattleAssistant.Web`: Blazor WASM（`Components/`・`Services/`）。ゲームデータの CSV は `wwwroot/api/game/<データセット>/`（公開サイトで `/api/game/...` として配り、BOT もここを読む）
- `src/FaraPokemonBattleAssistant.Cli`: コマンドをターミナルで試す
- データセットは Gen1〜Gen9・Champions（`tools/datasets.json`）。CSV は `python tools/sync_data.py` で作り直す（手で直さない。手入力の表は `tools/item_effects.csv`・`ability_names_ja.csv` など）
- 計算を意図して変えたら、`tests/FaraPokemonBattleAssistant.Core.Tests/Data/calc_snapshot.txt` を作り直し、変わったケースを確かめる。@smogon/calc との一致は `calc_vectors.json` で確かめている
- ページの文言・見出しを変えたら `npm run gen:search` で `wwwroot/search-index.json` を作り直してコミットする（CI が `--check` で確かめる）。ページを足したら `tools/gen-search-index.mjs` の `PAGE_READINGS` に読みも足す
- Worker は `worker/index.js`（`/`・`/api/*` だけ通す。`run_worker_first`）。共通部品は `external/fara-blazor-component`（submodule、タグ v1.0.1 に固定。上げるときは PR）
- 画面の文言は日本語

## 確認

```bash
dotnet build FaraPokemonBattleAssistant.sln
dotnet test
npm run check         # dotnet publish + 入口ページの作成 + wrangler deploy --dry-run（本番・develop）
npm run lint
npm run publish:web && npm run layout-check
npm run test:worker   # ほかの Node のテストは package.json の test:*
```

- 静的解析（ESLint・C# のアナライザーのラチェット）とレイアウト計測の除外の決まりは、README の「静的チェック」
