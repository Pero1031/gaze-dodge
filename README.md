# Gaze Dodge

視線でプレイヤーを動かし、降ってくる障害物を避けるブラウザゲームです。
入力は **Tobii Pro アイトラッカー**（推奨）、Webカメラ（[WebGazer.js](https://webgazer.cs.brown.edu/)）、マウス（テスト用）から選べます。

## Tobii で遊ぶ

ブラウザは Tobii に直接アクセスできないため、`tobii_bridge.py` が Tobii Pro SDK（`tobii_research`）で視線を取得し、
WebSocket（`ws://localhost:8765`）でゲームへ送ります。ゲーム本体も同じスクリプトが `http://localhost:8000` で配信します。

```
Tobii Pro ──(tobii_research)──> tobii_bridge.py ──(WebSocket)──> ブラウザ (index.html)
```

1. Tobii を接続し、Tobii Pro Eye Tracker Manager で認識・Display setup ができていることを確認
2. `run_tobii.bat` をダブルクリック（または `python -X utf8 tobii_bridge.py`）
3. ブラウザで http://localhost:8000 を開き「Tobii で始める」
4. 全画面になるので「キャリブレーション開始」→ 5 つの点を順に見つめる（点が縮みきるまで）
   - 「スキップ」すると Eye Tracker Manager 等で済ませた現在の校正をそのまま使う
5. 誤差を確認して「ゲーム開始」

注意:
- Tobii の座標は Display setup で設定したモニター全体が基準です。ゲームは **そのモニターで全画面** で遊んでください（全画面でない場合はウィンドウ位置から概算します）
- 目を検出できない間は画面上部に「目を検出できません」と出て、プレイヤーはその場で止まります
- 必要パッケージ: `tobii-research`, `websockets`（このPCには導入済み）

### 実機なしで試す

```
python -X utf8 tobii_bridge.py --mock
```

マウスカーソル位置を視線として送ります（キャリブレーションは常に成功扱い）。

## Webカメラ / マウスで遊ぶ

上と同じく http://localhost:8000 で開き、「Webカメラで始める」または「マウス」を選びます。
Webカメラモードは画面の 9 点を見つめながら各 5 回クリックして校正します。

## 調整ポイント（index.html）

- `GAZE_SMOOTH`：入力ごとの視線の平滑化（小さいほど滑らかだが遅れる）
- `movePlayer` の `maxSpeed`：入力ごとのプレイヤー最大速度
- `CALIB_POINTS`：Tobii キャリブレーション点（9 点にしたければ追加）
- `difficulty()`：難易度の上がり方
