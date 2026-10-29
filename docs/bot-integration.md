# FaraBotModerator に `!dmg` を組み込む

FaraBotModerator（WPF / TwitchLib）側に必要な変更は 3 箇所です。

## 1. Core を参照する

### 方法 A: 隣に clone して ProjectReference（開発中はこれが楽）

```
C:\repos\FaraBotModerator\
C:\repos\fara-pokemon-battle-assistant\
```

`FaraBotModerator.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\fara-pokemon-battle-assistant\src\FaraPokemonBattleAssistant.Core\FaraPokemonBattleAssistant.Core.csproj" />
</ItemGroup>
```

### 方法 B: NuGet パッケージ

`build.yml` が `FaraPokemonBattleAssistant.Core.<version>.nupkg` を Actions の成果物として出力します（develop / master への push と、手動実行（Run workflow）のときだけ。保存期間は 3 日なので、古いときは build.yml を手動実行して作り直してください）。
ローカルフォルダをパッケージソースに追加して参照してください。
パッケージは net10.0 と net8.0 の両方を含みます。bot の SDK が .NET 10 以上である必要があります（bot の `global.json` を `10.0.100` にしてください）。SDK 8 のままだと restore が NETSDK1045 で失敗します。bot が net10 に上がったら、Core の net8.0 を外します。

```powershell
dotnet nuget add source C:\packages --name local
dotnet add package FaraPokemonBattleAssistant.Core --source local
```

## 2. 起動時に DataCatalog と登録データを用意する

データは https://pokemon.fara-labs.com/api/game/ に公開された CSV（静的ファイル。認証なし。`HttpDataSource.GameDataPath`）を使い、1 日キャッシュします。取り直しに失敗したとき（回線断・タイムアウト）は古いキャッシュで続行します。登録したポケモン・チームはローカルの JSON に保存します。

```csharp
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;

public sealed class PokemonAssistService
{
    private const string DataUrl = RemoteRosterStore.ProductionBaseUrl + HttpDataSource.GameDataPath; // https://pokemon.fara-labs.com/api/game/

    private readonly PokeCommand _command;

    public PokemonAssistService()
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaraBotModerator");
        var source = new CachingDataSource(new HttpDataSource(new HttpClient(), DataUrl),
            Path.Combine(appDir, "pokemon-data"), TimeSpan.FromDays(1));
        var roster = new RosterRepository(new FileRosterStore(Path.Combine(appDir, "pokemon-roster.json")));
        // 第 2 引数: 長時間起動しても週次更新のデータを取り込めるよう、1 日ごとに読み直す
        _command = new PokeCommand(new DataCatalog(source, TimeSpan.FromDays(1)), roster);
    }

    /// <summary>"!pokech ..." などのメッセージかどうか。</summary>
    public async Task<bool> HandlesAsync(string message) => await _command.MatchAsync(message) is not null;

    /// <summary>チャットに返す 1 行を返す。</summary>
    public async Task<string> HandleAsync(string message, bool canEdit)
    {
        var result = await _command.ExecuteAsync(message, new PokeCommandOptions
        {
            Format = registeredFormat ?? Models.BattleFormat.Singles,   // 登録した形式（format set）。未登録ならシングル。形式を書かないコマンドはこの形式で動く
            FormatIsRegistered = registeredFormat is not null,          // format の表示（登録している／既定）に使う
            SaveFormatAsync = (format, ct) => SaveRegisteredFormatAsync(format, ct),   // format set / format clear の保存先（null は解除）。BOT の設定ファイルなど
            AllowMutations = canEdit,               // add -m / rm -m / add -t など登録系・format set / clear は配信者本人とモデレーターだけ
        });
        return result.Message;
    }
}
```

### チーム番号は形式ごと・形式の指定（`-f`）と登録（`format`）

チーム番号は **シングルとダブルで別々** に数えます（シングルのチーム1 とダブルのチーム1 は別のチーム）。使用中のチームも形式ごとに 1 つです。

