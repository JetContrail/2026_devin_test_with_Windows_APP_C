# Ethernet必須試験 (RULES)

自動試験: `tests/Monitoring.IntegrationTests` がTCPループバックで実施(CIで毎PR実行)。
長時間・物理系は本ドキュメントの手順に従う。

| 試験 | 実施場所 | 内容 |
|---|---|---|
| 通信断 | `PeerClose_AutoReconnects`, `PeerStopped_RecoversWhenRestarts` | サーバ切断/停止で Down→Reconnecting→復帰を確認 |
| 再接続 | `Reconnect100_AllSucceed` | 連続100回の切断→自動再接続→ポーリング成功 |
| タイムアウト | `Timeout_NoReply_Throws` | 応答なしでDeviceTimeoutException、連続失敗で再接続判定 |
| 異常パケット | `GarbageBytes_ResyncsAndDecodes`, `PartialFrames_ReassemblesStream` | ゴミバイト混入・フレーム分割でも再同期/再構成 |
| 相手停止 | `PeerStopped_RecoversWhenRestarts`, `Connect_RetriesUntilServerAppears` | 相手不在でも接続リトライを継続し復帰 |

## LANケーブル抜去試験(物理手順)

1. `FakeDevice` を別PC(または実デバイス)で起動、アプリを接続
2. クライアント側LANケーブルを抜く → UIが `Lost`/`Reconnecting` に遷移することを確認
3. ケーブル再接続 → バックオフ(最大30s)内に `Connected` 復帰、ポーリング再開を確認
4. `logs/comm-*.log` に disconnect/connect と tx/rx フレームが記録されていることを確認

## 通信相手停止試験(手順)

1. FakeDeviceを終了(kill) → UI `Lost` 表示とバックオフリトライを確認
2. FakeDeviceを再起動 → 自動復帰することを確認(手動操作なし)

## 連続再接続100回試験

CI: `Reconnect100_AllSucceed`。手動: FakeDeviceに `--drop 100` を付けてアプリを観察(接続毎に切断→再接続を繰返し)。

## 24時間連続運転試験

```powershell
dotnet run --project tests/FakeDevice -- --port 50001 --events 3
dotnet run --project tests/LongRun -- --hours 24 --port 50001
```

5分毎に WorkingSet/スレッド/ハンドルを記録し、終了時にメモリドリフト<10%・スレッド増加<5なら PASS(メモリリークゼロ判定)。nightly実行は `.github/workflows/soak.yml` を参照。
