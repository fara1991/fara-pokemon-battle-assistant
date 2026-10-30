using System.Net;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Tests;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace FaraPokemonBattleAssistant.Web.Tests;

/// <summary>
/// 写している間の変化を取りこぼさないこと（キーの作り直し・データ共有の開始・別のタブでの共有の停止）。
/// 偽サーバーの Override で「古いキーを消す直前に、別の端末・別のタブが書いた」状況を再現する。
/// </summary>
public sealed class CloudSyncRaceTests : IDisposable
{
    private readonly TestResources _owned = new();

    public void Dispose() => _owned.Dispose();

    private const string Base = "https://pokemon.example/";
    private const string Ds = "Gen9";
    private const string LocalKey = "fara-pokemon-battle-assistant.roster";

    private static string Pokemon(string name) => RosterRepository.Serialize(new RosterState
    {
        DataSets = { [Ds] = new DataSetRoster { NextId = 2, Pokemon = { new RegisteredPokemon { Id = 1, PokemonId = 6, PokemonName = name } } } },
    });

    private static RosterState WithRival() => new()
    {
        DataSets =
        {
            [Ds] = new DataSetRoster
            {
                NextId = 2,
                Pokemon = { new RegisteredPokemon { Id = 1, PokemonId = 6, PokemonName = "リザードン" } },
                NextRivalId = 2,
                Rivals = { new RivalPokemon { Id = 1, PokemonName = "相手" } },
            },
        },
    };

    private CloudSync NewSync(FakeJsRuntime js, HttpMessageHandler server) =>
        new(js, _owned.Own(new HttpClient(server)), Base, new ServiceCollection().AddSingleton(new RosterRepository(new MemoryRosterStore())).BuildServiceProvider());

    /// <summary>登録データを持つ古いキーで、データ共有を始めている状態を作る。</summary>
    private async Task<(CloudSync Sync, FakeJsRuntime Js, KeyRoutedServer Server, string OldKey)> JoinedAsync(RosterState initial)
    {
        var js = new FakeJsRuntime();
        var server = _owned.Own(new KeyRoutedServer());
        var key = RemoteRosterStore.GenerateKey();
        using var http = new HttpClient(server);
        await new RemoteRosterStore(http, Base, key).SaveAsync(RosterRepository.Serialize(initial));
        var sync = NewSync(js, server);
        Assert.Equal(JoinResult.Joined, await sync.JoinAsync(key));
        return (sync, js, server, key);
    }

    private static bool IsRetire(HttpRequestMessage request) =>
        request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/api/my/access-key";

    [Fact]
    public async Task Rotation_copies_a_pokemon_written_to_the_old_key_before_the_old_key_is_deleted()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        var written = false;
        old.Override = request =>
        {
            // 古いキーを消す直前に、別の端末がポケモンを登録する（履歴は変わらない・版だけが変わる）
            if (!written && IsRetire(request))
            {
                written = true;
                old.WriteFromElsewhere(state =>
                {
                    var set = state.For(Ds);
                    set.Pokemon.Add(new RegisteredPokemon { Id = 2, PokemonId = 445, PokemonName = "ガブリアス" });
                    set.NextId = 3;
                });
            }
            return null;
        };

        Assert.True((await sync.RotateAsync()).OldKeyDeleted);

