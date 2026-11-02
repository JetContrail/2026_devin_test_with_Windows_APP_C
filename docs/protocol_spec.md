# 通信プロトコル仕様書 (UDPポーリング) — ドラフト v0.1

対向デバイス(鍵・ドア・カードリーダー制御盤)との通信仕様。**プロダクトオーナー承認待ち**。

## 1. 基本方針

- トランスポート: **UDP**。ポート既定値 `50001`(設定可能)
- アプリがポーリング側(クライアント)、デバイスが応答側(サーバ)
- 全メッセージは request/response 1往復。デバイスからの自発送信はしない(カードリーダーイベントもポーリングで取得)
- バイトオーダ: **ビッグエンディアン**

## 2. フレームフォーマット

| オフセット | サイズ | フィールド | 説明 |
|---|---|---|---|
| 0 | 2 | Magic | `0x4A43` ("JC") |
| 2 | 1 | Version | `0x01` |
| 3 | 1 | MessageType | §3参照 |
| 4 | 2 | Sequence | 送信側が採番。responseはrequestと同値 |
| 6 | 2 | PayloadLength | ペイロードのバイト数(0〜1472) |
| 8 | N | Payload | メッセージ種別依存 |
| 8+N | 2 | CRC16 | CRC-16/CCITT-FALSE、Magic〜Payload末尾まで |

異常系: Magic不一致・Version不一致・CRCエラー・長さ不整合のデータグラムは**破棄しログ**に記録する。

## 3. メッセージ種別

| Type | 名称 | 方向 | Payload |
|---|---|---|---|
| `0x01` | PollStatus | App→Dev | なし |
| `0x81` | StatusResponse | Dev→App | §4.1 |
| `0x02` | GetCardEvents | App→Dev | LastSeq(4): この連番以降のイベントを要求。0=全件 |
| `0x82` | CardEventsResponse | Dev→App | §4.2 |
| `0x10` | LockCommand | App→Dev | §4.3 |
| `0x90` | CommandResponse | Dev→App | §4.4 |
| `0x7F` | ErrorResponse | Dev→App | ErrorCode(1) + Message(UTF-8) |

## 4. ペイロード詳細

### 4.1 StatusResponse (`0x81`)

| サイズ | フィールド | 値 |
|---|---|---|
| 1 | LockState | 0=Unknown, 1=Locked, 2=Unlocked, 3=Jammed |
| 1 | DoorState | 0=Unknown, 1=Closed, 2=Open |
| 4 | DoorOpenSeconds | 現在開いている継続秒数。Closedなら0 |
| 1 | CardReaderStatus | 0=Offline, 1=Online, 2=Error |
| 1 | PendingCardEvents | デバイス側に残っているカードイベント件数(>0ならGetCardEventsで取得) |
| 1 | DeviceStatus | bit0: ドア開きっぱなしアラート, bit1: デバイス警告, bit2: バッテリ低下(将来) |

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

## 5. 送受信ルール

- **シーケンス**: requestのSequenceをそのまま返す。不一致・重複・遅延到着のresponseは破棄
- **タイムアウト**: 応答待ち `500ms`。再送は最大2回(同一Sequence)
- **通信断判定**: 連続3回のポーリング失敗で接続状態 `Lost`。復帰後は `Connected` に自動遷移
- **ポーリング周期**: 既定 `1000ms`(設定可能、最小200ms)。PendingCardEvents>0の場合は即座にGetCardEventsを発行
- **制御コマンド**: ポーリングループとは別に即時送信。応答Result≠0の場合は理由をUI通知

## 6. アプリ側のドア開きっぱなし監視

- `DoorState=Open` かつ `DoorOpenSeconds` が閾値(既定60秒、設定可能)超過、またはデバイスのbit0アラート → UIアラート。両系統で冗長監視する

## 7. 未決事項(要PO承認)

1. ポート番号の既定値 `50001` でよいか
2. ポーリング周期 1秒で十分か(応答性 vs デバイス負荷)
3. 開きっぱなし閾値の既定60秒でよいか
4. 複数ドア/複数デバイスの監視は将来要件か(現行仕様は1デバイス前提)
5. 認証・暗号化は不要か(社内LAN前提)。必要ならDTLS等の検討が別途必要
