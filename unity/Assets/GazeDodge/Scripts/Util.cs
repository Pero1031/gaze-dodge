using System;
using System.Diagnostics;
using System.Linq;

namespace GazeDodge
{
    /// <summary>ゲーム全体で共通の時計 [ms]。視線サンプルの到着時刻（別スレッド）とフレームの時刻を同じ基準で比べる。</summary>
    public static class Clock
    {
        static readonly Stopwatch sw = Stopwatch.StartNew();
        public static double Ms => sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// One Euro フィルタ（Casiez et al., 2012）。index.html と同じ実装。
    /// 視線が止まっているときは強くなめらかにし、速く動いたときは補正を弱めて遅れを減らす。
    /// </summary>
    public class OneEuroFilter
    {
        readonly double minCutoff, beta, dCutoff;
        bool has;
        double x, dx, t;

        public OneEuroFilter(double minCutoff, double beta, double dCutoff)
        {
            this.minCutoff = minCutoff; this.beta = beta; this.dCutoff = dCutoff;
        }

        public void Reset() { has = false; dx = 0; t = 0; }

        static double Alpha(double cutoff, double dt)
        {
            double r = 2 * Math.PI * cutoff * dt;
            return r / (r + 1);
        }

        /// <param name="v">値</param>
        /// <param name="tSec">時刻 [s]</param>
        public double Filter(double v, double tSec)
        {
            if (!has) { has = true; x = v; t = tSec; return v; }
            double dt = Math.Max(1e-3, tSec - t); t = tSec;
            double ad = Alpha(dCutoff, dt);
            dx = ad * ((v - x) / dt) + (1 - ad) * dx;
            double a = Alpha(minCutoff + beta * Math.Abs(dx), dt);
            x = a * v + (1 - a) * x;
            return x;
        }
    }

    /// <summary>コマンドライン引数（ビルドした exe に渡す。例: GazeDodge.exe -ws 8766 -noblink）</summary>
    public static class Args
    {
        public static string Get(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == name) return a[i + 1];
            return null;
        }

        public static bool Has(string name) => Environment.GetCommandLineArgs().Contains(name);
    }
}
