# リリース手順書

対象: Door Monitor (Monitoring.App / .NET 8 + WPF) — プロトコル **v0.4**

## 1. 前提条件

| 項目 | 要件 |
|---|---|
| OS | Windows 10/11 (x64) |
| .NET | .NET 8 SDK (ビルド) / .NET 8 Desktop Runtime (実行) |
| デバイスFW | プロトコル v0.4 対応(CardEventsResponse先頭に `u32 BootId`) |
| ネットワーク | デバイス TCP 50001 到達可能 |

## 2. ビルド・検証

```powershell
dotnet restore Monitoring.sln
dotnet build Monitoring.sln -c Release        # 警告ゼロ(TreatWarningsAsErrors)
dotnet test tests/Monitoring.UnitTests -c Release
dotnet test tests/Monitoring.IntegrationTests -c Release
dotnet format Monitoring.sln --verify-no-changes
```

公開バイナリ:

```powershell
dotnet publish src/Monitoring.App -c Release -r win-x64 --self-contained -o publish/
```

`publish/` に `Monitoring.App.exe`, `appsettings.json`, `user_themes.json` が出力される。

## 3. 設定(配布前に必須編集)

`appsettings.json` — Devices配列の各デバイス:

| キー | 内容 | 必須 |
|---|---|---|
| `Name` | 表示名・通信ログ識別子 | - |
| `Host` / `Port` | デバイスアドレス(既定50001) | ○ |
| `UseTls` | TLS有効化(必須: true) | ○ |
| `TargetHost` | TLS証明書のCN/SAN検証名 | TLS時 |
| `CertThumbprint` | デバイス証明書ピン留め(推奨: 指定時は失効チェック省略) | 推奨 |
| `PskHex` | 共有鍵(64hex=32byte、**16byte未満は拒否**) | ○ |
| `AllowInsecure` | trueで平文/無認証を許可(**ラボ・デモ専用、本番禁止**) | - |

- **未設定のプレースホルダ(`REPLACE_WITH_*`)では起動時にフェイルクローズ**して接続しない。
- `Monitor`: `PollIntervalMs=1000`, `LostAfterFailures=3`, `DoorOpenAlertThresholdSec=60`, `UnlockDurationSec`(0=手動再施錠まで解錠維持)は確定値。
- `user_themes.json`: `"<Windowsユーザー名>"` キーで AccentColor/LogoImage/TitleSuffix を個人化、`"*"` がフォールバック。

## 4. デプロイ

1. `publish/` 一式を対象PCの任意フォルダ(例: `C:\DoorMonitor`)にコピー。
2. `appsettings.json` を実デバイス情報に書き換え(証明書サムプリント・PSKはデバイス毎に別値を推奨)。
3. 起動確認 → 接続表示とステータスが更新されること。

通信ログは `%LocalAppData%\DoorMonitor\logs\comm-<Name>-<tag>-YYYYMMDD.log`(JSON Lines、日次+16MBローテーション)に出力 — インストール先が読み取り専用でも書き込める。

## 5. 受入確認

- [ ] 接続→Connected表示・状態(鍵/ドア/リーダー)が1秒周期で更新
- [ ] Lock/Unlockボタンが未接続時Disable、実行結果がログ欄に記録
- [ ] ドア開放60秒で「DOOR LEFT OPEN」表示
- [ ] LANケーブル抜去→Lost遷移→挿入で自動再接続(ethernet_tests.md手順)
- [ ] デバイス再起動後もカードイベントが継続取得される(BootIdリセット)
- [ ] ログがデバイス毎に分離(`comm-<Name>-<tag>-*.log`)・`dev`フィールド付き

## 6. 運用

- **24h連続運転**: `tests/LongRun` + `soak.yml` で検証(週次/手動ワークフロー)。メモリドリフト基準値はウォームアップ(2min)後。
- **バージョン**: プロトコルは `docs/protocol_spec.md` の版(v0.4)。FWと不整合ならアプリは接続するがカードイベントがデコードエラーになる。
- **障害時**: 通信ログ(JSON)で時刻・デバイス・フレーム・CRCを追跡。監視継続はログ障害では止まらない設計。

## 7. ロールバック

前リリースの `publish/` フォルダを保持し、戻す場合は一式を差し替えるのみ(設定ファイルは互換)。DB/レジストリ等の永続状態なし。

## 8. 既知の残課題

- GUI自動試験は未実装(Phase 4)。手動の受入確認を実施すること。
- 実デバイスFWのv0.4対応が前提。旧FW(v0.3)とはカードイベント取得が非互換。
- 24h soak初回実行はリリース前に1回実施推奨。
