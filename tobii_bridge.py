# -*- coding: utf-8 -*-
"""
Tobii Pro アイトラッカーの視線データをブラウザのゲーム(index.html)へ中継するブリッジ。

- http://localhost:8000      ゲーム本体（このフォルダを静的配信）
- ws://localhost:8765        視線データ / キャリブレーション用 WebSocket

実行:
    python -X utf8 tobii_bridge.py           # Tobii 実機
    python -X utf8 tobii_bridge.py --mock    # 実機なし（マウスカーソル位置を視線として送る）
    python -X utf8 tobii_bridge.py --open    # 準備ができたらゲーム専用ウィンドウ（全画面）を開く

終了:
    ゲームで ESC を押すと、ブラウザが shutdown を送り、ブリッジはキャリブレーション中止・Tobii の受信停止・
    ポートの解放・専用ウィンドウを閉じる処理をしてから終了する。専用ウィンドウが閉じられた場合や Ctrl+C でも同じ。

座標は Tobii の display area 基準の正規化座標(0-1)。display area は
Tobii Pro Eye Tracker Manager の「Display setup」で設定したモニター全体なので、
ゲームはそのモニターで全画面表示して遊ぶ。

WebSocket プロトコル (JSON):
  server -> client
    {"type":"hello", "mode":"tobii"|"mock", "model":str, "freq":float}
    {"type":"gaze", "x":0-1, "y":0-1, "valid":bool}
    {"type":"calib_point_done", "ok":bool}
    {"type":"calib_result", "ok":bool, "points":int, "error":正規化距離の平均 or null}
    {"type":"ipd_result", "ipd":mm or null, "n":サンプル数}
    {"type":"players", "players":[{id, name, ipd, best, plays}]}  # best の降順
    {"type":"score_saved", "best":int, "is_best":bool, "rank":int, "players":[...]}
    {"type":"error", "message":str}
  client -> server
    {"type":"calib_start"}
    {"type":"calib_point", "x":0-1, "y":0-1}
    {"type":"calib_compute"}
    {"type":"calib_abort"}
    {"type":"ipd_measure", "seconds":float}      # 両目の間隔(瞳孔間距離)を測る
    {"type":"players_get"}
    {"type":"player_add", "name":str, "ipd":mm or null}   -> players
    {"type":"player_delete", "id":str}                    -> players
    {"type":"score_add", "id":str, "score":int, "seconds":float} -> score_saved
    {"type":"shutdown"}                         -> bye を返してからブリッジを終了する

プレイヤーとスコアは data/players.json に保存する（名前と両目の間隔の数値のみ。画像は扱わない）。
"""

import argparse
import asyncio
import functools
import http.server
import json
import math
import os
import random
import statistics
import subprocess
import sys
import threading
import time
import uuid
import webbrowser
from collections import deque
from pathlib import Path

from websockets.asyncio.server import broadcast, serve

HERE = Path(__file__).resolve().parent
DATA_FILE = HERE / "data" / "players.json"


# ---------------------------------------------------------------------------
# 視線ソース
# ---------------------------------------------------------------------------
class SourceBase:
    """視線ソース共通部分。
    - 新しいサンプルが来たら listener を呼ぶ（ブリッジがすぐブラウザへ送る。ポーリングによる待ちをなくす）
    - 両目の間隔（瞳孔間距離, mm）の直近サンプルを溜めて、測定時に中央値を返す
    """
    listener = None     # サンプル到着時に（SDK/ポーリングのスレッドから）呼ばれる

    def _notify(self):
        self.seq += 1
        if self.listener is not None:
            self.listener()

    def _init_ipd(self):
        self.ipd_samples = deque(maxlen=2000)

    def measure_ipd(self, seconds):
        # blocking。executor から呼ぶ
        self.ipd_samples.clear()
        time.sleep(seconds)
        vals = list(self.ipd_samples)
        return {"ipd": round(statistics.median(vals), 2) if len(vals) >= 10 else None, "n": len(vals)}


