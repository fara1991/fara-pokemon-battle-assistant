# Pokémon Battle Assistant

ポケモン対戦アシスタント（Pokémon Battle Assistant）。ダメージ計算・素早さ比較・育成済みポケモンとチームの管理・タイプ相性を、**ブラウザ**（Cloudflare Workers）と **Twitch チャットのコマンド**（FaraBotModerator）の両方から使えるようにするプロジェクトです。

- Web: https://pokemon.fara-labs.com/
- チャット: `!dmg イエッサン♂ ワイドフォース メガリザードンX` → 1 行目に入力した内容、2 行目以降に結果が返ります（`イエッサン(C252 C↑A↓ こだわりメガネ) ワイドフォース → メガリザードンX(H252 A↑C↓ リザードナイトX)` の次の行に `結果 61.6〜72.9% (114〜135) 確定2発 [効果抜群(2倍)]`）。`scan` と `faster` も、1 行目が入力した内容、2 行目以降が結果です。

## 構成

```
fara-pokemon-battle-assistant/
├── src/
│   ├── FaraPokemonBattleAssistant.Core/   計算ロジック・CSV 読み込み・名前解決・登録/チーム・チャットコマンド（クラスライブラリ）
│   ├── FaraPokemonBattleAssistant.Web/    Blazor WebAssembly（Cloudflare Workers に公開）
│   │   └── wwwroot/api/game/         ★ ゲームデータ CSV。Web も配信用BOTもこのファイルを読む（公開サイトでは /api/game/... で配られる）
│   └── FaraPokemonBattleAssistant.Cli/    コマンドをターミナルで試す / 配信用BOT組み込み前の動作確認用
├── external/fara-site-kit/           共通部品（git submodule。Worker の共有 JS: 報告の受け口・メンテナンス表示）
├── tests/FaraPokemonBattleAssistant.Core.Tests/
├── tools/
│   ├── sync_data.py                  公開データとポケモン HOME からデータを再生成するスクリプト
│   ├── datasets.json                 データセット定義（世代・バージョングループ・HOME 設定）
│   ├── item_effects.csv              持ち物の効果（手入力）
│   └── ability_names_ja.csv          PokeAPI に日本語名がまだ無い特性の補完表（手入力。sync_data.py が適用、英語のまま残ると警告）
└── .github/workflows/
    ├── build.yml                     ビルド・テスト・NuGet パッケージ化
    ├── deploy.yml                    master へ push されたら Cloudflare Workers（本番）に公開
    ├── deploy-develop.yml            develop へ push されたら確認用サイトに公開
    └── update-data.yml               毎週月曜にデータを再生成して develop にコミット（変更があれば deploy-develop.yml で確認用に公開。本番へは develop → master の PR で反映）
```

サーバーは存在しません。計算は Web ではブラウザ内、配信用BOTでは FaraBotModerator のプロセス内で行います。
配信用BOTは `Core` を参照し、データは公開サイト上の CSV（週 1 回自動更新）を取得してローカルにキャッシュします。

## データ

| データセット | 内容 | 元データ |
|---|---|---|
| `Gen1`・`Gen2` | 赤・緑〜金・銀・クリスタル。その世代の時点のタイプ・種族値・特性・技・タイプ相性。**参照のみ**（ポケモン一覧・わざ一覧・タイプ相性表。計算機・素早さ比較・登録・チャットの計算は非対応） | 公開データ（`*_past.csv`・`move_changelog.csv`） |
| `Gen3`〜`Gen7` | ルビー・サファイア〜サン・ムーン・USUM。上と同じくその世代の時点のデータで、計算機・素早さ比較にも対応（第6〜7世代はメガシンカ、第7世代は Z 技） | 同上 |
| `Gen8` | ソード・シールド（鎧の孤島・冠の雪原含む）。ダイマックス対応 | 公開データ |
| `Gen9` | スカーレット・バイオレット（DLC 含む）。テラスタル対応 | 公開データ + ポケモン HOME ランクバトル使用率 |
| `Champions` | ポケモンチャンピオンズ（メガシンカ・メガストーン含む） | 公開データ（`version_group = champions`）。使用率は準備中（下の「使用率について」） |

