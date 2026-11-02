# 2026_devin_test_with_Windows_APP_C

Ethernet(TCP/IP, 常時接続ポーリング)経由で鍵・ドア・カードリーダーを監視制御する Windows GUI アプリケーション。.NET 8 + WPF。自動再接続(指数バックオフ)・TCP KeepAlive・通信ログ保存(`logs/comm-*.log`)。表示イメージ(テーマ色・ロゴ等)は `user_themes.json` でユーザー毎に切替可能。

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/Monitoring.Protocol` | 通信フレーム・メッセージのエンコード/デコード、CRC16 |
| `src/Monitoring.Net` | TCPクライアント(シーケンス照合・タイムアウト・自動再接続・KeepAlive・通信ログ) |
| `src/Monitoring.Core` | ポーリングエンジン、接続状態機械、ドア開放監視 |
| `src/Monitoring.App` | WPFアプリ(単一EXE)。ユーザー別テーマ切替 |
| `tests/FakeDevice` | 試験用デバイスシミュレータ(故障注入可) |
| `tests/Monitoring.UnitTests` / `tests/Monitoring.IntegrationTests` | xUnit試験 |

## ドキュメント

- [docs/analysis_and_plan.md](docs/analysis_and_plan.md) — 現状分析・開発計画・リスク・アーキテクチャ・テスト戦略
- [docs/protocol_spec.md](docs/protocol_spec.md) — TCP通信プロトコル仕様
- [docs/ethernet_tests.md](docs/ethernet_tests.md) — Ethernet必須試験の実施方法
- [RULES.md](RULES.md) — 開発ルール(ブランチ/品質/試験/ドキュメント/レビュー)

## ビルド・実行 (Windows)

```powershell
dotnet build Monitoring.sln
dotnet run --project tests/FakeDevice -- --port 50001 --door open --reader online --events 3
dotnet run --project src/Monitoring.App
```

ユーザー別の表示イメージ: `src/Monitoring.App/user_themes.json` に `"<Windowsユーザー名>": { "AccentColor": "#RRGGBB", "LogoImage": "C:\\path\\logo.png", "TitleSuffix": " ..." }` を追加する(`*` がフォールバック)。

## CI

`.github/workflows/ci.yml` — windows-latest で format検証 + build + test。`.github/workflows/soak.yml` — 24時間連続運転試験(週次/手動)。