- 形式は、チームのコマンドに `-f シングル` / `-f single` / `-f ダブル` / `-f double`（`--format` でも可）を付けて指定します。`dmg` / `scan` / `faster` も同じオプションで、計算の形式（と、使う使用中チームの形式）を決めます
- 書かなかったときは、`PokeCommandOptions.Format`（`format set` で登録した形式。未登録ならシングル）で動きます。登録は BOT 全体で 1 つの状態で、`format set シングル|ダブル` で登録、`format clear` で解除（既定のシングルに戻る）、`format` で確認します
- `format set` / `format clear` の保存は `PokeCommandOptions.SaveFormatAsync` が呼ばれます（`null` を渡されたら登録の解除）。ここで設定ファイルなどに保存し、**次のメッセージから** `Format` / `FormatIsRegistered` にその値を渡してください。`SaveFormatAsync` を設定していなければ、`format set` は「保存できません」と返します
- 以前の BOT の「対戦形式」設定（`pokemon.battleFormat`）は、この「登録した形式」と同じものとして扱います（使用率・ダブルの計算の形式も、これを使います）
- チームの登録内容の表示（`RosterRepository` の `Teams` を読む画面）は、`Team.Format` と `Team.Number` の組でチームを区別し、使用中は `DataSetRoster.ActiveNumber(format)` で読みます（`ActiveTeam` は古いデータ用で、新しい版は使いません）
- 形式ごとにチーム番号を数える前に保存されたデータは、読み込むときに、すべてシングルのチームとして扱います（使用中のチームはシングルの使用中）。ダブルの構成で使っていたデータは、`set -t 番号 ダブル` でダブルへ移してください

### 登録データをサイトと共有する（データ共有）

サイトの「データ共有」ページ（`/data-share`）で **データ共有を始める** を押すとアクセスキー（43 文字）が作られ、登録データがクラウド（`https://pokemon.fara-labs.com/api/my/pokemon`・`/api/my/team`）に保存されます。
配信用BOT（Auto Bot Settings の Game タブ → ポケモンシリーズ の「アクセスキー」）に同じアクセスキーを設定し、`FileRosterStore` の代わりに `RemoteRosterStore` を使うと、サイトと配信用BOTが同じデータを読み書きします（データはクラウドの 1 か所だけにあり、コピーを同期するのではありません）。
画面上の呼び方は「データ共有」「アクセスキー」ですが、コード上の名前（`syncKey` 引数、サイトの localStorage キー `fara-pokemon-battle-assistant.syncKey` など）は互換性のためそのままです。

```csharp
// syncKey: 配信用BOTの Game タブ（ポケモンシリーズ）の「アクセスキー」欄に貼り付けた値（パスワードと同じ扱い。ログに出さない）
IRosterStore store = RemoteRosterStore.IsValidKey(syncKey)
    ? new RemoteRosterStore(httpClient, RemoteRosterStore.ProductionBaseUrl, syncKey)
    : new FileRosterStore(Path.Combine(appDir, "pokemon-roster.json"));
var roster = new RosterRepository(store);
```

