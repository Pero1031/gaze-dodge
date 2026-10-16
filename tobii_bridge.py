# -*- coding: utf-8 -*-
"""
Tobii Pro アイトラッカーの視線データをブラウザのゲーム(index.html)へ中継するブリッジ。

- http://localhost:8000      ゲーム本体（このフォルダを静的配信）
- ws://localhost:8765        視線データ / キャリブレーション用 WebSocket

実行:
    python -X utf8 tobii_bridge.py           # Tobii 実機
    python -X utf8 tobii_bridge.py --mock    # 実機なし（マウスカーソル位置を視線として送る）

座標は Tobii の display area 基準の正規化座標(0-1)。display area は
Tobii Pro Eye Tracker Manager の「Display setup」で設定したモニター全体なので、
ゲームはそのモニターで全画面表示して遊ぶ。

WebSocket プロトコル (JSON):
  server -> client
    {"type":"hello", "mode":"tobii"|"mock", "model":str, "freq":float}
    {"type":"gaze", "x":0-1, "y":0-1, "valid":bool}
    {"type":"calib_point_done", "ok":bool}
    {"type":"calib_result", "ok":bool, "points":int, "error":正規化距離の平均 or null}
    {"type":"error", "message":str}
  client -> server
    {"type":"calib_start"}
    {"type":"calib_point", "x":0-1, "y":0-1}
    {"type":"calib_compute"}
    {"type":"calib_abort"}
"""

import argparse
import asyncio
import functools
import http.server
import json
import math
import sys
import threading
import time
from pathlib import Path

from websockets.asyncio.server import broadcast, serve

HERE = Path(__file__).resolve().parent
SEND_HZ = 120          # ブラウザへ送る最大レート（トラッカーの周波数がこれ以下ならその周波数）


# ---------------------------------------------------------------------------
# 視線ソース
# ---------------------------------------------------------------------------
class TobiiSource:
    mode = "tobii"

    def __init__(self, timeout_sec=10):
        import tobii_research as tr
        self.tr = tr
        start = time.time()
        found = []
        while time.time() - start < timeout_sec:
            found = tr.find_all_eyetrackers()
            if found:
                break
            print("  ...アイトラッカー検出待機中")
            time.sleep(1)
        if not found:
            raise RuntimeError(
                "アイトラッカーが見つかりませんでした。USB 接続と Eye Tracker Manager での認識を確認してください。"
                "（実機なしで試す場合は --mock）")
        self.et = found[0]
        self.model = self.et.model
        self.freq = self.et.get_gaze_output_frequency()
        self.latest = None     # (x, y, valid)
        self.seq = 0
        self.calib = None

    def start(self):
        self.et.subscribe_to(self.tr.EYETRACKER_GAZE_DATA, self._on_gaze, as_dictionary=True)

    def stop(self):
        self.et.unsubscribe_from(self.tr.EYETRACKER_GAZE_DATA, self._on_gaze)
        if self.calib is not None:
            self.calib_abort()

    # SDK のスレッドから呼ばれる。有効な眼の平均を注視点とする
    def _on_gaze(self, g):
        pts = [g[f"{eye}_gaze_point_on_display_area"] for eye in ("left", "right")
               if g[f"{eye}_gaze_point_validity"] == 1]
        if pts:
            x = sum(p[0] for p in pts) / len(pts)
            y = sum(p[1] for p in pts) / len(pts)
            self.latest = (x, y, True)
        elif self.latest is not None:
            self.latest = (self.latest[0], self.latest[1], False)
        else:
            self.latest = (0.5, 0.5, False)
        self.seq += 1

    # --- キャリブレーション（blocking。executor から呼ぶ） ---
    def calib_start(self):
        if self.calib is None:
            self.calib = self.tr.ScreenBasedCalibration(self.et)
            self.calib.enter_calibration_mode()

    def calib_point(self, x, y):
        # SDK 推奨どおり、失敗したら 1 回だけ取り直す
        status = self.calib.collect_data(x, y)
        if status != self.tr.CALIBRATION_STATUS_SUCCESS:
            status = self.calib.collect_data(x, y)
        return status == self.tr.CALIBRATION_STATUS_SUCCESS

    def calib_compute(self):
        result = self.calib.compute_and_apply()
        ok = result.status == self.tr.CALIBRATION_STATUS_SUCCESS
        errors = []
        for cp in result.calibration_points:
            tx, ty = cp.position_on_display_area
            for s in cp.calibration_samples:
                for eye in (s.left_eye, s.right_eye):
                    if eye.validity == self.tr.VALIDITY_VALID_AND_USED:
                        ex, ey = eye.position_on_display_area
                        errors.append(math.hypot(ex - tx, ey - ty))
        self.calib.leave_calibration_mode()
        self.calib = None
        return {"ok": ok, "points": len(result.calibration_points),
                "error": (sum(errors) / len(errors)) if errors else None}

    def calib_abort(self):
        if self.calib is not None:
            self.calib.leave_calibration_mode()
            self.calib = None


