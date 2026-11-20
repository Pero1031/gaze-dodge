using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GazeDodge
{
    /// <summary>画像の障害物 1 種類（obstacles.json の images の 1 項目）。</summary>
    public class ObstacleImage
    {
        public string file;
        public Texture2D tex;
        public Sprite sprite;
        public float probability;
        public Vector2 size;          // 表示する幅 [px] の [最小, 最大]
        public string hitbox;         // alpha | circle | box
        public bool spin;
        public float aspect;          // 高さ / 幅
        public AlphaMask mask;        // hitbox == alpha のときだけ
        public Vector2? fallSpeed;    // 落ちる速さ [px/s]（null なら岩と同じ）
        public float homingSpeed;     // プレイヤーへ横に寄る最大速さ [px/s]（0 で寄らない）
    }

    /// <summary>形どおりの当たり判定に使う透明度マップ。a[v * w + u]、v = 0 が画像の上端。</summary>
    public class AlphaMask
    {
        public int w, h;
        public byte[] a;
    }

    /// <summary>
    /// gaze-dodge/obstacles/obstacles.json と画像を読み込む（Unity プロジェクトには取り込まない。GitHub にも載せない）。
    /// 使い方・項目の意味は obstacles/README.md と同じ。Unity 版で読める画像は PNG と JPG だけ（SVG・WebP は不可）。
    /// </summary>
    public static class ObstacleImages
    {
        const int MASK_MAX = 128;     // 透明度マップの解像度（長辺 px）

        /// <summary>obstacles フォルダを探す。-obstacles &lt;フォルダ&gt; で指定もできる。見つからなければ null。</summary>
        public static string FindDir()
        {
            var arg = Args.Get("-obstacles");
            if (arg != null) return Path.GetFullPath(arg);
            var cands = new List<string>();
#if UNITY_EDITOR
            // Assets の 2 つ上 = gaze-dodge/
            cands.Add(Path.Combine(Application.dataPath, "..", "..", "obstacles"));
#else
            // ビルドした exe のあるフォルダから上へたどる（unity/Build/GazeDodge.exe → gaze-dodge/obstacles）
            var exeDir = Path.GetDirectoryName(Application.dataPath);
            cands.Add(Path.Combine(exeDir, "obstacles"));
            cands.Add(Path.Combine(exeDir, "..", "obstacles"));
            cands.Add(Path.Combine(exeDir, "..", "..", "obstacles"));
#endif
            return cands.Select(Path.GetFullPath).FirstOrDefault(d => File.Exists(Path.Combine(d, "obstacles.json")));
        }

        /// <summary>読み込んだ画像の一覧と、画面左下に出す注意（なければ null）を返す。</summary>
        public static (List<ObstacleImage> images, string warning) Load()
        {
            var result = new List<ObstacleImage>();
            var dir = FindDir();
            if (dir == null) return (result, null);   // 設定がなければ画像の障害物なし
            JArray defs;
            try
            {
                var cfg = JObject.Parse(File.ReadAllText(Path.Combine(dir, "obstacles.json")));
                defs = cfg["images"] as JArray ?? new JArray();
            }
            catch (Exception e)
            {
                return (result, "【注意】obstacles/obstacles.json を読み込めませんでした（書式を確認してください）：" + e.Message);
            }
            foreach (var def in defs.OfType<JObject>())
            {
                var im = LoadOne(dir, def);
                if (im != null) result.Add(im);
            }
            // 確率の合計が 1 を超えていたら、比率を保って合計 1 に縮める
            float sum = result.Sum(im => im.probability);
            if (sum > 1) foreach (var im in result) im.probability /= sum;
            int failed = defs.Count - result.Count;
            string warning = failed > 0
                ? $"【注意】障害物の画像 {failed} 個を読み込めませんでした（ファイル名を確認してください。Unity 版は PNG・JPG のみ。詳細は Player.log）"
                : null;
            return (result, warning);
        }

        static ObstacleImage LoadOne(string dir, JObject def)
        {
            var file = (string)def["file"];
            try
            {
                if (string.IsNullOrEmpty(file)) throw new Exception("file がありません");
                var path = Path.Combine(dir, file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                    throw new Exception($"{ext} は Unity 版では読めません（PNG か JPG に変換してください）");
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = file };
                if (!tex.LoadImage(File.ReadAllBytes(path))) throw new Exception("画像として読めません");
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Trilinear;
                int nw = tex.width > 0 ? tex.width : 100, nh = tex.height > 0 ? tex.height : 100;

                var sz = Range(def["size"]) ?? new Vector2(80, 80);
                var hitbox = (string)def["hitbox"];
                if (hitbox != "alpha" && hitbox != "circle" && hitbox != "box") hitbox = "alpha";
                return new ObstacleImage
                {
                    file = file,
                    tex = tex,
                    sprite = Sprite.Create(tex, new Rect(0, 0, nw, nh), new Vector2(0.5f, 0.5f), 1),
                    probability = Mathf.Clamp01(Num(def["probability"]) ?? 0),
                    size = sz,
                    hitbox = hitbox,
                    spin = (bool?)def["spin"] ?? false,
                    aspect = (float)nh / nw,
                    mask = hitbox == "alpha" ? BuildAlphaMask(tex) : null,
                    fallSpeed = Range(def["fall_speed"]),
                    homingSpeed = Mathf.Max(0, Num(def["homing_speed"]) ?? 0),
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"障害物の画像を読み込めません: {file}（{e.Message}）");
                return null;
            }
        }

        static float? Num(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<float>();
            return float.TryParse((string)t, out var v) ? v : (float?)null;
        }

        /// <summary>数値 1 つなら [v, v]、配列なら [v0, v1 ?? v0]、なければ null。</summary>
        static Vector2? Range(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t is JArray a && a.Count > 0)
            {
                float v0 = Num(a[0]) ?? 0;
                return new Vector2(v0, a.Count > 1 ? Num(a[1]) ?? v0 : v0);
            }
            var v = Num(t);
            return v.HasValue ? new Vector2(v.Value, v.Value) : (Vector2?)null;
        }

        /// <summary>画像の透明度（α）を縮小して保持する（各マスの平均）。α が 128 以上を「当たる部分」とみなす。</summary>
        static AlphaMask BuildAlphaMask(Texture2D tex)
        {
            int nw = tex.width, nh = tex.height;
            float s = (float)MASK_MAX / Mathf.Max(nw, nh);
            int w = Mathf.Max(1, Mathf.RoundToInt(nw * s)), h = Mathf.Max(1, Mathf.RoundToInt(nh * s));
            var px = tex.GetPixels32();      // 下の行から順に並んでいる
            var a = new byte[w * h];
            for (int v = 0; v < h; v++)
            {
                int y0 = v * nh / h, y1 = Mathf.Max(y0 + 1, (v + 1) * nh / h);
                for (int u = 0; u < w; u++)
                {
                    int x0 = u * nw / w, x1 = Mathf.Max(x0 + 1, (u + 1) * nw / w);
                    int sum = 0, n = 0;
                    for (int y = y0; y < y1 && y < nh; y++)
                    {
                        int row = (nh - 1 - y) * nw;   // 上から y 行目
                        for (int x = x0; x < x1 && x < nw; x++) { sum += px[row + x].a; n++; }
                    }
                    a[v * w + u] = (byte)(n > 0 ? sum / n : 0);
                }
            }
            return new AlphaMask { w = w, h = h, a = a };
        }
    }
}