- `RemoteRosterStore(HttpClient http, string apiBaseUrl, string syncKey)`。`apiBaseUrl` は本番 `RemoteRosterStore.ProductionBaseUrl`（`https://pokemon.fara-labs.com/`）、確認用 `RemoteRosterStore.DevelopBaseUrl`（`https://pokemon-dev.fara-labs.com/`。データは本番と別）。キーの形式が違うと `ArgumentException`
- `RosterRepository` は読み書きのたびにクラウドから読み直し（ポケモンとチームの 2 回の GET。間に更新が入って版が食い違ったら読み直す）、他の場所（サイト）の更新と競合したら読み直して最大 3 回やり直します（`RosterRepository.MaxConflictRetries`）
- 保存は変えた部分だけです。登録・削除（`add -m` / `rm -m`）は `PUT /api/my/pokemon`、チーム・使用チームの変更（`add -t` / `rm -t` / `use -t` / `unuse -t`）は `PUT /api/my/team` を呼びます（`IRosterStore.SavePartAsync`）。ポケモンを消したときにチームから外す・0 体のチームを消す・使用中を解除する処理はサーバーが同じ処理の中で行います
- `IRosterStore` を包むクラスを作るときは、`SavePartAsync` も中の保存先にそのまま渡してください（`SaveAsync` は登録データ全体の保存で、クラウドではデータの移し替え用です）
- クラウドに届かないときは `RosterUnavailableException`（`Reason`: `Network` / `Unauthorized`＝キーが正しくない / `KeyRetired`＝サイトでキーが作り直された / `TooLarge` / `Rejected` / `Server` / `InvalidResponse`）。`Message` はそのまま表示できる日本語で、キーは含みません。やり直しても競合が続くと `RosterConflictException`
- `PokeCommand.ExecuteAsync` / `TryExecuteAsync` はこれらの例外を受け止めて「失敗しました: アクセスキーが正しくありません」のような結果を返します（チャットにそのまま返せます）
- サイトでキーを作り直すと（`DELETE /api/my/access-key`）、古いキーは `KeyRetired` になります。新しいキーを設定し直してください
- キーの作り直し（サイト側の処理）の API: `RemoteRosterStore.DeleteIfHistoryUnchangedAsync(expect, ct)` は従来どおり（履歴の状態だけを確かめて消す）。登録データの版も確かめるときは、新しいオーバーロード `DeleteIfUnchangedAsync(expect, long? expectRevision, ct)` を使います（`expectRevision` が `null` のときは版を見ない）。違えば `HistoryChangedException` で、`CurrentRevision`（今の版。サーバーが返さなかったときは `null`）と `Current`（今の履歴の状態）が入ります。ボットから作り直しを行う場合は、登録データを読んだ版（`RemoteRosterStore.Revision`）を `expectRevision` に渡してください
- **アクセスキーの作り直しは `RosterRotation`（`Core/Roster/RosterRotation.cs`）を使ってください**（サイトと同じ処理。版が `null` の 409 も、登録データを写し直してから消す。写し直しは最大 `RosterRotation.MaxRetries`（5）回）。手順は 2 段階です。
  1. `RosterRotation.PrepareAsync(HttpClient http, string apiBaseUrl, string oldKey, CancellationToken ct = default)` → `RosterRotationPlan`（登録データ・相手チーム・履歴を新しいキーへ写す。古いキーは消さない。失敗したら新しいキーを消して例外 `RosterUnavailableException`）
  2. 新しいキー（`plan.NewKey`）を保存したあとに、`RosterRotation.FinishAsync(http, apiBaseUrl, plan, ct)` → `RosterRotationResult`（`OldKeyDeleted` が false のときは、`Stop` が `RetriesExhausted`（上限まで変わり続けた）か `Failed`（消す処理が失敗した）か `RewriteFailed`（古いキーの変更を新しいキーへ写している途中で失敗した。新しいキーは不完全なので使わない。古いキーは残っています）。古いキーが `410`（すでに使えない）で返ったときは、消えているものとして `OldKeyDeleted` が true になります
  - `RewriteFailed` のときは、**新しいキーを使わず、古いキーへ戻してください**（古いキーは消していないので、データは古いキーに残っています）。戻したあとに `RosterRotation.DiscardAsync(http, apiBaseUrl, newKey, ct)` で、使われない新しいキーの置き場を消します（`bool` は消せたか。消せなくても、使われないだけなので害はありません）。キーの保存先を古いキーへ戻せなかったとき（ブラウザの保存などで）は、新しいキーを使い続けるしかないので、その置き場は消さずに、利用者に「古いキーで参加し直す」よう案内してください。どの場合も、配信用 BOT や他の端末には、新しいキーを入れないでください
  - 新しいキーを保存できなかったら、同じく `RosterRotation.DiscardAsync(http, apiBaseUrl, newKey, ct)` で新しいキーの置き場を消します（`bool` は消せたか）
  - `apiBaseUrl` は `RemoteRosterStore.ProductionBaseUrl` / `DevelopBaseUrl`。`RosterRotationPlan` は `OldKey`・`NewKey` を持ちますが、`ToString()` には出しません（キーはパスワードと同じ扱い）
  - ボットの作り直し処理（`PokemonSyncKeyController` の `PrepareRotateAsync` / `FinishRotateAsync`）は、この API に置き換えられます（ボット側の作業）。`RemoteRosterStore.DeleteIfUnchangedAsync` は、これまでどおり直接使えます
