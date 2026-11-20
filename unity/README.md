# Gaze Dodge（Unity 版）

ブラウザ版（[../index.html](../index.html)）を Unity 2022.3 に移植したものです。
ルールと数値（揺れ補正・瞬き対策・キャリブレーションの基準・難易度・障害物）はブラウザ版と同じです。

視線・キャリブレーション・プレイヤー登録・スコア保存は、ブラウザ版と同じ `tobii_bridge.py` を使います（ブリッジは変更なし）。

```
Tobii Pro ──(tobii_research)──> tobii_bridge.py ──(WebSocket ws://127.0.0.1:8765)──> Unity（GazeDodge.exe / エディタ）
```

## 遊ぶ（exe）

1. Unity でこのフォルダを開き、メニュー **Gaze Dodge → Build Windows** で `unity/Build/GazeDodge.exe` を作る
2. `gaze-dodge/run_unity.bat` をダブルクリック（ゲームとブリッジを起動。`run_unity.bat --mock` なら実機なし）
3. キャリブレーション・プレイヤー選択などの流れはブラウザ版と同じ
4. **ESC** で終了（ブリッジも閉じる）。Alt+F4 で閉じたときは、ブリッジの黒い画面で Ctrl+C

- Tobii の座標は Display setup をしたモニター全体が基準です。ゲームはそのモニターで全画面にしてください。
  別のモニターに出てしまうときは `GazeDodge.exe -monitor 2` のように番号を指定します
- ウィンドウ表示でも、ウィンドウの位置から換算するので使えます

## エディタで開発する

- Unity Hub の「Add」でこの `unity` フォルダを追加して開く（Unity 2022.3.62f1）
- `Assets/GazeDodge/Scenes/Main.unity` を開いて再生。カメラ・UI は実行時にコードで作ります
- ブリッジは別に起動しておく（`python -X utf8 tobii_bridge.py --mock` など）
- エディタの Game ビューは画面上の位置を取れないので、Tobii の座標は全画面とみなして換算します（少しずれる）。
  エディタでは主にマウスモードで確かめ、Tobii は exe で確かめてください
- エディタでの ESC は再生を止めるだけです（ブリッジは動かしたままなので、続けて再生できる）

## exe の起動オプション

| オプション | 意味 |
|---|---|
| `-ws 8766` | ブリッジのポート（ブリッジの `--ws-port` と合わせる） |
| `-noblink` | 瞬き対策を切る（比較用。ブラウザ版の `?noblink`） |
| `-obstacles <フォルダ>` | 障害物の画像フォルダ（省略時は `gaze-dodge/obstacles`） |
| `-monitor 2` | 表示するモニター（Unity 標準のオプション） |
| `-screen-fullscreen 0 -screen-width 1280 -screen-height 720` | ウィンドウ表示（Unity 標準のオプション） |
| `-autotest <フォルダ>` | 動作確認用の自動操作。各画面の写真と記録 `autotest.txt` を残して終了する（下記） |

自動の動作確認（実機なし・本番の記録は使わない）:

```
python -X utf8 tobii_bridge.py --mock --mock-pos 0.5,0.5 --data <テスト用.json>
unity\Build\GazeDodge.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -autotest <出力フォルダ>
```

視線を画面中央に固定しているので、精度の検証は基準に届かず「このまま進む」で進むのが正常です。
終わったら、ウィンドウ表示の設定が残らないよう レジストリの `HKCU\Software\GazeDodge` を消してください。

## ブラウザ版との違い

- 入力は Tobii とマウスだけ（Webカメラ（WebGazer）はブラウザ専用なのでなし）
- 障害物の画像は PNG・JPG だけ（SVG・WebP は読めない）。画像は起動時に `gaze-dodge/obstacles/` から読み、
  Unity プロジェクトには取り込みません（GitHub に載らない扱いはブラウザ版と同じ）
- ゲストのベストは Unity の PlayerPrefs に保存（ブラウザ版とは別）
- タイトルのランキングは画面に収まる人数まで（ブラウザ版はスクロールで全員）

## ファイル

| ファイル | 内容 |
|---|---|
| `Assets/GazeDodge/Scripts/GazeDodgeGame.cs` | ゲームの中身（障害物・当たり判定・描画） |
| `Assets/GazeDodge/Scripts/GazeDodgeGame.Gaze.cs` | 視線の処理（揺れ補正・瞬き対策）・キャリブレーションと検証 |
| `Assets/GazeDodge/Scripts/GazeDodgeGame.Flow.cs` | 画面と画面遷移・プレイヤー登録・ランキング・終了 |
| `Assets/GazeDodge/Scripts/BridgeClient.cs` | ブリッジとの通信 |
| `Assets/GazeDodge/Scripts/ObstacleImages.cs` | 画像の障害物の読み込み |
| `Assets/GazeDodge/Scripts/Ui.cs`・`Gfx.cs`・`Util.cs` | UI の部品・図形・共通処理 |
| `Assets/GazeDodge/Scripts/AutoTest.cs` | 動作確認用の自動操作（`-autotest` のときだけ） |
| `Assets/GazeDodge/Editor/ProjectSetup.cs` | シーン・プレイヤー設定の作成とビルド（メニュー「Gaze Dodge」） |