class TobiiSource(SourceBase):
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
        self._init_ipd()

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
        # 両目の 3 次元位置(gaze origin, mm)の距離 = 両目の間隔。キャリブレーションに依存しない
        if g["left_gaze_origin_validity"] == 1 and g["right_gaze_origin_validity"] == 1:
            self.ipd_samples.append(math.dist(g["left_gaze_origin_in_user_coordinate_system"],
                                              g["right_gaze_origin_in_user_coordinate_system"]))
        self._notify()

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


class MockSource(SourceBase):
    """実機なしの動作確認用。プライマリモニター上のマウスカーソル位置を視線とみなす。
    両目の間隔は ipd(mm) にノイズを加えた値を返す（--mock-ipd で別人を模擬できる）。"""
    mode = "mock"
    model = "mock (mouse cursor)"
    freq = 60.0

    # --mock-blink のときの瞬きの再現（実機で見られる形）：
    # 目が閉じ始めると視線が下にずれ（BLINK_DRIFT_PX まで）、閉じている間は無効、開いた直後もしばらく下にずれる
    BLINK_PERIOD, BLINK_PRE, BLINK_CLOSED, BLINK_POST, BLINK_DRIFT_PX = 3.0, 0.05, 0.20, 0.08, 250

    def __init__(self, ipd=63.0, blink=False, pos=None):
        import ctypes
        import ctypes.wintypes
        self.ctypes = ctypes
        self.user32 = ctypes.windll.user32
        self.user32.SetProcessDPIAware()
        self.latest = None
        self.seq = 0
        self._stop = threading.Event()
        self.ipd = ipd
        self.blink = blink
        self.pos = pos          # (x, y) 0-1 を指定するとカーソルの代わりにその位置を見続ける
        self._init_ipd()

    def start(self):
        threading.Thread(target=self._poll, daemon=True).start()

    def _blink_offset(self, now):
        """(下へのずれ px, 有効か)。瞬きは BLINK_PERIOD 秒ごと。"""
        if not self.blink:
            return 0.0, True
        ph = now % self.BLINK_PERIOD
        pre, closed, post = self.BLINK_PRE, self.BLINK_CLOSED, self.BLINK_POST
        if ph < pre:
            return self.BLINK_DRIFT_PX * ph / pre, True
        if ph < pre + closed:
            return 0.0, False
        if ph < pre + closed + post:
            return self.BLINK_DRIFT_PX * (1 - (ph - pre - closed) / post), True
        return 0.0, True

    def stop(self):
        self._stop.set()

    def _poll(self):
        pt = self.ctypes.wintypes.POINT()
        sw, sh = self.user32.GetSystemMetrics(0), self.user32.GetSystemMetrics(1)
        while not self._stop.is_set():
            if self.pos:
                pt.x, pt.y = int(self.pos[0] * sw), int(self.pos[1] * sh)
            else:
                self.user32.GetCursorPos(self.ctypes.byref(pt))
            drift, valid = self._blink_offset(time.time())
            self.latest = (pt.x / sw, (pt.y + drift) / sh, valid)
            self.ipd_samples.append(random.gauss(self.ipd, 0.3))
            self._notify()
            time.sleep(1 / self.freq)

    def calib_start(self): pass
    def calib_point(self, x, y): time.sleep(0.5); return True
    def calib_compute(self): return {"ok": True, "points": 5, "error": 0.0}
    def calib_abort(self): pass


