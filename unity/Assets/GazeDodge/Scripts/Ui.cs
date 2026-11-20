using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GazeDodge
{
    public enum ButtonStyle { Normal, Primary, Danger }

    /// <summary>
    /// 日本語の文用の Text。uGUI の Text は半角スペースの位置を優先して折り返すので、
    /// 「画面から 60 cm ほど…」が「60 cm」の直後で改行されてしまう。スペースを改行しないスペース（U+00A0）に替えて、
    /// 行末までは詰めて書き、はみ出したところで折り返すようにする。
    /// </summary>
    public class JpText : Text
    {
        public override string text
        {
            get => base.text;
            set => base.text = value?.Replace(' ', ' ');
        }
    }

    /// <summary>見つめて押せるボタンの印（index.html の button.dwell）。下端のバーで進み具合を見せる。</summary>
    public class DwellTarget : MonoBehaviour
    {
        public RectTransform bar;
        public void SetProgress(float p)
        {
            if (!bar) return;
            bar.gameObject.SetActive(p > 0);
            bar.anchorMax = new Vector2(Mathf.Clamp01(p), bar.anchorMax.y);
        }
    }

    /// <summary>
    /// uGUI の部品をコードで組み立てる（index.html のパネル・ボタン・表の見た目に合わせる）。
    /// 日本語は Windows のフォント（Yu Gothic UI など）をそのまま使う。
    /// </summary>
    public static class Ui
    {
        static Font font;
        public static Font Font => font
            ? font
            : font = Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS UI Gothic", "Arial" }, 16);

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static void Stretch(RectTransform rt, float inset = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
        }

        public static Text Label(Transform parent, string text, int size, Color color,
                                 FontStyle style = FontStyle.Normal, TextAnchor align = TextAnchor.UpperCenter, bool jp = true)
        {
            var rt = Rect("Text", parent);
            var t = jp ? rt.gameObject.AddComponent<JpText>() : rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.alignment = align;
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.lineSpacing = 1.15f;
            t.raycastTarget = false;
            t.text = text;
            return t;
        }

        /// <summary>画面全体を覆う透明な入れ物（index.html の .overlay）。最初は非表示。</summary>
        public static RectTransform Overlay(Transform canvas, string name)
        {
            var rt = Rect(name, canvas);
            Stretch(rt);
            rt.gameObject.SetActive(false);
            return rt;
        }

        /// <summary>角丸・枠線つきの背景。枠の内側に塗りの Image を 1 枚重ねる（レイアウトの対象外）。返り値は塗り。</summary>
        public static Image Frame(RectTransform rt, int radius, Color fill, Color border)
        {
            var bg = rt.gameObject.AddComponent<Image>();
            bg.sprite = Gfx.Rounded(radius);
            bg.type = Image.Type.Sliced;
            bg.color = border;
            var inner = Rect("Fill", rt);
            inner.SetAsFirstSibling();
            Stretch(inner, 1);
            inner.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            var fi = inner.gameObject.AddComponent<Image>();
            fi.sprite = Gfx.Rounded(Mathf.Max(1, radius - 1));
            fi.type = Image.Type.Sliced;
            fi.color = fill;
            fi.raycastTarget = false;
            return fi;
        }

        /// <summary>中央のパネル（index.html の .panel）。中身は縦に並ぶ。幅は画面に収まるように縮める。</summary>
        public static RectTransform Panel(Transform overlay, float width)
        {
            var rt = Rect("Panel", overlay);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(Mathf.Min(width, Screen.width - 32), 0);
            Frame(rt, 12, Gfx.PanelBg, Gfx.Border);
            var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(32, 32, 28, 28);
            v.spacing = 10;
            v.childAlignment = TextAnchor.UpperCenter;
            v.childControlWidth = true; v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            rt.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rt;
        }

        public static Text H1(Transform panel, string text) => Label(panel, text, 28, Gfx.Fg, FontStyle.Bold);
        public static Text P(Transform panel, string text, int size = 16) => Label(panel, text, size, Gfx.Muted);

        /// <summary>黄色の枠で囲んだ注意書き（index.html の p.notice）。</summary>
        public static Text Notice(Transform panel, string text)
        {
            var box = Rect("Notice", panel);
            Frame(box, 8, new Color(227 / 255f, 179 / 255f, 65 / 255f, 0.12f), new Color(227 / 255f, 179 / 255f, 65 / 255f, 0.5f));
            var h = box.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(14, 14, 10, 10);
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = true; h.childForceExpandHeight = false;
            return Label(box, text, 16, Gfx.Fg);
        }

        /// <summary>横並びの入れ物（ボタンの列など）。</summary>
        public static RectTransform Row(Transform parent, float spacing = 12, TextAnchor align = TextAnchor.MiddleCenter)
        {
            var rt = Rect("Row", parent);
            var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = align;
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            return rt;
        }

        /// <summary>縦並びの入れ物（表など）。</summary>
        public static RectTransform Column(Transform parent, float spacing = 0)
        {
            var rt = Rect("Column", parent);
            var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.childAlignment = TextAnchor.UpperCenter;
            v.childControlWidth = true; v.childControlHeight = true;
            v.childForceExpandWidth = false; v.childForceExpandHeight = false;
            return rt;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        /// <summary>
        /// ボタン。押したあとは選択を外す（選択が残ると Space キーでもう一度押されてしまうため）。
        /// dwell = true なら見つめて押す対象にする（index.html の button.dwell）。
        /// </summary>
        public static Button Button(Transform parent, string label, Action onClick, ButtonStyle style = ButtonStyle.Normal,
                                    bool dwell = false, int fontSize = 16)
        {
            var rt = Rect("Button", parent);
            Color fillColor = style == ButtonStyle.Primary ? Gfx.Accent : Gfx.Button;
            Color borderColor = style == ButtonStyle.Primary ? Gfx.Accent : style == ButtonStyle.Danger ? Gfx.Danger : Gfx.Border;
            Color textColor = style == ButtonStyle.Primary ? Gfx.Bg : style == ButtonStyle.Danger ? Gfx.Danger : Gfx.Fg;
            var fill = Frame(rt, 8, Color.white, borderColor);
            var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(20, 20, 10, 10);
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = true; h.childControlHeight = true;
            // true にするとボタン自体が「伸びてよい」扱いになり、列の幅いっぱいに広がってしまう
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            Label(rt, label, fontSize, textColor, style == ButtonStyle.Primary ? FontStyle.Bold : FontStyle.Normal,
                  TextAnchor.MiddleCenter).horizontalOverflow = HorizontalWrapMode.Overflow;

            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = fill;
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = fillColor;
            cb.selectedColor = fillColor;
            cb.highlightedColor = Brighten(fillColor, 1.15f);
            cb.pressedColor = Brighten(fillColor, 0.9f);
            cb.disabledColor = Gfx.WithAlpha(fillColor, 0.5f);
            cb.fadeDuration = 0.05f;
            b.colors = cb;
            b.onClick.AddListener(() =>
            {
                if (EventSystem.current) EventSystem.current.SetSelectedGameObject(null);
                onClick?.Invoke();
            });

            if (dwell)
            {
                // 見つめている間に下端で伸びる緑のバー
                var bar = Rect("DwellBar", rt);
                bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(0, 0);
                bar.pivot = new Vector2(0, 0);
                bar.offsetMin = new Vector2(1, 1); bar.offsetMax = new Vector2(-1, 6);
                var bi = bar.gameObject.AddComponent<Image>();
                bi.color = Gfx.Ok;
                bi.raycastTarget = false;
                bar.gameObject.SetActive(false);
                rt.gameObject.AddComponent<DwellTarget>().bar = bar;
            }
            return b;
        }

        public static void SetButtonLabel(Button b, string label)
        {
            var t = b.GetComponentInChildren<Text>();
            if (t) t.text = label;
        }

        static Color Brighten(Color c, float k) => new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);

        /// <summary>1 行分の表（各セルの幅を固定）。返り値はセルの Text。</summary>
        public static Text[] TableRow(Transform table, float[] widths, string[] cells, TextAnchor[] aligns, Color color,
                                      FontStyle style = FontStyle.Normal, int size = 16)
        {
            var row = Row(table, 24);   // index.html の td の左右の余白（12 px ずつ）
            var texts = new Text[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                var t = Label(row, cells[i], size, color, style, aligns[i]);
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                var le = t.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = widths[i];
                le.preferredHeight = size + 12;
                texts[i] = t;
            }
            return texts;
        }

        /// <summary>名前入力欄（index.html の input[type=text]）。日本語入力（IME）も使える。</summary>
        public static InputField Input(Transform parent, int maxLength)
        {
            var wrap = Row(parent);
            var rt = Rect("Input", wrap);
            Frame(rt, 8, Gfx.Bg, Gfx.Border);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 320; le.preferredHeight = 52;
            var text = Label(rt, "", 22, Gfx.Fg, FontStyle.Normal, TextAnchor.MiddleCenter, jp: false);
            text.supportRichText = false;
            Stretch(text.rectTransform, 8);
            var f = rt.gameObject.AddComponent<InputField>();
            f.textComponent = text;
            f.characterLimit = maxLength;
            f.lineType = InputField.LineType.SingleLine;
            f.navigation = new Navigation { mode = Navigation.Mode.None };
            f.caretColor = Gfx.Fg;
            f.customCaretColor = true;
            f.selectionColor = Gfx.WithAlpha(Gfx.Accent, 0.4f);
            return f;
        }
    }
}