- ポケモン・技・持ち物・覚える技・タイプ相性は [公開されている CSV ダンプ](https://github.com/PokeAPI/pokeapi/tree/master/data/v2/csv) から生成します。REST API は叩きません（週 1 回のバルク取得のみ）。
- ID は元データと同じです。フォルム違い（メガリザードンX = 10034、イエッサン(♀) = 10186 など）も元データの ID をそのまま使います。性別でフォルムが分かれる種族は基本フォルムにも `(♂)` を付けます（`イエッサン(♂)`。チャットでは `イエッサン` だけでも ♂ になります）。
- チャンピオンズの収録ポケモンは **チャンピオンズ図鑑（pokedex 36、231 種）** とそのメガシンカ・性別差・種族値やタイプの違うフォルムです。チャンピオンズ用の技データがまだ無いポケモンは SV の技で補い、SV にも居ないポケモン（ガラルのバリヤード等）は剣盾 → USUM の順に、そのポケモンのデータがある最初の世代の技で補います（`tools/datasets.json` の `learnset_fallback_version_groups` / `learnset_fallback_older_version_groups`）。技データが空のポケモンは作りません（空だと「全技を覚える」扱いになるため）。`python tools/verify_learnsets.py` で PokeAPI の CSV と突き合わせて検証できます。図鑑に無いポケモンは表示しません。
- 第9世代の `usage_*.csv` は HOME のランクバトル最新シーズンから生成します。
- **使用率について（チャンピオンズ）**: チャンピオンズの使用率は公式 HOME から取れないため、現在は**使用率なし**です（表示・並び替えをしません）。第 9 世代の使用率で代用するのはやめました（誤解を招くため）。取得元は championsbattledata.com を想定し、定期取得する仕組みを検討中です。注記（確認中）: 利用ルールは確認中のため、ここでは断定しません。取得を始めるときは、利用ルールを確認したうえで、その条件（クレジット表記など）に従います。
  - 復活のしかた: 取得できた使用率の `usage_*.csv` を `Champions/` に置けば、`usage_fallback` なしでそのまま表示・並び替えに使います（既存の仕組み）。第 9 世代の使用率で代用する場合は、`tools/datasets.json` の Champions の `usage_fallback` を `"Gen9"` に戻して `sync_data.py` を実行します（`datasets.csv` の UsageFallback 列も同時に変わります）。
- 第1〜7世代（`tools/datasets.json` の `historical: true`）は、元データの `pokemon_types_past` / `pokemon_stats_past` / `pokemon_abilities_past` / `type_efficacy_past` / `move_changelog` で、その世代の時点の値にします。第1世代の「とくしゅ」は特攻・特防の両方に入れ、第3世代までの技は技のタイプで物理・特殊を決めます。特性は第3世代から、隠れ特性は第5世代からです。メガシンカ・リージョンフォームなどは、そのゲームまでに登場していたものだけを入れます。
- `datasets.json` の `calc: "Reference"` のゲームは参照のみです（計算機などでは案内を出します）。
- 第3〜7世代のダメージ計算（`LegacyDamageCalculator`）は、その世代の補正の順番・丸め（第3〜4世代は補正ごとの切り捨て・タイプ一致の位置、急所 2 倍 / 1.5 倍、スキン系 1.3 倍、フィールド 1.5 倍、まひ 1/4、やけど 1/8 など）で行います。公開されているダメージ計算ツール（@smogon/calc）の結果と、`tools/calc_vectors` で作った条件（`tests/.../Data/calc_vectors.json`。第3〜7世代に加え、ソード・シールド / スカーレット・バイオレット / チャンピオンズの条件も含む）で一致を確かめています（第3世代のウェザーボールは、天候があると威力ではなくダメージが 2 倍になり、変わったタイプで物理・特殊も決まります。天候がないときはツールが特殊技として計算しますが、ゲームではノーマルタイプの物理技なので、その条件だけ比較から除いています）。ちからずく・すてみの対象の技は `move_lists.js` で作る `Data/move_lists.json` で全部の技を照合します。今のゲームの計算を意図して変えたときは `Data/calc_snapshot.txt` を作り直し、変わったケースを確認します。
- 手動で更新する場合: `python tools/sync_data.py`（HOME を飛ばすなら `--skip-home`）。
- 公開サイトでは `src/FaraPokemonBattleAssistant.Web/wwwroot/api/game/` がそのまま `https://pokemon.fara-labs.com/api/game/`（確認用は `https://pokemon-dev.fara-labs.com/api/game/`）で配られます（`datasets.csv`・`natures.csv`・`updated.txt`・`<ゲーム>/pokemon.csv` など。認証なしの静的ファイル。通常は Worker が、そのまま返します。メンテナンス中（`MAINTENANCE=true`）だけ `503`。そのとき配信用BOTは、取り直しに失敗した扱いで、古いキャッシュで続行します）。配信用BOT（`HttpDataSource` + `CachingDataSource`）もこの URL から読みます。無いファイルはサイトの SPA 設定のため `404` ではなく `index.html`（`200`、`text/html`）が返るので、`HttpDataSource` は HTML を「無い」として扱います。

## チャットコマンド

接頭辞でデータセットを選びます: **`!pokech`** = ポケモンチャンピオンズ、**`!pokesv`** = スカーレット・バイオレット、**`!pokess`** = ソード・シールド（`!poke` はチャンピオンズ扱い）。
`!poke help` で使えるコマンドの一覧が返ります。登録・削除・使用チームの変更は配信者本人とモデレーターだけに許可する想定です（`PokeCommandOptions.AllowMutations`）。
登録データは配信用BOTなら PC 上の JSON、ブラウザなら localStorage に保存され、データセットごとに独立しています。
サイトの「データ共有」ページ（`/data-share`。育成ポケモンは `/my-pokemon`、自分チームは `/my-team`。以前の `/data-sync`・`/sync`・`/pokemon`・`/roster`・`/team`・`/my-teams` などは新しい URL に転送）でアクセスキーを作ると、登録データをクラウドに保存し、同じアクセスキーを入れた別のブラウザ・端末や配信用BOTが同じデータを読み書きできます（データ共有中はクラウドだけが正で、ブラウザにはアクセスキーだけを保存します。配信用BOT側は `RemoteRosterStore`。[docs/bot-integration.md](docs/bot-integration.md)）。

### 育成済みポケモンの登録（`-m` / `--my`）

対象はオプションで指定します。育成ポケモンは `-m`（長い形は `--my`。以前の `-p` / `--pokemon` は廃止）、自分チームは `-t`（`--team`）、相手チームは `-r`（`--rival`）で、サブコマンドの直後に置きます。長い形は短い形と同じ意味です（例は下）。`add pokemon` のような単語の形は使えません（`add` だけを打つと、`-m` か `-t` を付けるよう案内します）。全角スペースや全角の英数字・記号（`ｄｍｇ`・`－ｐ`・`＋１` など）は半角として読み取ります（`PokeCommand` / `DamageCommand` の入口で `CommandText.Normalize`）。

例（短い形の直後に、同じ意味の長い形を並べています）:

```
!pokech add -m ガブリアス 4 32 0 0 0 30 ようき
!pokech add --my ガブリアス 4 32 0 0 0 30 ようき
!pokech ls -m
!pokech ls --my
!pokech ls -m ガブリアス
!pokech use -t 1
!pokech use --team 1
```

| コマンド | 内容 |
|---|---|
| `add -m 名前 H A B C D S 性格 [特性] [技]` | 登録（振り方は `H32B32C2` のように書いた能力だけでもよく、書いていない能力は 0）。チャンピオンズはポイント（各 0〜32・合計 66、1 pt = 実数値 +1）、SV/剣盾は努力値（各 0〜252・合計 510） |
| `ls -m [名前]` | 一覧（登録 ID・ポケモン・性格）。名前を付けるとそのポケモンだけ |
| `detail -m ID` | 詳細（性格・振り・特性・実数値・所属チーム） |
| `rm -m ID` | 削除（チームからも外れる） |
| `set -m ID または名前 [H32 A10 …] [性格] [特性] [技]` | 登録済みのポケモンの、書いた項目だけ更新（チーム・持ち物はそのまま。技は書いたときだけ、書いた技に置き換わる。振り方を書いたときは振り方全体の置き換えで、書いていない能力は 0） |

### 自分チーム（`-t` / `--team`）

| コマンド | 内容 |
|---|---|
| `add -t 番号 ID [持ち物]` | チームに追加（6 体まで）。同じ ID を再指定すると持ち物を更新 |
| `rm -t 番号 名前` / `rm -t 番号` | チームから外す / チーム削除。登録の削除などで 0 体になったチームは自動で削除 |
| `use -t 番号` | 使用チームにする。以降の dmg / scan / faster はこのチームの構成（努力値・性格・特性・持ち物）で計算 |
| `unuse -t` | 使用チームを解除する |
| `ls -t` / `detail -t 番号` | チーム一覧 / 詳細 |

### 相手チーム（`-r` / `--rival`）と履歴（`-h` / `--history`）

対戦中の相手を登録します（同じポケモンは 1 体、6 体まで）。名前だけ登録しておき、分かった部分から埋めていきます。登録データと同じ保存先（クラウド・ローカル）に入るので、サイトとBOTで共有されます。サイトの「相手チーム」ページ（`/my-rival`）でも同じ操作ができます。

| コマンド | 内容 |
|---|---|
| `add -r 名前 [名前 …] [振り方] [性格] [特性]` | 対戦中の相手を登録。名前だけでもよく、複数の名前をまとめて登録できる（選出画面の 6 体など）。振り方は `H32 D10` か 6 つの数字（`H A B C D S`）、性格は名前か `B↑A↓`。同じポケモンをもう一度指定すると更新 |
| `set -r 名前 [振り方] [性格] [特性] [技 …] [選出]` | 登録済みの相手の、書いた項目だけ更新（まだなら登録）。書いた能力のポイントだけ更新し、書いていない能力は今のまま（`scan` の推定も消さない）。相手が使った技（その相手が覚える技）を書くと 4 つまで覚えて、履歴に残す。持ち物（確定したもの）を書くと保存され、`dmg`・`scan`・履歴に使う。`選出` で選出に、`set -r メガリザードンX` のようにメガシンカした姿の名前で姿を変える |
| `scan 攻撃側 技±ランク 相手 受けたダメージ` | 受けた（与えた）ダメージから相手の防御の振り方と性格補正を推定し、相手に反映する（登録していなければ自動で登録）。`93` なら実数、`45%` なら割合（H も推定）。同じ相手を続けて推定すると、前の推定と重なる範囲に絞り込む（矛盾するときは反映しない）。`防:たべのこし` のように持ち物を書くと、その持ち物で推定して、相手に保存する |
| `ls -r` / `detail -r 名前` | 対戦中の相手の一覧 / 1 体の詳細（推定の根拠つき） |
| `rm -r 名前` | 登録ミスなどの取り消し（履歴には残さない） |
| `clear -r` | 対戦中の相手の登録を全員解除する（履歴には残さない） |
| `end [メモ]` | 試合を終える。対戦中の相手を全員（名前だけの相手も）履歴に移し、対戦中の相手を空にする。選出した相手・見えた技・ポイント・性格・特性・推定も一緒に残る |
| `ls -h [名前]` / `detail -h ID` / `rm -h ID` | 試合の履歴（新しい順。名前を付けるとそのポケモンの履歴）/ 1 試合の詳細 / 削除。履歴の件数は、クラウドではゲームごとに 10,000 件まで、ブラウザ（localStorage）では 2,000 件までです（ファイル保存の配信用BOTは上限なし）。上限に達したら `end` が断られるので、サイトの相手チーム画面で古い履歴を削除してください |

登録した相手は、`dmg` の防御側に使われます。1 つに決まっていれば、その振り方で 1 行。範囲のときは「B（特殊技なら D）最小想定」「最大想定」の 2 行（耐久が最も低い想定と最も高い想定）。分かっていない部分（HP・防御）は、これまでどおり全振りで計算します。以前の `calc` は `scan` に、`foe` は `-r` に変わりました。

### 計算・ヘルプ

| コマンド | 内容 |
|---|---|
| `dmg 攻撃側 技±ランク 防御側 [オプション]` | 通常と急所のダメージ・確定数。先頭が数字だけなら、育成ポケモンの登録 ID として読みます（`#3` でも可）。攻撃側は登録済みのポケモンだけ使えます（使用チームにいればその構成、いなければ登録の構成。持ち物は括弧内に表示）。同じポケモンを複数登録しているときは `#登録ID` で指定します（名前だけだと一覧を出して案内。使用チームにその 1 体だけがいればそれを使います）。相手の特性が不明で特性によって結果が変わる場合は「特性別:」で特性ごとに表示。例: `!pokech dmg ガブリアス じしん+1 リザードン` |
| `faster 自分 相手 [+1 晴れ]` | 素早さ比較。自分は登録済みのポケモンだけ使えます（同じポケモンを複数登録しているときは `#登録ID` で指定。実数値は使用チームの構成を優先。スカーフ・ランク・天候特性反映）。相手は 無振り / 準速 / 最速 / スカーフ最速 |
| `help` | 使えるコマンドの一覧 |

`dmg` のオプション（順不同）:

| 書き方 | 意味 |
|---|---|
| `A252` `C252` `S4` / `H252` `B4` `D252` | 攻撃側 / 防御側の努力値（チャンピオンズはポイント） |
| 性格名、持ち物名、特性名 | 性格・特性は持てる側・自然な側に付く。持ち物名は防御側（対戦中の相手）に付く（攻撃側の持ち物は育成ポケモン・自分チームの登録で決まる）。`攻:` / `防:` で明示（例: `防:ずぶとい` `防:持ち物なし`。`攻撃:` `防御:` `こうげき:` `ぼうぎょ:` `A:` `B:` `C:` `D:` や全角の「：」も可。英字は攻撃側・防御側の指定だけで、ランクなどが効く能力は技の分類で決まります） |
| `+1`〜`+6` / `-1`〜`-6` | 攻撃側の攻撃ランク（技名に直接 `じしん+2` とも書ける） |
| `急所` `ダブル` `シングル` `無振り` `持ち物なし` | そのまま |
| `壁` | 防御側の場にリフレクター / ひかりのかべ（オーロラベール）。0.5 倍、ダブルは 2732/4096。急所・すりぬけは無視 |
| `テラス` / `テラスほのお` / `防:テラスみず` | テラスタル（SV） |
| `ダイマ` / `防:ダイマ` | ダイマックス（剣盾）。HP 2 倍、技はダイマックス技の威力 |
| `晴れ` `雨` `砂嵐` `雪` / `エレキフィールド` `グラスフィールド` `サイコフィールド` `ミストフィールド` | 天候 / フィールド（略称の `サイコ` なども可） |
| `やけど` `まひ`（既定は攻撃側）/ `どく` `もうどく` `ねむり` `こおり`（既定は防御側） | 状態異常。やけどは物理 0.5 倍（こんじょう・からげんきは除外）、どく系は毎ターンの定数ダメージを確定数に織り込む（ポイズンヒール・マジックガードは除く）。からげんき・たたりめ・ベノムショック・きつけ・めざましビンタ・どくぼうそう・ねつぼうそうなども反映。`faster` では `まひ` が相手、`自:まひ` が自分 |

指定しなかった項目は「防御側は H 全振り、性格・特性は使用率 1 位（無ければ第 1 特性）」で埋め（使用率のないゲームは性格は補正なし・特性は第 1 特性）、結果の括弧内に明記します。攻撃側は登録済みのポケモンなので、持ち物も含めて、育成ポケモン・自分チームの登録で決まります（持ち物名を攻撃側に付けることはできません）。防御側（対戦中の相手）の持ち物は、書いたもの（`たべのこし` など。前置きなしでも防御側に付きます）・相手チームに保存した持ち物・メガシンカ後のメガストーンだけで、不明なら持ち物なしとして計算し、返信にも出しません。
名前はカタカナ・ひらがな・前方一致・略称（`メガリザX`、`ガブ` など）で解決し、曖昧なときは候補を返します。

ブラウザの「チャットコマンド」ページで同じ文字列を試せます。ターミナルなら:

```bash
dotnet run --project src/FaraPokemonBattleAssistant.Cli -- "!pokech dmg イエッサン♂ ワイドフォース メガリザードンX サイコフィールド"
```

## FaraBotModerator への組み込み

[docs/bot-integration.md](docs/bot-integration.md) を参照してください。要点:

1. `FaraBotModerator.csproj` から `FaraPokemonBattleAssistant.Core` を参照する（隣に clone して `ProjectReference`、または CI の成果物 `.nupkg`）。
2. 起動時に `DataCatalog` を 1 つ作る。データ元は公開サイトの URL（`https://pokemon.fara-labs.com/api/game/`）+ ローカルキャッシュ。
3. `OnMessageReceived` で `!pokech` / `!pokesv` / `!pokess` を見つけたら `PokeCommand.ExecuteAsync` の結果をそのまま `SendMessage`。登録・削除は配信者本人とモデレーターだけに許可する。

## クローンと submodule（共通部品 fara-site-kit）

Worker の共有 JS（`external/fara-site-kit/worker/`）は [fara-site-kit](https://github.com/fara1991/fara-site-kit) を git submodule で取り込んでいます。

```bash
git clone --recurse-submodules https://github.com/fara1991/fara-pokemon-battle-assistant.git
# 取得済みのクローンでは
git submodule update --init
```

submodule が無いと、`npm run check`・`npm run test:report`・`npm run test:maintenance`・`wrangler deploy --dry-run` が失敗します（`worker/index.js` が kit の import を読むため）。
kit のどのコミットを使うかは、このリポジトリの `external/fara-site-kit` が指すコミットで決まります（更新は PR で行う）。
注意: 報告フォームの画面側は `external/fara-site-kit/src/FaraSiteKit.Razor/wwwroot/js/report.js`（Turnstile）と `ReportFormView`、受け口の Worker 側は `external/fara-site-kit/worker/report.js` です（別のファイル）。

## 開発

```bash
dotnet build FaraPokemonBattleAssistant.sln
dotnet test tests/FaraPokemonBattleAssistant.Core.Tests
dotnet run --project src/FaraPokemonBattleAssistant.Web      # https://localhost:52017
npm ci && npm run preview                                # Cloudflare と同じ動きで http://localhost:8787
npm run check                                            # dotnet publish + wrangler deploy --dry-run（本番・develop）
npm run test:worker                                      # データ共有 API（worker/index.js）を wrangler dev で確認
```

### サイト内検索（ヘッダーの検索欄）

ヘッダー（PC）の検索欄で、ページ・項目・用語・チャットコマンドと、いま選んでいるゲームのポケモン・わざ・特性を探せます（Ctrl+K か `/` で欄に移ります）。結果には、一致した場所（ページ › 項目）と、その前後の文章を出します。

- ポケモン・わざ・特性は、実行時に、選んでいるゲームのデータから探します（`Services/SearchService.cs`）
- ページ・項目・用語・コマンドは、`src/FaraPokemonBattleAssistant.Web/wwwroot/search-index.json` から探します。これは各ページの Razor から `tools/gen-search-index.mjs` が作ります。**ページの文言・見出しを変えたら、`npm run gen:search` で作り直して、一緒にコミットしてください**（CI の `node tools/gen-search-index.mjs --check` が、合っているかを確かめます）
- ページには、ひらがなの読みを付けています（`tools/gen-search-index.mjs` の `PAGE_READINGS`。ローマ字で探すため。例: 権利表記 → けんりひょうき）。**ページを足したら、読みも足してください**（無いと生成が失敗します）

### 計算で考慮しているもの

- 実数値（種族値・個体値・努力値・性格 1.1/0.9 倍）、ランク補正、急所（スナイパー対応）、タイプ一致、タイプ相性、ダブルの複数対象 0.75 倍、五捨五超入
- 持ち物: こだわり系・いのちのたま・たつじんのおび・タイプ強化・プレート・ちからのハチマキ・ものしりメガネ・パンチグローブ・とつげきチョッキ・しんかのきせき・でんきだま・ふといホネ・ふうせん・ブーストエナジー
- テラスタル: 防御タイプの置き換え、タイプ一致 1.5/2.0 倍（てきおうりょく 2.0/2.25 倍）、威力 60 保証
- ダイマックス: HP 2 倍、ダイマックス技の威力表（かくとう・どくは低めの表）
- サイコショック・サイコブレイク・しんぴのつるぎは相手の「防御」で計算
- 天候: 晴れ・雨のほのお/みず補正、砂嵐のいわ特防 1.5 倍、雪のこおり防御 1.5 倍
- フィールド: エレキ・グラス・サイコの 1.3 倍、ミストのドラゴン半減、グラスのじしん系半減、ワイドフォース（1.5 倍＋全体化）、ライジングボルト、サイコブレイド
- 特性: `src/FaraPokemonBattleAssistant.Core/Battle/DamageCalculator.cs` の `AbilityEffects` と各 `*Modifier` を参照。
  スキン系、てきおうりょく、ちからもち/ヨガパワー、はりきり、ごりむちゅう、テクニシャン、ちからずく、てつのこぶし、がんじょうあご、メガランチャー、かたいツメ、パンクロック、すなのちから、はがねつかい/はがねのせいしん、トランジスタ、りゅうのあぎと、いわはこび、すいほう、サンパワー、ひひいろのこどう、ハドロンエンジン、こだいかっせい/クォークチャージ、いろめがね、ブレインフォース、スナイパー、かたやぶり系、
  防御側: ふゆう・ちょすい・よびみず・かんそうはだ・もらいび・こんがりボディ・ちくでん・ひらいしん・でんきエンジン・そうしょく・ぼうおん・ぼうだん・ふしぎなまもり・だっぴ…の無効化、あついしぼう、たいねつ、もふもふ、きよめのしお、ファーコート、くさのけがわ、マルチスケイル/ファントムガード（満タン想定）、フィルター/ハードロック/プリズムアーマー、こおりのりんぷん、てんねん
- 壁（リフレクター / ひかりのかべ / オーロラベール）
- HP に依存する特性: もうか・しんりょく・げきりゅう・むしのしらせ（HP 1/3 以下）、マルチスケイル / ファントムガード（満タン時の 1 発目のみ）
- 半減きのみ（1 発目のみ 0.5 倍、ホズのみはノーマル技）
- 乱数 16 通りをすべて計算し、確定数（乱数 N 発の確率は残り HP の分布を 1 発ずつ進めて厳密に算出）。どく・もうどく・やけど・くろいヘドロの定数ダメージ、オボンのみ・オレンのみ・フィラのみ等の回復きのみ、たべのこし・くろいヘドロ・グラスフィールドの回復を含む

### まだ無いもの

威力が状況で変わる技（ジャイロボール・はたきおとす等）の個別処理、連続技の回数指定。

## 公開（Cloudflare Workers）

`master` への push（PR のマージ含む）で `deploy.yml` が https://pokemon.fara-labs.com/ に公開します（Worker `fara-pokemon`。設定は `wrangler.jsonc`）。サイト本体は静的アセットで、`/`（ホームの本文入りの `home.html`。`worker/home.js`）と `/api/*`（登録データの API `/api/my/*`・報告・メンテナンスの状態・ゲームデータ `/api/game/*`）だけ Worker スクリプト（`worker/index.js`）を通ります（`run_worker_first: ["/", "/api/*"]`）。存在しない URL は `index.html`（汎用の title と読み込み表示だけ。ホームの本文は入れない）を返します。入口ページの静的 HTML（`tools/build-entry-pages.mjs` が作る。`battle-terms.html` など）と `_headers`（CSP）は、`publish:web` と `deploy*.yml` の publish の後に作ります（`--dir` は publish の出力の `wwwroot`）。作り方は fara-site-kit（`external/fara-site-kit/build`・`csp`）、サイトの設定は `tools/site.json`、入口ページの本文は `tools/entry-body.mjs`、文言は `tools/entry-pages.json`。ゲームデータは、通常は静的アセットをそのまま返し、メンテナンス中（Worker の変数 `MAINTENANCE=true`）だけ `503` にします。
手動で公開し直すときは **Actions → Deploy to Cloudflare Workers → Run workflow**。

`develop` への push（PR のマージ含む）では `deploy-develop.yml` が確認用の https://pokemon-dev.fara-labs.com/ に公開します（Worker `fara-pokemon-dev`。検索エンジンには載らないよう `X-Robots-Tag: noindex` を付けています）。本番には影響しません。
確認用サイトは Cloudflare Access で自分だけが見られるようにしています（Zero Trust → Access → Applications。許可したメールアドレスに届くコードでログイン）。

初回だけ、リポジトリの **Settings → Secrets and variables → Actions** に次を登録してください（値は fara-portfolio-site と同じものを使えます）。

| タブ | 名前 | 値 |
|---|---|---|
| Secrets | `CLOUDFLARE_API_TOKEN` | Cloudflare の API トークン（テンプレート Edit Cloudflare Workers、ゾーン `fara-labs.com`） |
| Variables | `CLOUDFLARE_ACCOUNT_ID` | Cloudflare のアカウント ID |

### 登録データの共有 API

登録ポケモン・チームを、アクセスキーで読み書きするための API です（`worker/index.js`）。サイトの「データ共有」ページの「API で使うには」にも同じ説明があります。サイトも API の使い手の 1 つで、配信用BOT（FaraBotModerator の `RemoteRosterStore`）はその一例です。
アクセスキーごとに SQLite ストレージの Durable Object（`RosterSlot`）を 1 つ持ち、登録データ（`RosterState` の JSON）を 1 つの文書として保存しています。API はその一部（ポケモン / チーム / 相手チーム）を読み書きします。試合の履歴は文書には入れず、同じ Durable Object の SQLite の表に 1 試合 1 行で持ちます（履歴が増えても 256 KB の上限に当たらないように）。本番と確認用は Worker が別なので、データも別々です。

登録データはゲーム（データセットのキー: `Champions`・`Gen9` など。大文字小文字は区別しない）ごとに分かれているので、本文の `dataSets` はゲームのキーごとのオブジェクトです。
ポケモン・チームの中身は `RegisteredPokemon` / `Team` の JSON（`Id`・`PokemonName`・`Slots`・`EntryId` など先頭が大文字の項目）をそのまま使います。

| メソッド | 内容 |
|---|---|
| `GET /api/my/pokemon` | `200 {"revision": n, "dataSets": {"Champions": {"nextId": n, "pokemon": [...]}, ...}}`（データが無ければ `"dataSets": {}`） |
| `PUT /api/my/pokemon` | 本文 `{"baseRevision": n, "dataSets": {"Champions": {"pokemon": [...], "nextId": n}}}`（`nextId` は省略可）。書いたゲームのポケモンだけを置き換え、**同じ処理の中で**、消えたポケモンをそのゲームのすべてのチームから外し、0 体になったチームを消し、使用中のチームが消えたら使用中を解除します。成功で `200 {"revision": n+1}` |
| `GET /api/my/team` | `200 {"revision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n または null}, ...}}` |
| `PUT /api/my/team` | 本文 `{"baseRevision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n または null}}}`。書いたゲームのチームと使用中チームだけを置き換えます。登録に無いポケモン・7 体以上・0 体のチーム・同じチーム番号や同じポケモンの重複・無いチームを使用中にする、は `400`（`message` に理由）で、何も保存しません。成功で `200 {"revision": n+1}` |
| `GET /api/my/rival` | `200 {"revision": n, "dataSets": {"Champions": {"nextId": n, "rivals": [...], "nextMatchId": n, "history": [...]}, ...}}`（対戦中の相手チームと、終わった試合の履歴）。`?historyLimit=n` で履歴を直近 n 件（古い順）にできます（0 で空）。指定しないと直近 200 件（古い版のクライアント用。全件にすると、書き戻しの本文が 256 KB を超えるため） |
| `PUT /api/my/rival` | 本文 `{"baseRevision": n, "dataSets": {"Champions": {"rivals": [...], "nextId"?: n, "history"?: [...], "nextMatchId"?: n}}}`。書いたゲームの相手チームを置き換えます（ID は使い回しません）。`history` は省略でき、あっても**置き換えず**、「次の Id 以降の、まだ無い試合」だけを足します（古い版のクライアントが書き戻しても、消した試合が復活せず、履歴が消えません。**古い版のクライアントは履歴を消せません**）。足せなかった試合（上限・次の Id から 1,000 以上離れた Id）は、応答の `historyIgnored`（件数）で分かります。別の端末が同じ Id を先に割り当てていた試合は足されません。`409` は `GET` と同じ形で現在の内容を返します（`?historyLimit=n` も使えます） |
| `GET /api/my/history?dataSet=Champions` | `200 {"total": 件数, "items": [試合…新しい順], "hasMore": bool}`。`dataSet` を付けない `GET /api/my/history` は、履歴のあるゲームの一覧（`{"dataSets": [{"dataSet": "champions", "total": n, "nextId": n}]}`。キーの作り直しで履歴を写すときに使います）。`limit`（既定 50・最大 200）と `before`（その Id より前から）でページ送り。`&id=n` で 1 試合（`{"total", "item"}`、無ければ `404`） |
| `POST /api/my/history` | 本文 `{"dataSet": "Champions", "match": {"Date"?, "Memo"?, "Foes": [...]}}`。Id はサーバーが割り当てます。`201 {"id": n, "total": 件数}`。**ゲームごとに 10,000 件まで**で、超えると `409`（`error` は `history_limit`）。アクセスキー 1 つあたりの履歴の合計は 64 MB まで、履歴を持てるゲームは 16 までで、これらも超えると `409`。1 試合は 64 KB まで。`match` に `ClientId`（1〜100 文字の文字列）を付けると、同じゲームに同じ `ClientId` の試合がすでにあれば、足さずに `200 {"id": 既存の Id, "total": n, "duplicate": true}` を返します（上限に達していても。応答が届かずにやり直しても二重に記録されません）。`match` の代わりに `"matches": [...]`（100 件まで。データの移し替え用）を送ると、古い順にまとめて足します（`201 {"ids": [...], "total": n}`。1 つでも上限を超えるなら何も足さずに `409`）。なお `409` は、revision の衝突（`{"revision", "dataSets"}`）と `history_limit`（`{"error", "message"}`）の 2 つの形があるので、`error` があるかで見分けてください |
| `DELETE /api/my/access-key?expect=ゲーム:件数:次のId,…` | `expect` を付けると、履歴の状態（上の一覧の `dataSet:total:nextId`）が一致するときだけ消します（`204`）。違えば消さずに `409 {"error": "changed", "dataSets": [今の状態]}`。キーの作り直しで、写している間に足された試合を取りこぼさないためです（付けなければ、これまで通り無条件に消します） |
| `DELETE /api/my/history?dataSet=Champions&id=n` | 1 試合を消す（`204`、無ければ `404`）。`&keepLatest=n` なら新しい n 件を残して古い試合を消す（`200 {"deleted": n, "total": 件数}`） |
| `DELETE /api/my/access-key` | このアクセスキーを使えなくし、その置き場（履歴も）を消す（キーの作り直し・データ共有の解除用）。`204`。以後そのキーはすべての API で `410` |

- `revision` は登録データ全体で 1 つです（どの PUT でも増えます。履歴の追加・削除では増えません）。`baseRevision` が現在と違えば `409 {"revision": 現在, "dataSets": そのリソースの現在の内容}` なので、読み直してからやり直してください。両方を読むときは 2 回の GET の `revision` が同じことを確かめ、違えば読み直します
- 書かなかったゲームは変わりません。登録 ID は使い回さず、`nextId` は「保存していた値・送った `nextId`・一番大きい `Id` + 1」の最大になります
- 本文と保存後の登録データ全体（履歴を除く）の上限は 256 KB で、超えると `413`
- 1 年間アクセスが無いアクセスキーの置き場は、自動的に消えます（アクセスすると延長。更新は 1 日に 1 回まで。この機能より前からある置き場は、最初にアクセスされた時から数えます）。`DELETE /api/my/access-key` で消したキーは、そのまま `410` です
- 認証は `Authorization: Bearer <アクセスキー>`。アクセスキーは 256 bit の乱数を base64url（パディングなし・43 文字）にしたもので、形式が違えば `401`。キーはクライアントが作るもので、作成・登録の API はありません（新しいキーの置き場は空で、`revision` は 0）
- キーの作り直し・データ共有の開始では、新しいキーの空の置き場に `PUT /api/my/pokemon`（`baseRevision` 0）→ `PUT /api/my/team`（`baseRevision` 1）→ `PUT /api/my/rival`（相手チーム。`history` は付けない）の順で移し、履歴は古い順に `POST /api/my/history` で足し（Id は振り直されます）、新しいキーに切り替えてから、古いキーには `DELETE /api/my/access-key?expect=…` を呼びます（履歴も消えます。写している間に履歴が足されていたら `409` になるので、足された分だけ新しいキーへ写して、もう一度呼びます）。新しいキーは移し終わるまで誰も使わないので、途中の状態は見えません（途中で失敗したら新しいキーは使わずに捨てます）
- サーバーはキーを保存せず、`SHA-256(キー)` を Durable Object の名前に使います。キーはログ・URL・エラーメッセージに出しません
- すべての応答に `Cache-Control: no-store`。CORS ヘッダーは付けません（サイトは同じオリジン、配信用BOTはブラウザではないため）
- 以前の `/api/roster`・`/api/my-pokemon`・`/api/my-team`・`/api/access-key` はありません（`404`）。ゲームデータは API ではなく静的ファイル（`/api/game/...`。上の「データ」を参照）で、以前の `/data/...` もありません
- 確認用サイト（Cloudflare Access の内側）では、`/api/*`（登録データの API `/api/my/*` とゲームデータ `/api/game/*`）を Access の **Bypass** にしないと配信用BOTから使えません（登録データはキーで認証するので Access は不要）

Cloudflare の DNS に `pokemon` / `pokemon-dev` のレコード（GitHub Pages 時代の CNAME など）が残っていると、カスタムドメインの作成で失敗します。削除してから再実行してください。

## ライセンス

MIT License。ポケモンのデータは [公開データベース](https://pokeapi.co/)（BSD-3）および株式会社ポケモンの公開情報に基づきます。