# ---------------------------------------------------------------------------
# プレイヤー・スコア保存
# ---------------------------------------------------------------------------
class PlayerStore:
    def __init__(self, path):
        self.path = path
        if path.exists():
            self.data = json.loads(path.read_text(encoding="utf-8"))
        else:
            self.data = {"players": [], "scores": []}

    def _save(self):
        self.path.parent.mkdir(exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(self.data, ensure_ascii=False, indent=1), encoding="utf-8")
        tmp.replace(self.path)

    def players(self):
        """ベストスコアの降順（同点・未プレイは登録順）。"""
        out = []
        for p in self.data["players"]:
            scores = [s["score"] for s in self.data["scores"] if s["player_id"] == p["id"]]
            out.append({"id": p["id"], "name": p["name"], "ipd": p.get("ipd"),
                        "best": max(scores, default=0), "plays": len(scores)})
        return sorted(out, key=lambda p: -p["best"])

    def add(self, name, ipd):
        name = str(name).strip()[:20]
        if not name:
            raise ValueError("名前が空です")
        if any(p["name"] == name for p in self.data["players"]):
            raise ValueError(f"「{name}」はすでに登録されています")
        self.data["players"].append({"id": uuid.uuid4().hex[:8], "name": name, "ipd": ipd,
                                     "created": time.strftime("%Y-%m-%d %H:%M:%S")})
        self._save()

    def delete(self, pid):
        self.data["players"] = [p for p in self.data["players"] if p["id"] != pid]
        self.data["scores"] = [s for s in self.data["scores"] if s["player_id"] != pid]
        self._save()

    def add_score(self, pid, score, seconds):
        if not any(p["id"] == pid for p in self.data["players"]):
            raise ValueError("プレイヤーが見つかりません")
        prev = max((s["score"] for s in self.data["scores"] if s["player_id"] == pid), default=0)
        self.data["scores"].append({"player_id": pid, "score": int(score), "seconds": round(float(seconds), 2),
                                    "ts": time.strftime("%Y-%m-%d %H:%M:%S")})
        self._save()
        players = self.players()
        rank = next(i for i, p in enumerate(players, 1) if p["id"] == pid)
        return {"best": max(prev, int(score)), "is_best": int(score) > prev, "rank": rank, "players": players}


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


def disable_console_quick_edit():
    """Windows のコンソールで「簡易編集モード」を切る。
    有効なままだとウィンドウ内をクリックしただけで出力(print)が止まり、ブリッジ全体が固まる。"""
    if sys.platform != "win32":
        return
    import ctypes
    k32 = ctypes.windll.kernel32
    h = k32.GetStdHandle(-10)               # STD_INPUT_HANDLE
    mode = ctypes.c_uint32()
    if k32.GetConsoleMode(h, ctypes.byref(mode)):   # コンソールでなければ何もしない
        ENABLE_QUICK_EDIT_MODE, ENABLE_EXTENDED_FLAGS = 0x0040, 0x0080
        k32.SetConsoleMode(h, (mode.value & ~ENABLE_QUICK_EDIT_MODE) | ENABLE_EXTENDED_FLAGS)


# ---------------------------------------------------------------------------
# ゲーム専用ウィンドウ
# ---------------------------------------------------------------------------
# 普段使いのブラウザのタブはページから閉じられないので、専用のプロファイルでアプリ用ウィンドウを開き、
# 終了時にブリッジが閉じる。ブラウザ自体の全画面（--start-fullscreen）にしておくと、
# ESC がページに届く（ページの全画面 API だと ESC はブラウザが全画面解除に使ってしまう）。
_PF, _PF86, _LAD = (os.environ.get(k, "") for k in ("ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"))
BROWSER_CANDIDATES = [   # 上から順に探す（Chrome → Edge）
    Path(_PF) / "Google/Chrome/Application/chrome.exe",
    Path(_PF86) / "Google/Chrome/Application/chrome.exe",
    Path(_LAD) / "Google/Chrome/Application/chrome.exe",
    Path(_PF86) / "Microsoft/Edge/Application/msedge.exe",
    Path(_PF) / "Microsoft/Edge/Application/msedge.exe",
]
BROWSER_PROFILE = Path(_LAD or HERE) / "GazeDodge" / "browser_profile"