- 確認用サイト（`RemoteRosterStore.DevelopBaseUrl`）は Cloudflare Access の内側なので、`/api/*`（登録データの `/api/my/*` とゲームデータの `/api/game/*`）を Access の **Bypass** にしておく必要があります

## 3. チャットメッセージをさばく

`TwitchClientController.TwitchClientOnMessageReceived` の先頭に追加します。

```csharp
private readonly PokemonAssistService _pokemon = new();

private async void TwitchClientOnMessageReceived(object? sender, OnMessageReceivedArgs e)
{
    var text = e.ChatMessage.Message.Trim();
    if (await _pokemon.HandlesAsync(text))
    {
        // 登録・削除・使用チームの変更は配信者本人とモデレーターだけ（視聴者には開放しない）
        var canEdit = e.ChatMessage.IsBroadcaster || e.ChatMessage.IsModerator;
        SendMessage(e.ChatMessage.Channel, await _pokemon.HandleAsync(text, canEdit));
        return;
    }

    // 既存の処理 …
}
```

## 動作確認

配信用BOTに組み込む前に、同じ文字列をターミナルやブラウザで試せます。

```bash
dotnet run --project src/FaraPokemonBattleAssistant.Cli -- "!pokech dmg イエッサン♂ ワイドフォース メガリザードンX サイコフィールド"
dotnet run --project src/FaraPokemonBattleAssistant.Cli -- --data https://pokemon.fara-labs.com/api/game/ "!pokesv faster ガブリアス ハバタクカミ"
```

ブラウザ: https://pokemon.fara-labs.com/chat-commands

## 注意

- Twitch は `/` 始まりのメッセージを自身のコマンドとして扱うため、`!` 始まりにしています。
- 1 メッセージ 500 文字制限に合わせて、結果は `DamageCommandOptions.MaxLength`（既定 480）で切り詰めます。
- 連投対策（同一ユーザーのクールダウンなど）は配信用BOT側で行ってください。計算自体は数ミリ秒です。
- `!poke help` で使えるコマンド一覧が返ります（`!poke` はチャンピオンズ扱い）。
- 登録・チームは短い形の `-m` / `-t` が主です。長い形の `--my` / `--team` も同じ意味で使えます。例（短い形の直後に同じ意味の長い形を並べています）:
  ```
  !pokech add -m ガブリアス 4 32 0 0 0 30 ようき
  !pokech add --my ガブリアス 4 32 0 0 0 30 ようき
  !pokech use -t 1
  !pokech use --team 1
  ```
- 返信の性格は、性格名ではなく上がる・下がる能力で出ます（例: `イエッサン(C252 C↑A↓ …)`）。補正のない性格（まじめ・てれや など）は「補正無し」と出ます（例: `ガブリアス(H32 補正無し …)`、`ls -m` なら `#1 ガブリアス(補正無し)`）。入力は今までどおり性格名です。
- `AllowMutations = false` のとき、`add` / `rm` / `use -t`（pokemon・team とも） は「配信者・モデレーターのみ行えます」と返し、閲覧・計算系はそのまま動きます。