class MockSource:
    """実機なしの動作確認用。プライマリモニター上のマウスカーソル位置を視線とみなす。"""
    mode = "mock"
    model = "mock (mouse cursor)"
    freq = 60.0

    def __init__(self):
        import ctypes
        import ctypes.wintypes
        self.ctypes = ctypes
        self.user32 = ctypes.windll.user32
        self.user32.SetProcessDPIAware()
        self.latest = None
        self.seq = 0
        self._stop = threading.Event()

    def start(self):
        threading.Thread(target=self._poll, daemon=True).start()

    def stop(self):
        self._stop.set()

    def _poll(self):
        pt = self.ctypes.wintypes.POINT()
        sw, sh = self.user32.GetSystemMetrics(0), self.user32.GetSystemMetrics(1)
        while not self._stop.is_set():
            self.user32.GetCursorPos(self.ctypes.byref(pt))
            self.latest = (pt.x / sw, pt.y / sh, True)
            self.seq += 1
            time.sleep(1 / self.freq)

    def calib_start(self): pass
    def calib_point(self, x, y): time.sleep(0.5); return True
    def calib_compute(self): return {"ok": True, "points": 5, "error": 0.0}
    def calib_abort(self): pass


# ---------------------------------------------------------------------------
# サーバー
# ---------------------------------------------------------------------------
class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


class ExclusiveHTTPServer(http.server.ThreadingHTTPServer):
    # Windows の SO_REUSEADDR は使用中のポートにも bind できてしまうので無効化する
    allow_reuse_address = False


PORT_IN_USE_MSG = ("ポート {port} は使用中です。tobii_bridge.py がすでに別のターミナルで動いていないか確認し、"
                   "そちらを Ctrl+C で止めてから起動してください。（別ポートで起動する場合は {opt}）")


def start_http(port):
    handler = functools.partial(QuietHandler, directory=str(HERE))
    try:
        httpd = ExclusiveHTTPServer(("127.0.0.1", port), handler)
    except OSError:
        raise RuntimeError(PORT_IN_USE_MSG.format(port=port, opt="--http-port 8001"))
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


async def main(args):
    src = MockSource() if args.mock else TobiiSource()
    print(f"視線ソース: {src.mode} / {src.model} / {src.freq} Hz")

    clients = set()
    loop = asyncio.get_running_loop()
    calib_lock = asyncio.Lock()

    async def handler(ws):
        clients.add(ws)
        print(f"ブラウザ接続 ({len(clients)})")
        try:
            await ws.send(json.dumps({"type": "hello", "mode": src.mode,
                                      "model": src.model, "freq": src.freq}))
            async for raw in ws:
                msg = json.loads(raw)
                t = msg.get("type")
                try:
                    async with calib_lock:
                        if t == "calib_start":
                            await loop.run_in_executor(None, src.calib_start)
                        elif t == "calib_point":
                            ok = await loop.run_in_executor(None, src.calib_point, msg["x"], msg["y"])
                            await ws.send(json.dumps({"type": "calib_point_done", "ok": ok}))
                        elif t == "calib_compute":
                            res = await loop.run_in_executor(None, src.calib_compute)
                            print(f"キャリブレーション結果: {res}")
                            await ws.send(json.dumps({"type": "calib_result", **res}))
                        elif t == "calib_abort":
                            await loop.run_in_executor(None, src.calib_abort)
                except Exception as e:  # SDK エラーはブラウザに伝えて継続
                    print(f"エラー: {e}")
                    await ws.send(json.dumps({"type": "error", "message": str(e)}))
        finally:
            clients.discard(ws)
            print(f"ブラウザ切断 ({len(clients)})")

    async def pump():
        last = -1
        interval = 1 / min(SEND_HZ, src.freq or SEND_HZ)
        while True:
            if src.seq != last and src.latest and clients:
                last = src.seq
                x, y, valid = src.latest
                broadcast(clients, json.dumps({"type": "gaze", "x": x, "y": y, "valid": valid}))
            await asyncio.sleep(interval)

    httpd = start_http(args.http_port)
    try:
        server = await serve(handler, "127.0.0.1", args.ws_port)
    except OSError:
        httpd.shutdown()
        raise RuntimeError(PORT_IN_USE_MSG.format(port=args.ws_port, opt="--ws-port 8766、ゲームは http://localhost:8000/?ws=8766 で開く"))
    async with server:
        src.start()
        print(f"ゲーム: http://localhost:{args.http_port}  （Ctrl+C で終了）")
        try:
            await pump()
        finally:
            src.stop()


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--mock", action="store_true", help="実機なしでマウスカーソルを視線として送る")
    ap.add_argument("--http-port", type=int, default=8000)
    ap.add_argument("--ws-port", type=int, default=8765)
    try:
        asyncio.run(main(ap.parse_args()))
    except KeyboardInterrupt:
        pass
    except RuntimeError as e:
        print(f"NG: {e}")
        sys.exit(1)