class GameWindow:
    """専用プロファイルのブラウザ（= ゲーム専用ウィンドウ）を開き、閉じる。
    Chrome は起動直後に自分を別プロセスとして立ち上げ直すことがあるので、起動したプロセスの PID ではなく
    「専用プロファイルを使っているブラウザのプロセス」を psutil で探して見張る。
    全プロセスのコマンドラインを読むのは遅い（約 0.6 秒）ので、見つけたメインのプロセスを覚えておき、
    ふだんはその生存確認だけをする。どの処理も時間がかかりうるので、イベントループではなく別スレッドで呼ぶこと。"""

    BROWSER_NAMES = {"chrome.exe", "msedge.exe"}

    def __init__(self, url):
        exe = next((p for p in BROWSER_CANDIDATES if p.is_file()), None)
        self.managed = False
        self.main = None        # 見つけたメインのブラウザプロセス（psutil.Process）
        self.seen = False       # 一度でも動いているのを確認したか
        self.missing = 0        # 続けて見つからなかった回数
        if exe is None:
            webbrowser.open(url)
            print("Chrome / Edge が見つからないため、既定のブラウザで開きました（終了時にブラウザは自動で閉じません）")
            return
        subprocess.Popen([
            str(exe), f"--app={url}", f"--user-data-dir={BROWSER_PROFILE}", "--start-fullscreen",
            "--no-first-run", "--no-default-browser-check", "--disable-session-crashed-bubble",
        ])
        self.managed = True
        print(f"ゲーム専用ウィンドウを開きました（{exe.stem}）")

    @classmethod
    def _procs(cls, main_only):
        """専用プロファイルを使っているブラウザのプロセス（main_only なら子プロセス --type=... を除く）。
        名前で Chrome / Edge に絞ってからコマンドラインを読む（全プロセスを読むと遅い）。"""
        import psutil
        key = f"--user-data-dir={BROWSER_PROFILE}"
        found = []
        for p in psutil.process_iter(["name"]):
            if (p.info["name"] or "").lower() not in cls.BROWSER_NAMES:
                continue
            try:
                cmd = p.cmdline()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
            if key in cmd and not (main_only and any(a.startswith("--type=") for a in cmd)):
                found.append(p)
        return found

    def is_closed(self):
        """1 秒ごとに（別スレッドから）呼ぶ。一度動いているのを見たあと、2 回続けて見つからなければ「閉じられた」とみなす。"""
        if not self.managed:
            return False
        if self.main is not None and self.main.is_running():   # ふだんはこれだけ（一瞬で終わる）
            return False
        mains = self._procs(main_only=True)                     # まだ見つけていない／いなくなったときだけ探す
        if mains:
            self.main, self.seen, self.missing = mains[0], True, 0
            return False
        self.main = None
        self.missing += 1
        return self.seen and self.missing >= 2

    def close(self):
        """まず普通に閉じる（ウィンドウに閉じる指示）→ 5 秒たっても残っていれば強制終了。"""
        if not self.managed:
            return
        import psutil
        mains = self._procs(main_only=True)
        if not mains and not self._procs(main_only=False):
            return
        for p in mains:
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T"], capture_output=True)
        _, alive = psutil.wait_procs(self._procs(main_only=False), timeout=5)
        for p in alive:
            try:
                p.kill()
            except psutil.NoSuchProcess:
                pass
        print("ゲーム専用ウィンドウを閉じました" + ("（強制終了あり）" if alive else ""))


def start_http(port):
    handler = functools.partial(QuietHandler, directory=str(HERE))
    try:
        httpd = ExclusiveHTTPServer(("127.0.0.1", port), handler)
    except OSError:
        raise RuntimeError(PORT_IN_USE_MSG.format(port=port, opt="--http-port 8001"))
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


