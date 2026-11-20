using System.Collections.Generic;
using UnityEngine;

namespace GazeDodge
{
    /// <summary>色と、実行時に作る図形のスプライト（円・輪・光・角丸の四角）。</summary>
    public static class Gfx
    {
        public static Color Hex(string hex, float a = 1f)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            c.a = a;
            return c;
        }

        // index.html の CSS 変数と同じ色
        public static readonly Color Bg = Hex("#0d1117");
        public static readonly Color Fg = Hex("#e6edf3");
        public static readonly Color Muted = Hex("#8b949e");
        public static readonly Color Accent = Hex("#58a6ff");
        public static readonly Color Danger = Hex("#ff6b6b");
        public static readonly Color Ok = Hex("#3fb950");
        public static readonly Color Gold = Hex("#e3b341");
        public static readonly Color Border = Hex("#30363d");
        public static readonly Color Button = Hex("#21262d");
        public static readonly Color Bar = Hex("#f0883e");
        public static readonly Color PanelBg = new Color(22 / 255f, 27 / 255f, 34 / 255f, 0.95f);
        public static readonly Color Grid = new Color(88 / 255f, 166 / 255f, 1f, 0.06f);

        public static Color WithAlpha(Color c, float a) { c.a = a; return c; }

        /// <summary>CSS の hsl(h, s, l)。h は度、s・l は 0-1。</summary>
        public static Color Hsl(float h, float s, float l)
        {
            float c = (1 - Mathf.Abs(2 * l - 1)) * s;
            float hp = Mathf.Repeat(h, 360f) / 60f;
            float x = c * (1 - Mathf.Abs(hp % 2 - 1));
            float r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            float m = l - c / 2;
            return new Color(r + m, g + m, b + m, 1f);
        }

        static Sprite circle, square, glow, markerRing, cursorRing;
        static readonly Dictionary<int, Sprite> rounded = new Dictionary<int, Sprite>();

        /// <summary>直径 1（ワールド単位）の白い円。</summary>
        public static Sprite Circle => circle ? circle : circle = MakeRadial(128, d => Mathf.Clamp01(64f - d + 0.5f));

        /// <summary>1×1（ワールド単位）の白い四角。線・横バー・粒子に使う。</summary>
        public static Sprite Square
        {
            get
            {
                if (square) return square;
                var tex = NewTex(4, 4, false);
                var px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(px); tex.filterMode = FilterMode.Point; tex.Apply(false);
                return square = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4);
            }
        }

        /// <summary>注視点マーカー（半径 10・線の太さ 2 の輪）。22 倍して使う。</summary>
        public static Sprite MarkerRing => markerRing ? markerRing : markerRing = MakeRing(2f / 11f);

        /// <summary>見つめて選ぶときのカーソル（直径 26・線の太さ 2 の輪）。</summary>
        public static Sprite CursorRing => cursorRing ? cursorRing : cursorRing = MakeRing(2f / 13f);

        /// <summary>プレイヤーのまわりの光（canvas の shadowBlur 20 の近似）。直径 GlowSize で使う。</summary>
        public const float GlowSize = 116f;
        public static Sprite Glow
        {
            get
            {
                if (glow) return glow;
                const int n = 128;
                float pxToWorld = GlowSize / n;
                // 半径 18 の円をぼかした形：縁で 0.5、外に向かってなめらかに消える
                return glow = MakeRadial(n, d =>
                {
                    float dist = d * pxToWorld;
                    return 1f / (1f + Mathf.Exp((dist - 18f) / 6.25f));
                });
            }
        }

        static Sprite MakeRing(float thicknessFrac)
        {
            const int n = 128;
            float R = n / 2f, Ri = R * (1 - thicknessFrac);
            return MakeRadial(n, d => Mathf.Clamp01(R - d + 0.5f) * Mathf.Clamp01(d - Ri + 0.5f));
        }

        /// <summary>中心からの距離 [テクスチャの px] → 不透明度 で白い丸い形を作る。直径 1 のスプライト。</summary>
        static Sprite MakeRadial(int n, System.Func<float, float> alphaAt)
        {
            var tex = NewTex(n, n, true);
            var px = new Color32[n * n];
            float c = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(alphaAt(d))));
                }
            tex.SetPixels32(px);
            tex.Apply(true);
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        }

        /// <summary>UI 用の白い角丸の四角（9 スライス）。Image.type = Sliced で使う。</summary>
        public static Sprite Rounded(int radius)
        {
            if (rounded.TryGetValue(radius, out var s) && s) return s;
            int n = 2 * radius + 4;
            var tex = NewTex(n, n, false);
            var px = new Color32[n * n];
            float h = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // 角丸の四角の符号付き距離
                    float qx = Mathf.Abs(x + 0.5f - h) - (h - radius);
                    float qy = Mathf.Abs(y + 0.5f - h) - (h - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude;
                    float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0) - radius;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(0.5f - d)));
                }
            tex.SetPixels32(px);
            tex.Apply(false);
            float b = radius + 1;
            s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect,
                              new Vector4(b, b, b, b));
            rounded[radius] = s;
            return s;
        }

        static Texture2D NewTex(int w, int h, bool mip)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, mip)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }
    }

    /// <summary>
    /// canvas の描画（毎フレーム描き直す）を SpriteRenderer の使い回しで再現する。
    /// Draw を呼んだ順に手前へ重なる（canvas と同じ塗り重ね）。座標は画面の px（左上が原点・下向きが +y）。
    /// </summary>
    public class SpriteBatch
    {
        readonly Transform root;
        readonly List<SpriteRenderer> pool = new List<SpriteRenderer>();
        int used;

        public SpriteBatch(Transform root) { this.root = root; }

        public void Begin() { used = 0; }

        public void Draw(Sprite sprite, float x, float y, float sx, float sy, Color color, float rotDeg = 0f)
        {
            SpriteRenderer r;
            if (used < pool.Count) r = pool[used];
            else
            {
                var go = new GameObject("sprite");
                go.transform.SetParent(root, false);
                r = go.AddComponent<SpriteRenderer>();
                pool.Add(r);
            }
            if (!r.gameObject.activeSelf) r.gameObject.SetActive(true);
            r.sprite = sprite;
            r.color = color;
            r.sortingOrder = used;
            var t = r.transform;
            t.localPosition = new Vector3(x, -y, 0);
            t.localRotation = Quaternion.Euler(0, 0, rotDeg);
            t.localScale = new Vector3(sx, sy, 1);
            used++;
        }

        public void End()
        {
            for (int i = used; i < pool.Count; i++)
                if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
        }
    }
}
