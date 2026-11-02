# 通信プロトコル仕様書 (TCP/IP + 自動再接続 + 認証/暗号) — v0.4

対向デバイス(鍵・ドア・カードリーダー制御盤)との通信仕様。
v0.1(UDP)から **TCP/IP永続接続** へ変更(2026-11-02 指示)。フレーム形式は継続。
v0.3: 既定値確定、**複数デバイス必須**、**認証/暗号必須**(TLS + PSKハンドシェイク)を追加(2026-11-02 設計承認)。
v0.4: CardEventsResponse に **BootId(世代ID)** を追加 — デバイス再起動でイベント連番がリセットされても
アプリがカーソルを適切に初期化できるようにする(2026-11-02 レビュー指摘対応)。

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

## 3. メッセージ種別

| Type | 名称 | 方向 | Payload |
|---|---|---|---|
| `0x01` | PollStatus | App→Dev | なし(ハートビート兼用) |
| `0x81` | StatusResponse | Dev→App | §4.1 |
| `0x02` | GetCardEvents | App→Dev | LastSeq(4) |
| `0x82` | CardEventsResponse | Dev→App | §4.2 |
| `0x10` | LockCommand | App→Dev | §4.3 |
| `0x90` | CommandResponse | Dev→App | §4.4 |
| `0x20` | AuthHello | App→Dev | §9.2 (v0.3〜) |
| `0xA0` | AuthAck | Dev→App | §9.2 (v0.3〜) |
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

### 4.2 CardEventsResponse (`0x82`) — v0.4

| サイズ | フィールド |
|---|---|
| 4 | BootId — イベント連番の世代ID。デバイス再起動・連番リセット時に必ず変化させる(単調増加・乱数・起動時刻等、実装自由) |
| 1 | Count |
| Count×可変 | Events[] |

Event: EventSeq(4) + TimestampUnixSec(4) + CardIdLen(1) + CardId(N, UTF-8) + Result(1: 0=Denied,1=Accepted)

- アプリは BootId 変化を検出したら `LastSeq` カーソルを 0 にリセットし、新世代のイベントを先頭から再取得する。TCP切断・再接続のみではカーソルを維持する(取得済みイベントの重複・欠落防止)。

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

- 送受信フレームをデバイス毎の `logs/comm-<DeviceName>-YYYYMMDD.log` にJSON Linesで記録: `{ts, dev, dir, type, seq, len, crcOk, note}`
- 再接続・エラーも同ファイルに記録(note="connect"/"disconnect"/"error")
- ローテーション: 日次 + 1ファイル最大 16MB(超過で連番ファイル)

## 7. ポーリング・制御ルール

- ポーリング周期: 既定 `1000ms`(設定可能、最小200ms)。PendingCardEvents>0で即GetCardEvents
- 制御コマンド: ポーリングとは別に即時送信。応答 `500ms` 待ち。Result≠0は理由をUI通知
- ドア開きっぱなし: 開状態が閾値(既定60秒)超過、またはデバイスbit0アラート → UIアラート(冗長監視)

## 8. 確定既定値(2026-11-02 PO承認済み)

| 項目 | 確定値 |
|---|---|
| ポート番号 | `50001` |
| ポーリング周期 | `1秒` |
| ドア開きっぱなし閾値 | `60秒` |
| 複数デバイス | **必須**(§10) |
| 認証/暗号化 | **必須**(§9) |

## 9. 認証・暗号(v0.3〜, 必須)

### 9.1 2層構成

1. **TLS**(任意だが推奨): TCP接続直後に `SslStream` で TLS 1.2/1.3 ハンドシェイク。デバイスは自己署名証明書を許容し、アプリ側は**証明書サムプリントピン留め**で正当性を確認(CAモデルではなくピンが信頼判定)。復号チェーン検証を使う場合はサムプリント未指定で通常検証。
2. **PSK相互認証**(必須): 共有秘密(256bit, 設定 `PskHex`)による HMAC-SHA256 チャレンジ/レスポンス。TLSの有無にかかわらず実施。未認証のままではデバイスは制御・監視コマンドを受理しない。

### 9.2 ハンドシェイク

- `AuthHello(0x20)`: App→Dev、payload = `nonce(16B) || clientProof(32B)`、clientProof = `HMAC-SHA256(psk, 0x01 || nonce)`
- `AuthAck(0xA0)`: Dev→App、payload = `serverProof(32B)` = `HMAC-SHA256(psk, 0x02 || nonce)`
- ドメイン分離タグ(client=0x01, server=0x02)で反射攻撃を防止。双方検証で**相互**認証
- リプレイ対策: デバイスは受理済みnonceを記録し再利用を拒否(ErrorResponse code=5)。平文時の盗聴リプレイはTLS(§9.1)を併用することで根本対策とする
- 失敗: デバイスは ErrorResponse(code=4) または接続切断 → アプリは再接続バックオフして再試行(Upにはしない)
- 実装: `src/Monitoring.Protocol/AuthCode.cs`

### 9.3 設定

`appsettings.json` のデバイス毎に: `UseTls`, `TargetHost`, `CertThumbprint`, `PskHex`, `AllowInsecure`。

**認証/暗号は必須**: `UseTls` または `PskHex` の未設定は起動エラー(フェイルクローズ)。実験・デモ用途のみ `AllowInsecure: true` の明示指定で平文/無認証を許容する。証明書ピン留め使用時はピンが信頼判定のため失効チェーン確認は行わない。ピン未指定の通常CA検証時はオンライン失効確認を行う。

## 10. 複数デバイス(v0.3〜, 必須)

- `appsettings.json` の `Devices` 配列で複数デバイスを定義(後方互換のため旧 `Device` 単独キーも受理)
- デバイス毎に独立した `TcpDeviceClient` + `DeviceMonitor` を生成し、接続・再接続・認証は完全に独立(1台の障害が他デバイスに影響しない)
- GUIはデバイスセレクタで表示対象を切替。デバイス名は `Name`(省略時 `host:port`)
