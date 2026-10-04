<p align="center"><img src="docs/icon.png" width="96" alt="ぽくミラー"></p>

# ぽくミラー for iPhone

iPhone の画面を Windows PC に映して（画面ミラーリング）、PC のマウスで iPhone を操作できるようにするツールです。
iPhone 側にアプリを入れる必要はありません。

- **画面ミラーリング**: iPhone の「画面ミラーリング」で PC に映します（受信には [UxPlay](https://github.com/FDH2/UxPlay) を使っています）
- **PC のマウスで操作**（任意）: USB でつないだ **ESP32 ボード**が Bluetooth マウスとして iPhone に動きを伝えます
  - 左クリック = タップ、ドラッグ = スワイプ、ホイール = スクロール、Shift を押しながら = ゆっくり
  - 画面の縦／横の切り替えにも対応
- ESP32 がなくても、ミラーリングだけのアプリとして使えます
- 黒い画面は出さず、タスクバー右下のアイコンとして常駐。初回はセットアップのウィザードが案内します

## ダウンロード

[Releases](../../releases) から `PokuMirror-v*.zip` をダウンロードして展開し、`PokuMirror.exe` を起動してください。
使い方は zip 内の「はじめにお読みください.txt」にあります。

### 必要なもの

- Windows 10 / 11（64bit）
- iPhone（PC と同じ Wi-Fi につながっていること）
- マウスで操作する場合のみ: ESP32 ボード（ESP32-DevKitC、Freenove ESP32 など。ESP32-S2 は Bluetooth がないため不可）と、データ通信対応の USB ケーブル

## しくみ

```
iPhone ──(AirPlay 画面ミラーリング)──▶ PC: UxPlay が映像を表示
  ▲                                         │
  │ Bluetooth マウス (BLE HID)               │ PC のマウス操作をフックして、移動量を USB シリアルで送る
  └──────────── ESP32 ◀────(USB)──────── PokuMirror.exe
```

- iPhone は AssistiveTouch をオンにすると Bluetooth マウスのポインタを表示します
- iPhone はマウスがつながった時点の画面の向きでポインタの向きと動ける範囲を決めるため、
  画面の縦横が変わると ESP32 を再起動してつなぎ直し、ポインタを画面中央へ移動します

## フォルダ構成

| フォルダ | 内容 |
| --- | --- |
| `app/` | PC アプリ（C# / .NET 8 / WinForms）。`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` で単体 exe になります |
| `firmware/esp32-mouse/` | ESP32 用プログラム（Arduino。ライブラリ: NimBLE-Arduino 2.x、ボード: esp32 3.x） |
| `scripts/` | ESP32 への書き込み用バッチ（esptool を使用） |
| `uxplay/` | UxPlay の起動用スクリプトと、iPhone の回転に合わせてウィンドウの形を変える補助スクリプト |
| `docs/` | アイコン、紹介ページ |

配布用 zip には、このほかに UxPlay と GStreamer などのライブラリ（MSYS2 由来）、esptool、書き込み用のファームウェア（.bin）を同梱しています。

## ライセンス

[GNU General Public License v3](LICENSE)

使用しているソフトウェア: UxPlay（GPLv3）、esptool（GPLv2）、NimBLE-Arduino（Apache 2.0）、Arduino core for ESP32（LGPL 2.1）/ ESP-IDF（Apache 2.0）、.NET（MIT）、GStreamer / FFmpeg ほか（各ライセンス）

本ツールは無保証です。iPhone、AirPlay は Apple Inc. の商標です。
