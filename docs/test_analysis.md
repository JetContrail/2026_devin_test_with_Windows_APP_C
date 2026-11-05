# テスト結果分析 & Monitoring.App 機能レビュー

作成日: 2026-11-02 ／ 対象: main (squash `0cd1d72` + flag修正)

## 1. dotnet test 実行結果

| プロジェクト | 実行環境 | 結果 |
|---|---|---|
| Monitoring.UnitTests | Linux (.NET 8) | **27/27 PASS** |
| Monitoring.IntegrationTests | Linux (.NET 8) | **17/17 PASS** |
| Monitoring.App (WPF) | windows-latest CI | ビルド成功・警告ゼロ |

合計 44件、全パス。`dotnet format --verify-no-changes` 全プロジェクト差異なし(TreatWarningsAsErrors)。

### カバレッジ範囲(試験項目別)

| RULES要求 | 対応試験 |
|---|---|
| 通信断 | `UnreachableDevice_TransitionsToLost`, `ConsecutiveFailures_TransitionToLost` |
| 再接続 | `PeerClose_AutoReconnects`, `Connect_RetriesUntilServerAppears`, `RecoveryAfterLost_ReturnsToConnected` |
| タイムアウト | `Timeout_NoReply_Throws`, `HangPoll`(PollTimeout) |
| 異常パケット | `GarbageBytes_ResyncsAndDecodes`, `CorruptReply`, `PartialFrames_ReassemblesStream` |
| 相手停止 | `PeerStopped_RecoversWhenRestarts` |
| 再接続100回 | `Reconnect100_AllSucceed` |
| 24h連続運転 | `tests/LongRun` + soak.yml(週次/手動、ウォームアップ後基準値でドリフト判定) |
| LAN抜去 | docs/ethernet_tests.md(物理手順) |
| 認証 | PSK正誤・リプレイ・未認証拒否・TLSピン留め・証明書失効(6件) |
| 再発防止 | 全バグ修正に回帰テスト付(BootId, Dispose隔離, 短PSK, 認証中断, ログ名衝突) |

**カバレッジ上の隙間:**
- WPF App自体はUIロジックが薄く単体試験対象外(MainWindowのXAMLイベント配線のみ)。GUI自動試験はPhase 4課題。
- LAN物理抜去・実機結合は自動化不可のため手順書運用。
- コマンド送信期限の「送信中期限切れ」パスはタイミング依存で未直接試験(コードパスは存在)。

## 2. Monitoring.App 機能レビュー

### 仕様適合

| 要求 | 実装 | 状態 |
|---|---|---|
| 監視: 鍵・ドア・カードリーダー | LockText/DoorText/ReaderText + PollStatus | OK |
| 開きっぱなし監視 | DoorOpenText + 「DOOR LEFT OPEN」赤表示(閾値60s) | OK |
| 制御: 鍵 | Lock/Unlockボタン、未接続時はDisable | OK |
| ユーザー毎の表示イメージ | user_themes.json(AccentColor/LogoImage/TitleSuffix、`*`フォールバック) | OK |
| 複数デバイス | Devices配列 + セレクタ(>1台で表示) | OK |
| 24h常時監視 | 自動再接続・Lost判定・ログローテーション | OK |

### レビュー指摘(優先度順)

1. **[低] `OnStartup` が `async void` で例外が未ハンドル** — appsettings.json不正(構文エラー/必須プロパティ欠落/不正なIP)時は未処理例外で落ちる。`try/catch` で `MessageBox` + `Shutdown(2)` にすると設定ミスが分かりやすい。
2. **[低] デバイス選択で `Attach` が2回呼ばれる** — ctorで `Attach(_monitors[0])` + `SelectedIndex=0` のSelectionChanged発火で二重登録→解除→再登録(実害は「Watching」ログ1行のみ)。
3. **[低] 状態表示が文字列のみ** — Connected/Reconnecting/Lost を色分けすると遠隔監視の視認性向上。
4. **[低] 通知・常駐化なし** — 24h運用ではタスクトレイ常駐やドア開放・Lost時の音/トースト通知が望ましい(要望次第)。
5. **[参考] CommunicationLogの所有権** — `TcpDeviceClient`がDisposeするため二重破棄なし(確認済)。
6. **[参考] コマンド期限** — `SendLockAsync`は`CancellationToken.None`だが、クライアント内部の `CommandConnectBudget`(2秒)が期限を担保(確認済)。

### 設定の安全側動作(確認済)

- `UseTls: true` + `PskHex` 必須。プレースホルダのままだと `FromConfig` が `InvalidOperationException` でフェイルクローズ(平文運用は `AllowInsecure: true` 明示のみ)。
- PSKは最低16バイト(32hex)必須。
