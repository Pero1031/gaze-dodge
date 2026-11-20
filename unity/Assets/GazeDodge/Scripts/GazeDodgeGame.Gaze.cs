using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GazeDodge
{
    public partial class GazeDodgeGame
    {
        // ---------- 揺れ補正（One Euro フィルタ） ----------
        // 視線が止まっているときは強くなめらかにし、速く動いたときは補正を弱めて遅れを減らす。
        //   ONE_EURO_MIN_CUTOFF [Hz]：止まっているときの揺れの抑え具合（小さいほど揺れないが遅れる）
        //   ONE_EURO_BETA：速さに応じて補正を弱める度合い（大きいほど速い動きの遅れが減る）
        const double ONE_EURO_MIN_CUTOFF = 1.5, ONE_EURO_BETA = 0.01, ONE_EURO_D_CUTOFF = 1.0;
        readonly OneEuroFilter euroX = new OneEuroFilter(ONE_EURO_MIN_CUTOFF, ONE_EURO_BETA, ONE_EURO_D_CUTOFF);
        readonly OneEuroFilter euroY = new OneEuroFilter(ONE_EURO_MIN_CUTOFF, ONE_EURO_BETA, ONE_EURO_D_CUTOFF);

        Vector2? rawGaze;               // 補正後の注視点（受信のたびに更新）

        BridgeClient bridge;
        bool BridgeOK => bridge != null && bridge.IsOpen;
        // ブリッジを --ws-port で別ポート起動した場合は -ws <port> を付けて起動する
        static int WsPort => int.TryParse(Args.Get("-ws"), out var p) ? p : 8765;

        void InitBridge()
        {
            bridge = new BridgeClient
            {
                OnGaze = (m, t) => { if (inputMode == Mode.Tobii) HandleTobiiGaze(m, t); },
                OnClosed = () =>
                {
                    if (state != State.Title) SetStatus("【注意】ブリッジとの接続が切れました（スコアは保存されません）");
                },
            };
        }

        // Tobii の視線のほか、プレイヤー登録・スコア保存もブリッジが担う（マウスモードでも使う）
        async Task ConnectBridge()
        {
            if (BridgeOK) return;
            await bridge.Connect(WsPort);
        }

        void UpdateGazeTarget()
        {
            if (inputMode != Mode.Tobii || !rawGaze.HasValue) return;
            // 受信時に One Euro で補正済み
            target = new Vector2(Mathf.Clamp(rawGaze.Value.x, 0, W), Mathf.Clamp(rawGaze.Value.y, 0, H));
        }

        // ---------- Tobii の視線サンプルの処理（瞬き対策つき） ----------
        // 瞬きの直前・直後は、まぶたが瞳孔を隠して視線が下にずれたように計測される（Tobii は「有効」として返す）。
        //  - 目を検出できないサンプルが BLINK_MIN_SAMPLES 個続いたら瞬きとみなし、
        //    注視点とプレイヤーを、瞬きが始まる BLINK_ROLLBACK_MS 前の位置に戻す（直前のずれを取り消す）
        //  - 目が再び検出されてから BLINK_POST_IGNORE_MS の間のサンプルは捨てる（直後のずれを使わない）
        // 1〜2 サンプルだけの欠けは瞬きではないので、そのまま直前の位置を保つ
        const int BLINK_MIN_SAMPLES = 3;
        const double BLINK_ROLLBACK_MS = 100, BLINK_POST_IGNORE_MS = 120;
        bool blinkFix = true;           // 比較用：-noblink で瞬き処理を切る

        struct TimedPos { public double t; public float x, y; }
        readonly List<TimedPos> gazeHist = new List<TimedPos>();     // 直近 0.5 秒の注視点（補正後）
        readonly List<TimedPos> playerHist = new List<TimedPos>();   // 直近 0.5 秒のプレイヤー位置
        int invalidRun;
        double firstInvalidT, ignoreUntil, lastValidGaze;
        bool needFilterReset;

        class ValidationBuf { public readonly List<Vector2> valid = new List<Vector2>(); public int invalid; }
        ValidationBuf validationBuf;    // キャリブレーション検証中だけ

        /// <param name="now">サンプルが届いた時刻 [ms]（Clock.Ms）</param>
        void HandleTobiiGaze(JObject m, double now)
        {
            float nx = (float?)m["x"] ?? 0.5f, ny = (float?)m["y"] ?? 0.5f;
            bool valid = (bool?)m["valid"] ?? false;
            if (validationBuf != null)
            {
                if (valid) validationBuf.valid.Add(ScreenToClient(nx, ny)); else validationBuf.invalid++;
            }
            if (!valid)
            {
                if (invalidRun++ == 0) firstInvalidT = now;
                if (blinkFix && invalidRun == BLINK_MIN_SAMPLES) RollbackTo(firstInvalidT - BLINK_ROLLBACK_MS);
                return;
            }
            if (blinkFix && invalidRun >= BLINK_MIN_SAMPLES) { ignoreUntil = now + BLINK_POST_IGNORE_MS; needFilterReset = true; }
            invalidRun = 0;
            if (now < ignoreUntil) return;
            // 瞬きのあとは補正を初期化し、瞬き前の位置から引きずらないようにする
            if (needFilterReset) { euroX.Reset(); euroY.Reset(); needFilterReset = false; }
            // 描画フレームを待たず、届いた時刻を使ってサンプルごとに（120 Hz）補正する
            var p = ScreenToClient(nx, ny);
            double t = now / 1000;
            rawGaze = new Vector2((float)euroX.Filter(p.x, t), (float)euroY.Filter(p.y, t));
            lastValidGaze = now;
            gazeHist.Add(new TimedPos { t = now, x = rawGaze.Value.x, y = rawGaze.Value.y });
            while (gazeHist[0].t < now - 500) gazeHist.RemoveAt(0);
        }

        void RollbackTo(double t)
        {
            TimedPos? LastBefore(List<TimedPos> hist)
            {
                for (int i = hist.Count - 1; i >= 0; i--) if (hist[i].t <= t) return hist[i];
                return null;
            }
            var g = LastBefore(gazeHist);
            if (!g.HasValue) return;
            rawGaze = new Vector2(g.Value.x, g.Value.y);
            target = rawGaze.Value;
            gazeHist.RemoveAll(h => h.t > t);
            var p = (state == State.Countdown || state == State.Play) && player != null ? LastBefore(playerHist) : null;
            if (p.HasValue)
            {
                player.x = p.Value.x; player.y = p.Value.y;
                player.trail.Clear();
                playerHist.RemoveAll(h => h.t > t);
            }
        }

        // ---------- Tobii の座標 ↔ 画面の px ----------
        // Tobii の座標はモニター全体（Eye Tracker Manager の Display setup）を 0-1 に正規化したもの。
        // 全画面なら画面の px と一致する。ウィンドウ表示（ビルドした exe）のときはウィンドウの位置から換算する。
        // エディタの Game ビューでは位置を取れないので全画面とみなす（ずれるので、エディタではマウスモードで確かめる）
        Vector2 ScreenToClient(float nx, float ny)
        {
            if (TryGetClientOnMonitor(out var ox, out var oy, out var mw, out var mh))
                return new Vector2(nx * mw - ox, ny * mh - oy);
            return new Vector2(nx * W, ny * H);
        }

        Vector2 ClientToScreen(float x, float y)
        {
            if (TryGetClientOnMonitor(out var ox, out var oy, out var mw, out var mh))
                return new Vector2((x + ox) / mw, (y + oy) / mh);
            return new Vector2(x / W, y / H);
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        static IntPtr hwnd;
#endif

        /// <summary>ウィンドウ表示のとき、描画領域の左上（モニターの左上から）とモニターの大きさ [px] を返す。全画面なら false。</summary>
        static bool TryGetClientOnMonitor(out float ox, out float oy, out float mw, out float mh)
        {
            ox = oy = mw = mh = 0;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (Screen.fullScreen) return false;
            if (hwnd == IntPtr.Zero) hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) return false;
            var pt = new POINT();
            if (!ClientToScreen(hwnd, ref pt)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi)) return false;
            ox = pt.x - mi.rcMonitor.left; oy = pt.y - mi.rcMonitor.top;
            mw = mi.rcMonitor.right - mi.rcMonitor.left; mh = mi.rcMonitor.bottom - mi.rcMonitor.top;
            return mw > 0 && mh > 0;
#else
            return false;
#endif
        }

        // ---------- 5 点キャリブレーション ----------
        // 点が縮んでいく間に見つめてもらい、縮みきったら Tobii に記録させる
        class CalibDot { public float x, y; public double t0, dur; public Color color; }
        CalibDot calibDot;
        static readonly Vector2[] CALIB_POINTS = { new Vector2(0.5f, 0.5f), new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.1f),
                                                   new Vector2(0.1f, 0.9f), new Vector2(0.9f, 0.9f) };

        async Task<(bool ok, string text)> TobiiCalibrate()
        {
            state = State.Calib;
            try
            {
                await bridge.Request(new { type = "calib_start" });
                foreach (var f in CALIB_POINTS)
                {
                    calibDot = new CalibDot { x = f.x * W, y = f.y * H, t0 = Clock.Ms, dur = 1000, color = Gfx.Danger };
                    await Wait(1000);
                    var s = ClientToScreen(calibDot.x, calibDot.y);
                    await bridge.Request(new { type = "calib_point", x = s.x, y = s.y }, "calib_point_done");
                }
                calibDot = null;
                var res = await bridge.Request(new { type = "calib_compute" }, "calib_result");
                bool ok = (bool?)res["ok"] ?? false;
                return (ok, ok ? "" : "キャリブレーションの計算に失敗しました（点を見ている間に目を検出できなかった可能性があります）。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                calibDot = null;
                try { await bridge.Request(new { type = "calib_abort" }); } catch (Exception) { /* 接続が切れていれば送れない */ }
                return (false, "エラー: " + e.Message);
            }
        }

        // ---------- キャリブレーションの検証 ----------
        // キャリブレーションとは別の点（緑）を見てもらい、実際の注視点が点からどれだけずれるかを測る。
        // 補正前の生の視線の中央値と点の距離 [px] を各点で求め、平均が基準以下なら合格。
        // 基準 40 px は、この PC の実験環境（Tobii_pro/config.py：幅 51.2 cm・距離 61 cm・1920 px）で約 1°
        static readonly Vector2[] VALID_POINTS = { new Vector2(0.3f, 0.3f), new Vector2(0.7f, 0.3f),
                                                   new Vector2(0.3f, 0.7f), new Vector2(0.7f, 0.7f) };
        const float CALIB_MAX_ERROR_PX = 40;     // 平均のずれの上限
        const float CALIB_MAX_LOSS_PCT = 20;     // 目を検出できなかったサンプルの割合の上限
        const int CALIB_MAX_ATTEMPTS = 3;        // 基準に届かないとき自動でやり直す回数の上限（無限ループ防止）
        const int VALID_SETTLE_MS = 800, VALID_COLLECT_MS = 600;

        class Validation { public bool complete; public float? meanPx, maxPx; public float lossPct; }

        async Task<Validation> TobiiValidate()
        {
            state = State.Calib;
            var errs = new List<float>();
            int invalid = 0, total = 0;
            float Median(IEnumerable<float> a) { var s = a.OrderBy(v => v).ToList(); return s[s.Count >> 1]; }
            foreach (var f in VALID_POINTS)
            {
                calibDot = new CalibDot { x = f.x * W, y = f.y * H, t0 = Clock.Ms, dur = VALID_SETTLE_MS, color = Gfx.Ok };
                await Wait(VALID_SETTLE_MS);           // 視線が点に移って落ち着くのを待つ
                validationBuf = new ValidationBuf();
                await Wait(VALID_COLLECT_MS);
                var buf = validationBuf; validationBuf = null;
                invalid += buf.invalid; total += buf.invalid + buf.valid.Count;
                if (buf.valid.Count >= 5)
                    errs.Add(Dist(Median(buf.valid.Select(v => v.x)) - calibDot.x, Median(buf.valid.Select(v => v.y)) - calibDot.y));
            }
            calibDot = null;
            return new Validation
            {
                complete = errs.Count == VALID_POINTS.Length,   // 全点で目を検出できたか
                meanPx = errs.Count > 0 ? errs.Average() : (float?)null,
                maxPx = errs.Count > 0 ? errs.Max() : (float?)null,
                lossPct = total > 0 ? 100f * invalid / total : 100,
            };
        }

        static string ValidationText(Validation v)
        {
            string crit = $"基準：平均のずれ {CALIB_MAX_ERROR_PX} px 以下、目を検出できなかった割合 {CALIB_MAX_LOSS_PCT}% 以下";
            if (!v.complete) return $"目を検出できない点がありました（検出できなかった割合 {Mathf.RoundToInt(v.lossPct)}%）\n{crit}";
            return $"平均のずれ {Mathf.RoundToInt(v.meanPx.Value)} px（最大 {Mathf.RoundToInt(v.maxPx.Value)} px）、" +
                   $"目を検出できなかった割合 {Mathf.RoundToInt(v.lossPct)}%\n{crit}";
        }

        // 接続確認 → キャリブレーション → 検証。基準に届かなければ自動でやり直す（任意でスキップ可）
        async Task TobiiSetup(bool skipConnectMsg)
        {
            state = State.Calib;
            string msg = "";
            if (!skipConnectMsg && bridge.Info != null)
            {
                var info = bridge.Info;
                string freq = ((float?)info["freq"] ?? 0).ToString("0.##", CultureInfo.InvariantCulture);
                msg = $"接続しました：{(string)info["model"]}（{freq} Hz{((string)info["mode"] == "mock" ? "、マウスで代用中" : "")}）\n";
            }
            var first = await TobiiPanel("Tobii", msg +
                "<b><color=#e6edf3>頭をできるだけ動かさず固定し、目だけで点を追ってください。</color></b>\n" +
                "キャリブレーションでは、画面に順に出る<b><color=#ff6b6b>赤い点</color></b>が縮みきるまで見つめてください。\n" +
                "続けて<b><color=#3fb950>緑の点</color></b>で精度を確かめます。",
                ("キャリブレーション開始", "calib", true), ("スキップ（現在の校正を使う）", "skip", false));
            if (first == "skip") return;

            for (int attempt = 1; ; attempt++)
            {
                var cal = await TobiiCalibrate();
                var val = cal.ok ? await TobiiValidate() : null;
                bool pass = val != null && val.complete && val.meanPx <= CALIB_MAX_ERROR_PX && val.lossPct <= CALIB_MAX_LOSS_PCT;
                string detail = val != null ? ValidationText(val) : cal.text;
                if (pass)
                {
                    if (await TobiiPanel("キャリブレーション OK", detail, ("次へ", "next", true), ("やり直す", "calib", false)) == "next") return;
                    attempt = 0;
                    continue;
                }
                if (attempt < CALIB_MAX_ATTEMPTS)
                {
                    await TobiiNotice("精度が基準に届きませんでした", detail +
                        $"\n\n頭の位置を整えて固定し、もう一度行います（{attempt + 1} / {CALIB_MAX_ATTEMPTS} 回目）", 3500);
                    continue;
                }
                var c = await TobiiPanel("精度が基準に届きません", detail +
                    $"\n\n{CALIB_MAX_ATTEMPTS} 回続けて基準に届きませんでした。画面との距離（60 cm 前後）・頭の位置・メガネの反射・照明を確かめてください。",
                    ("もう一度", "calib", true), ("このまま進む", "next", false));
                if (c == "next") return;
                attempt = 0;
            }
        }

        // ---------- 両目の間隔（瞳孔間距離）の測定 ----------
        async Task<float?> MeasureIpd(float seconds)
        {
            if (inputMode != Mode.Tobii || !BridgeOK) return null;
            var res = await bridge.Request(new { type = "ipd_measure", seconds }, "ipd_result");
            return (float?)res["ipd"];
        }
    }
}
