# 2026_devin_test_with_Windows_APP_C

Ethernet(UDPポーリング)経由で鍵・ドア・カードリーダーを監視制御する Windows GUI アプリケーション。.NET 8 + WPF、GUIはユーザー毎に差し替え可能なプラグイン構成。

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/Monitoring.Protocol` | 通信フレーム・メッセージのエンコード/デコード、CRC16 |
| `src/Monitoring.Net` | UDPクライアント(シーケンス照合・タイムアウト・再送) |
| `src/Monitoring.Core` | ポーリングエンジン、接続状態機械、ドア開放監視 |
| `src/Monitoring.Gui.Contract` | GUIプラグイン契約 `IMonitorGui` |
| `src/Monitoring.AppHost` | 起動シェル(接続確立 + ユーザー別GUI DLLのロード) |
| `src/Monitoring.App.Wpf` | 標準GUIプラグイン(WPF) |
| `tests/FakeDevice` | 試験用デバイスシミュレータ(故障注入可) |
| `tests/Monitoring.UnitTests` / `tests/Monitoring.IntegrationTests` | xUnit試験 |

## ドキュメント

- [docs/analysis_and_plan.md](docs/analysis_and_plan.md) — 現状分析・開発計画・リスク・アーキテクチャ・テスト戦略
- [docs/protocol_spec.md](docs/protocol_spec.md) — UDP通信プロトコル仕様

## ビルド・実行 (Windows)

```powershell
dotnet build Monitoring.sln
dotnet run --project tests/FakeDevice -- --port 50001 --door open --reader online --events 3
dotnet run --project src/Monitoring.AppHost
```

GUIの切り替え: `src/Monitoring.AppHost/gui_plugins.json` に `"<Windowsユーザー名>": "<GUI DLL名>"` を追加する(`*` がフォールバック)。

## CI

`.github/workflows/ci.yml` — windows-latest で build + test。