async def main(args):
    mock_pos = tuple(float(v) for v in args.mock_pos.split(",")) if args.mock_pos else None
    src = MockSource(args.mock_ipd, args.mock_blink, mock_pos) if args.mock else TobiiSource()
    store = PlayerStore(Path(args.data))
    print(f"視線ソース: {src.mode} / {src.model} / {src.freq} Hz")

    clients = set()
    loop = asyncio.get_running_loop()
    calib_lock = asyncio.Lock()
    stop = asyncio.Event()      # セットされたら終了処理へ

    async def handler(ws):
        clients.add(ws)
        print(f"ブラウザ接続 ({len(clients)})")
        try:
            await ws.send(json.dumps({"type": "hello", "mode": src.mode,
                                      "model": src.model, "freq": src.freq}))
            async for raw in ws:
                msg = json.loads(raw)
                t = msg.get("type")
                if t == "shutdown":     # キャリブレーション中でも待たずに受け付ける
                    print("ゲームから終了の依頼を受けました")
                    await ws.send(json.dumps({"type": "bye"}))
                    stop.set()
                    continue
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
                        elif t == "ipd_measure":
                            res = await loop.run_in_executor(None, src.measure_ipd, float(msg.get("seconds", 2)))
                            await ws.send(json.dumps({"type": "ipd_result", **res}))
                        elif t in ("players_get", "player_add", "player_delete"):
                            if t == "player_add":
                                store.add(msg["name"], msg.get("ipd"))
                            elif t == "player_delete":
                                store.delete(msg["id"])
                            await ws.send(json.dumps({"type": "players", "players": store.players()}))
                        elif t == "score_add":
                            res = store.add_score(msg["id"], msg["score"], msg["seconds"])
                            await ws.send(json.dumps({"type": "score_saved", **res}))
                except Exception as e:  # SDK エラーはブラウザに伝えて継続
                    print(f"エラー: {e}")
                    await ws.send(json.dumps({"type": "error", "message": str(e)}))
        finally:
            clients.discard(ws)
            print(f"ブラウザ切断 ({len(clients)})")

    # サンプルが届いたらすぐ送る（以前は一定間隔のポーリングで、平均 5 ms ほど余計に待っていた）。
    # SDK のスレッドから呼ばれるので、送信はイベントループに渡す
    def push():
        if clients and src.latest:
            x, y, valid = src.latest
            broadcast(clients, json.dumps({"type": "gaze", "x": x, "y": y, "valid": valid}))

    def on_sample():
        try:
            loop.call_soon_threadsafe(push)
        except RuntimeError:    # 終了処理中でループが閉じている
            pass

    httpd = start_http(args.http_port)
    try:
        server = await serve(handler, "127.0.0.1", args.ws_port)
    except OSError:
        httpd.shutdown()
        raise RuntimeError(PORT_IN_USE_MSG.format(port=args.ws_port, opt="--ws-port 8766、ゲームは http://localhost:8000/?ws=8766 で開く"))
    window = None
    try:
        async with server:
            src.listener = on_sample
            src.start()
            url = f"http://localhost:{args.http_port}" + (f"/?ws={args.ws_port}" if args.ws_port != 8765 else "")
            print(f"ゲーム: {url}  （ゲームで ESC、または Ctrl+C で終了）")
            if args.open:   # サーバーの準備ができてからゲーム専用ウィンドウを開く
                window = GameWindow(url)

            async def watch_window():   # 専用ウィンドウが閉じられたらブリッジも終わる
                while not stop.is_set():
                    # 別スレッドで調べる（イベントループで調べると、その間は視線の送信が止まってしまう）
                    if window is not None and await loop.run_in_executor(None, window.is_closed):
                        print("ゲーム専用ウィンドウが閉じられました")
                        stop.set()
                    await asyncio.sleep(1)

            watcher = asyncio.create_task(watch_window())
            await stop.wait()
            watcher.cancel()
            print("終了処理中…")
            # ここで async with を抜けると、WebSocket の接続を閉じてポートを解放する
    finally:
        # Ctrl+C のときもここを通る。どれかが失敗しても残りは必ず行う
        src.listener = None
        for name, step in (("キャリブレーションの中止", src.calib_abort), ("視線データの受信停止", src.stop),
                           ("ゲーム専用ウィンドウを閉じる", window.close if window else None),
                           ("HTTP サーバーの停止", httpd.shutdown)):
            if step is None:
                continue
            try:
                step()
            except Exception as e:
                print(f"  {name}に失敗: {e}")
        print("終了しました")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--mock", action="store_true", help="実機なしでマウスカーソルを視線として送る")
    ap.add_argument("--mock-ipd", type=float, default=63.0, help="--mock 時の両目の間隔 [mm]（別人を模擬）")
    ap.add_argument("--mock-blink", action="store_true", help="--mock 時に 3 秒ごとの瞬き（直前直後に視線が下にずれる）を再現")
    ap.add_argument("--mock-pos", help="--mock 時にカーソルの代わりに見続ける位置（画面全体を 0-1 として 'x,y'）")
    ap.add_argument("--http-port", type=int, default=8000)
    ap.add_argument("--ws-port", type=int, default=8765)
    ap.add_argument("--open", action="store_true", help="起動完了後にブラウザでゲームを開く")
    ap.add_argument("--data", default=str(DATA_FILE), help="プレイヤー・スコアの保存先（テスト用に切り替える場合）")
    disable_console_quick_edit()
    try:
        asyncio.run(main(ap.parse_args()))
    except KeyboardInterrupt:
        pass
    except RuntimeError as e:
        print(f"NG: {e}")
        sys.exit(1)
