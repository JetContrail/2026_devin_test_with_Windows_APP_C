# 通信プロトコル仕様書 (TCP/IP + 自動再接続) — v0.2

対向デバイス(鍵・ドア・カードリーダー制御盤)との通信仕様。
v0.1(UDP)から **TCP/IP永続接続** へ変更(2026-11-02 指示)。フレーム形式は継続。

## 1. 基本方針

- トランスポート: **TCP**。デバイス側がリッスン、アプリが接続(クライアント)
- ポート既定値 `50001`(設定可能)
- 1本の永続接続上で request/response を多重化。デバイスからの自発送信はしない(カードリーダーイベントもポーリングで取得)
- バイトオーダ: **ビッグエンディアン**
- TCPはストリームのため、フレーム境界は `PayloadLength` フィールドで復元する(部分受信・結合受信を正規扱い)

## 2. フレームフォーマット(変更なし)

| オフセット | サイズ | フィールド | 説明 |
|---|---|---|---|
| 0 | 2 | Magic | `0x4A43` ("JC") |
| 2 | 1 | Version | `0x01` |
| 3 | 1 | MessageType | §3参照 |
| 4 | 2 | Sequence | 送信側が採番。responseはrequestと同値 |
| 6 | 2 | PayloadLength | ペイロードのバイト数(0〜1472) |
| 8 | N | Payload | メッセージ種別依存 |
| 8+N | 2 | CRC16 | CRC-16/CCITT-FALSE、Magic〜Payload末尾まで |

異常系:
- Magic不一致・Version不一致・CRCエラー・長さ不整合 → 受信データを**再同期**(Magic `0x4A43` を先頭からスキャンして捨てる)し、通信ログに記録
- 連続する不正フレームが閾値(10回)を超えたら接続を切断し再接続フェーズへ

## 3. メッセージ種別(v0.1と同じ)

| Type | 名称 | 方向 | Payload |
|---|---|---|---|
| `0x01` | PollStatus | App→Dev | なし(ハートビート兼用) |
| `0x81` | StatusResponse | Dev→App | §4.1 |
| `0x02` | GetCardEvents | App→Dev | LastSeq(4) |
| `0x82` | CardEventsResponse | Dev→App | §4.2 |
| `0x10` | LockCommand | App→Dev | §4.3 |
| `0x90` | CommandResponse | Dev→App | §4.4 |
| `0x7F` | ErrorResponse | Dev→App | ErrorCode(1) + Message(UTF-8) |

## 4. ペイロード詳細(v0.1と同じ)

### 4.1 StatusResponse (`0x81`)

| サイズ | フィールド | 値 |
|---|---|---|
| 1 | LockState | 0=Unknown, 1=Locked, 2=Unlocked, 3=Jammed |
| 1 | DoorState | 0=Unknown, 1=Closed, 2=Open |
| 4 | DoorOpenSeconds | 現在開いている継続秒数。Closedなら0 |
| 1 | CardReaderStatus | 0=Offline, 1=Online, 2=Error |
| 1 | PendingCardEvents | 未送カードイベント件数 |
| 1 | DeviceStatus | bit0: ドア開きっぱなしアラート, bit1: デバイス警告, bit2: 将来予約 |

### 4.2 CardEventsResponse (`0x82`)

| サイズ | フィールド |
|---|---|
| 1 | Count |
| Count×可変 | Events[] |

Event: EventSeq(4) + TimestampUnixSec(4) + CardIdLen(1) + CardId(N, UTF-8) + Result(1: 0=Denied,1=Accepted)

### 4.3 LockCommand (`0x10`)

| サイズ | フィールド | 値 |
|---|---|---|
| 1 | Command | 1=Lock, 2=Unlock |
| 4 | UnlockDurationSec | Unlock時の自動再施錠秒数。0=維持 |

### 4.4 CommandResponse (`0x90`)

| サイズ | フィールド | 値 |
|---|---|---|
| 1 | Result | 0=OK, 1=Rejected, 2=Timeout, 3=Jammed |
| 1 | LockState | §4.1と同じ |

## 5. 接続・再接続・KeepAlive

- **接続状態機械**: `Disconnected → Connecting → Connected → Reconnecting → Connected`(失敗継続で `Lost` と判定してUI通知、内部ではReconnectingを継続)
- **自動再接続**: 切断・送信/受信エラー検知で即座に再接続試行。指数バックオフ `1s → 2s → 4s …`、上限 `30s`、各試行に ±20% ジッター。上限到達後も `30s` 間隔で継続(24時間運用前提、諦めない)
- **KeepAlive**: 2系統。
  - ソケットレベル: TCP KeepAlive 有効化(idle 15s、間隔 5s、試行 3回 相当)
  - アプリケーションレベル: PollStatus(1秒周期)が実質ハートビート。応答タイムアウト `500ms`、連続3回失敗で接続不良と判定→再接続
- **再送**: TCPなので同一シーケンス再送はしない。応答タイムアウト時は要求を失敗扱いにして次のポーリングへ(コマンドは失敗をUI通知)
- **Sequence照合**: responseはrequestと同値を返す。不一致・遅延到着は破棄してログ

## 6. 通信ログ

- 送受信フレームを `logs/comm-YYYYMMDD.log` にJSON Linesで記録: `{ts, dir, type, seq, len, crcOk, note}`
- 再接続・エラーも同ファイルに記録(note="connect"/"disconnect"/"error")
- ローテーション: 日次 + 1ファイル最大 16MB(超過で連番ファイル)

## 7. ポーリング・制御ルール

- ポーリング周期: 既定 `1000ms`(設定可能、最小200ms)。PendingCardEvents>0で即GetCardEvents
- 制御コマンド: ポーリングとは別に即時送信。応答 `500ms` 待ち。Result≠0は理由をUI通知
- ドア開きっぱなし: 開状態が閾値(既定60秒)超過、またはデバイスbit0アラート → UIアラート(冗長監視)

## 8. 未決事項(要PO承認)

1. ポート番号 `50001` でよいか
2. ポーリング周期 1秒で十分か
3. 開きっぱなし閾値 60秒でよいか
4. 複数ドア/複数デバイス監視は将来要件か(現行は1デバイス前提)
5. 認証・暗号化は不要か(社内LAN前提)。必要ならTLS化が別途必要