        Assert.True(written);
        Assert.True(old.Deleted);
        Assert.Contains(server.For(sync.Key!).State.For(Ds).Pokemon, p => p.PokemonName == "ガブリアス");
    }

    [Fact]
    public async Task Rotation_clears_a_rival_removed_on_the_old_key_while_it_was_copying()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        var cleared = false;
        old.Override = request =>
        {
            if (!cleared && IsRetire(request))
            {
                cleared = true;
                old.WriteFromElsewhere(state => state.For(Ds).Rivals.Clear());   // 写している間に、相手チームを消した
            }
            return null;
        };

        Assert.True((await sync.RotateAsync()).OldKeyDeleted);

        Assert.True(cleared);
        Assert.Empty(server.For(sync.Key!).State.For(Ds).Rivals);   // 消した相手チームが、新しいキーに残らない
    }

    [Fact]
    public async Task Rotation_gives_up_after_its_retries_and_keeps_the_old_key_while_it_keeps_changing()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        var deletes = 0;
        old.Override = request =>
        {
            if (IsRetire(request))
            {
                deletes++;
                // 削除のたびに、別の端末が書き込む（いつまでも変わり続ける）
                old.WriteFromElsewhere(state => state.For(Ds).Pokemon.Add(new RegisteredPokemon { Id = 100 + deletes, PokemonId = 445, PokemonName = $"ポケ{deletes}" }));
            }
            return null;
        };

        Assert.False((await sync.RotateAsync()).OldKeyDeleted);

        Assert.Equal(5, deletes);                                  // 上限（5 回）で止まる
        Assert.False(old.Deleted);                                 // 古いキーは消さない
        Assert.NotEqual(oldKey, sync.Key);                         // 新しいキーへは切り替わっている
        Assert.Contains(server.For(sync.Key!).State.For(Ds).Pokemon, p => p.PokemonName == "ポケ5");   // 最後に読んだ登録は写っている
    }

    [Fact]
    public async Task Deleting_with_a_stale_revision_reports_the_current_revision_and_deletes_nothing()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        using var http = new HttpClient(server);
        var remote = new RemoteRosterStore(http, Base, oldKey);
        await remote.LoadAsync();
        var read = remote.Revision!.Value;
        server.For(oldKey).WriteFromElsewhere(state => state.For(Ds).NextId++);   // 読んだあとに、別の端末が登録を書いた

        var changed = await Assert.ThrowsAsync<HistoryChangedException>(() => remote.DeleteIfUnchangedAsync(new Dictionary<string, HistoryTotal>(), read));

        Assert.Equal(read + 1, changed.CurrentRevision);
        Assert.False(server.For(oldKey).Deleted);
    }

    [Fact]
    public async Task Rotation_that_cannot_reread_the_old_key_stops_without_deleting_it()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        var retired = false;
        old.Override = request =>
        {
            if (IsRetire(request))
            {
                retired = true;
                old.WriteFromElsewhere(state => state.For(Ds).Pokemon.Add(new RegisteredPokemon { Id = 9, PokemonId = 445, PokemonName = "ポケ9" }));
                return null;
            }
            // 消せなかったあとの、写し直しのための読み直し（GET）が失敗する
            return retired && request.Method == HttpMethod.Get ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}") : null;
        };

        var result = await sync.RotateAsync();

        Assert.False(result.OldKeyDeleted);
        Assert.Equal(RosterRotationStop.RewriteFailed, result.Stop);   // 写し直しの読み直しに失敗 = 古いキーへ戻す
        Assert.False(old.Deleted);
        Assert.Equal(oldKey, sync.Key);
        Assert.Equal(RotationKeyState.OldKeyRestored, result.KeyState);
    }

    /// <summary>
    /// 写し直しの途中で失敗させる。古いキーを消す直前に別の端末が書き（写し直しに入る）、新しいキーへの書き直し（PUT）を失敗させる。
    /// 作った新しいキーは <paramref name="created"/> に入る（作り直しの最初のコピーは成功させる）。
    /// </summary>
    /// <param name="onRewriteFails">書き直しが失敗するときに呼ぶ（保存の失敗を重ねるなど）。</param>
    /// <param name="failNewKeyDelete">true なら、新しいキーの置き場を消す処理も失敗させる（捨てられない）。</param>
    private static void ArrangeRewriteFailure(KeyRoutedServer server, FakeRosterServer old, List<string> created,
        Action? onRewriteFails = null, bool failNewKeyDelete = false)
    {
        var rewriting = false;
        server.OnCreate = (key, bucket) =>
        {
            created.Add(key);
            bucket.Override = request =>
            {
                if (rewriting && request.Method == HttpMethod.Put)
                {
                    onRewriteFails?.Invoke();
                    return FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}");
                }
                return failNewKeyDelete && request.Method == HttpMethod.Delete
                    ? FakeRosterServer.Json(HttpStatusCode.InternalServerError, "{}")
                    : null;
            };
        };
        old.Override = request =>
        {
            if (!IsRetire(request)) return null;
            rewriting = true;
            // 消す直前に、別の端末が登録を書いた（写し直しに入る）
            old.WriteFromElsewhere(state => state.For(Ds).Pokemon.Add(new RegisteredPokemon { Id = 2, PokemonId = 445, PokemonName = "ガブリアス" }));
            return null;
        };
    }

    [Fact]
    public async Task Rotation_whose_rewrite_fails_goes_back_to_the_old_key_and_discards_the_new_one()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        var created = new List<string>();
        ArrangeRewriteFailure(server, old, created);

        var result = await sync.RotateAsync();

        var newKey = Assert.Single(created);
        Assert.Equal(RosterRotationStop.RewriteFailed, result.Stop);
        Assert.Equal(RotationKeyState.OldKeyRestored, result.KeyState);
        Assert.False(result.OldKeyDeleted);
        Assert.Equal(oldKey, sync.Key);                                    // 古いキーへ戻っている（新しいキーのままではない）
        Assert.Equal(oldKey, js.Storage[CloudSync.KeyStorageKey]);
        Assert.False(old.Deleted);                                         // 古いキーは残っている
        Assert.True(result.NewKeyDiscarded);
        Assert.True(server.For(newKey).Deleted);                           // 使われない新しいキーの置き場は消す
        var message = CloudSync.DescribeRotation(result);
        Assert.Contains("古いアクセスキーのまま使えます", message);
        Assert.Contains("もう一度お試しください", message);
        Assert.Contains("配信用BOTや他の端末には、新しいアクセスキーを入れないでください", message);
        Assert.DoesNotContain("不完全", message);

        // 戻したあとの保存は、古いキーの置き場へ入る（新しいキーの置き場には書かれない）
        var state = RosterRepository.Deserialize(await sync.LoadAsync());
        state.For(Ds).Pokemon.Add(new RegisteredPokemon { Id = 90, PokemonId = 6, PokemonName = "リザードン" });
        await sync.SaveAsync(RosterRepository.Serialize(state));
        Assert.Contains(old.State.For(Ds).Pokemon, p => p.PokemonName == "リザードン");
        Assert.DoesNotContain(server.For(newKey).State.For(Ds).Pokemon, p => p.PokemonName == "リザードン");
    }

    [Fact]
    public async Task Rotation_whose_rewrite_fails_and_cannot_go_back_keeps_the_new_key_and_says_how_to_recover()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        var created = new List<string>();
        // 書き直しの失敗と同時に、ブラウザが保存できなくなる（古いキーへ戻す保存も失敗する）
        ArrangeRewriteFailure(server, old, created, onRewriteFails: () => js.FailSet = true);

        var result = await sync.RotateAsync();

        var newKey = Assert.Single(created);
        Assert.Equal(RosterRotationStop.RewriteFailed, result.Stop);
        Assert.Equal(RotationKeyState.IncompleteNewKey, result.KeyState);
        Assert.False(result.NewKeyDiscarded);
        Assert.Equal(newKey, sync.Key);                                    // 保存できないので、ブラウザは新しいキーのまま
        Assert.Equal(newKey, js.Storage[CloudSync.KeyStorageKey]);
        Assert.False(server.For(newKey).Deleted);                          // 使っているキーの置き場は消さない
        Assert.False(old.Deleted);
        var message = CloudSync.DescribeRotation(result);
        Assert.Contains("古いアクセスキーに戻せませんでした", message);
        Assert.Contains("不完全な新しいアクセスキーのまま使われています", message);
        Assert.Contains("データ共有をやめる", message);
        Assert.Contains("新しいアクセスキーを入れないでください", message);
    }

    [Fact]
    public async Task Rotation_that_goes_back_to_the_old_key_says_so_when_the_new_slot_cannot_be_discarded()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        var created = new List<string>();
        ArrangeRewriteFailure(server, old, created, failNewKeyDelete: true);

        var result = await sync.RotateAsync();

        var newKey = Assert.Single(created);
        Assert.Equal(RotationKeyState.OldKeyRestored, result.KeyState);    // 古いキーへは戻せている
        Assert.Equal(oldKey, sync.Key);
        Assert.False(result.NewKeyDiscarded);
        Assert.False(server.For(newKey).Deleted);                          // 捨てられなかったので残る（使われない）
        Assert.Contains("置き場は消せず残りましたが、使われていません", CloudSync.DescribeRotation(result));
    }

    [Fact]
    public async Task Rotation_whose_old_key_is_already_gone_is_described_as_completed()
    {
        var (sync, _, server, oldKey) = await JoinedAsync(WithRival());
        server.For(oldKey).Override = request => IsRetire(request) ? FakeRosterServer.Json(HttpStatusCode.Gone, "{}") : null;

        var result = await sync.RotateAsync();

        Assert.True(result.OldKeyDeleted);
        Assert.Contains("作り直しました", CloudSync.DescribeRotation(result));
        Assert.DoesNotContain("不完全", CloudSync.DescribeRotation(result));
    }

    [Fact]
    public async Task Rotation_whose_delete_fails_unexpectedly_returns_a_failure_and_switches_to_the_new_key()
    {
        var (sync, _, server, oldKey) = await JoinedAsync(WithRival());
        var old = server.For(oldKey);
        old.Override = request => IsRetire(request) ? throw new InvalidOperationException("想定外") : null;

        var result = await sync.RotateAsync();

        Assert.False(result.OldKeyDeleted);
        Assert.Equal(RosterRotationStop.Failed, result.Stop);
        Assert.NotEqual(oldKey, sync.Key);
        Assert.Contains("消せませんでした", CloudSync.DescribeRotation(result));
    }

    [Fact]
    public async Task Sharing_stopped_in_another_tab_stops_saving_here_instead_of_writing_to_the_browser()
    {
        var (sync, js, server, key) = await JoinedAsync(WithRival());
        Assert.True(sync.IsSyncing);

        js.Storage.Remove(CloudSync.KeyStorageKey);   // 別のタブで共有を止めた（キーが消えた）
        await Assert.ThrowsAsync<RosterUnavailableException>(() => sync.SaveAsync(Pokemon("ガブリアス")));

        Assert.False(sync.IsSyncing);
        Assert.NotNull(sync.LastError);
        Assert.False(js.Storage.ContainsKey(LocalKey));   // このブラウザの登録データには黙って書かない
        Assert.Null(await sync.LoadAsync());               // 読むのは止めない（このブラウザの登録データを読む）
    }

    [Fact]
    public async Task Rotated_key_in_another_tab_is_picked_up_on_the_next_save()
    {
        var (sync, js, server, _) = await JoinedAsync(WithRival());
        var other = RemoteRosterStore.GenerateKey();
        js.Storage[CloudSync.KeyStorageKey] = other;   // 別のタブでキーを作り直した

        await sync.SaveAsync(Pokemon("ガブリアス"));

        Assert.Equal(other, sync.Key);
        Assert.Equal("ガブリアス", server.For(other).State.For(Ds).Pokemon.Single().PokemonName);
    }

    [Fact]
    public async Task Starting_sharing_keeps_browser_data_that_was_written_while_it_was_being_moved()
    {
        var js = new FakeJsRuntime();
        js.Storage[LocalKey] = Pokemon("リザードン");
        using var server = new KeyRoutedServer();
        var written = false;
        server.OnCreate = (_, bucket) => bucket.Override = request =>
        {
            // 移している途中で、このブラウザの別のタブが登録を書く（読んだあとの変化）
            if (!written && request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/my/pokemon")
            {
                written = true;
                js.Storage[LocalKey] = Pokemon("ガブリアス");
            }
            return null;
        };
        var sync = NewSync(js, server);

        await sync.StartNewAsync();

        Assert.True(written);
        Assert.Equal("リザードン", server.For(sync.Key!).State.For(Ds).Pokemon.Single().PokemonName);   // 移したのは読んだ内容
        Assert.Equal(Pokemon("ガブリアス"), js.Storage[LocalKey]);                                    // 写していないものは消さない
        Assert.NotNull(sync.LastNotice);
        Assert.Contains("消さずに残しました", sync.LastNotice!);
    }

    [Fact]
    public async Task Starting_sharing_clears_the_browser_when_nothing_changed_while_moving()
    {
        var js = new FakeJsRuntime();
        js.Storage[LocalKey] = Pokemon("リザードン");
        using var server = new KeyRoutedServer();
        var sync = NewSync(js, server);

        await sync.StartNewAsync();

        Assert.False(js.Storage.ContainsKey(LocalKey));
        Assert.Null(sync.LastNotice);
    }

    [Fact]
    public async Task Rotation_whose_new_key_cannot_be_saved_removes_the_copied_new_slot_and_keeps_the_old_key()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var created = new List<string>();
        server.OnCreate = (key, _) => created.Add(key);
        js.FailSet = true;   // 新しいキーを保存できない（ブラウザの保存領域に書けない）

        await Assert.ThrowsAsync<JSException>(() => sync.RotateAsync());

        Assert.Equal(oldKey, sync.Key);
        Assert.Equal(oldKey, js.Storage[CloudSync.KeyStorageKey]);
        Assert.False(server.For(oldKey).Deleted);
        Assert.True(server.For(Assert.Single(created)).Deleted);   // 使われない新しいキーの置き場は消す
    }

    [Fact]
    public async Task Starting_sharing_whose_key_cannot_be_saved_removes_the_copied_new_slot_and_keeps_the_browser_data()
    {
        var js = new FakeJsRuntime { FailSet = true };
        js.Storage[LocalKey] = Pokemon("リザードン");
        using var server = new KeyRoutedServer();
        var created = new List<string>();
        server.OnCreate = (key, _) => created.Add(key);
        var sync = NewSync(js, server);

        await Assert.ThrowsAsync<JSException>(() => sync.StartNewAsync());

        Assert.False(sync.IsSyncing);
        Assert.Equal(Pokemon("リザードン"), js.Storage[LocalKey]);   // ブラウザのデータは残る
        Assert.True(server.For(Assert.Single(created)).Deleted);
    }

    [Fact]
    public async Task A_switch_that_took_effect_is_not_undone_when_a_later_step_fails()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var created = new List<string>();
        server.OnCreate = (key, _) => created.Add(key);
        var failOnce = true;
        // キーは保存できた（切り替えは効いた）あとの通知で失敗する。新しいキーは使われているので、消してはいけない
        sync.Changed += () =>
        {
            if (!failOnce) return;
            failOnce = false;
            throw new InvalidOperationException("通知で失敗");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.RotateAsync());

        var newKey = Assert.Single(created);
        Assert.Equal(newKey, sync.Key);
        Assert.Equal(newKey, js.Storage[CloudSync.KeyStorageKey]);
        Assert.False(server.For(newKey).Deleted);
        Assert.Equal("リザードン", server.For(newKey).State.For(Ds).Pokemon.Single().PokemonName);
    }

    [Fact]
    public async Task Reading_does_not_reread_the_stored_key_but_writing_does()
    {
        var (sync, js, _, _) = await JoinedAsync(WithRival());
        js.Calls.Clear();

        await sync.LoadAsync();
        Assert.DoesNotContain("localStorage.getItem", js.Calls);

        await sync.SaveAsync(Pokemon("ガブリアス"));
        Assert.Contains("localStorage.getItem", js.Calls);
    }

    [Fact]
    public async Task A_tab_that_was_not_sharing_gets_a_conflict_instead_of_overwriting_a_key_another_tab_started()
    {
        var js = new FakeJsRuntime();
        using var server = new KeyRoutedServer();
        var key = RemoteRosterStore.GenerateKey();
        using var http = new HttpClient(server);
        await new RemoteRosterStore(http, Base, key).SaveAsync(RosterRepository.Serialize(WithRival()));
        var sync = NewSync(js, server);
        await sync.InitializeAsync();
        Assert.False(sync.IsSyncing);

        js.Storage[CloudSync.KeyStorageKey] = key;   // 別のタブでデータ共有を始めた（このタブは読んでいない）

        await Assert.ThrowsAsync<RosterConflictException>(() => sync.SaveAsync(Pokemon("ガブリアス")));
        Assert.Equal("リザードン", server.For(key).State.For(Ds).Pokemon.Single().PokemonName);   // 上書きしていない
    }

    [Fact]
    public async Task A_read_after_another_tab_rotated_the_key_uses_the_new_key()
    {
        var (sync, js, server, oldKey) = await JoinedAsync(WithRival());
        var newKey = RemoteRosterStore.GenerateKey();
        using var http = new HttpClient(server);
        await new RemoteRosterStore(http, Base, newKey).SaveAsync(Pokemon("ガブリアス"));
        await new RemoteRosterStore(http, Base, oldKey).DeleteAsync();   // 別のタブが作り直して、古いキーを消した
        js.Storage[CloudSync.KeyStorageKey] = newKey;

        var json = await sync.LoadAsync();

        Assert.Equal("ガブリアス", RosterRepository.Deserialize(json).For(Ds).Pokemon.Single().PokemonName);
        Assert.Equal(newKey, sync.Key);
    }
}
