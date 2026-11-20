using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GazeDodge
{
    public class PlayerInfo
    {
        public string id, name;
        public float? ipd;
        public int best, plays;
    }

    public partial class GazeDodgeGame
    {
        // ---------- UI の部品 ----------
        Transform canvasT;
        RectTransform titleOv, quitOv, tobiiOv, selectOv, confirmOv, registerOv, manageOv, dialogOv, rankingOv, overOv;
        Text tobiiTitle, tobiiMsg, dialogTitle, dialogMsg, selectMsg, confirmName, confirmMsg, regMsg, rankingMsg, overScore, overHint;
        RectTransform tobiiBtns, dialogBtns, plist, manageList, rankingTable, overRank;
        GridLayoutGroup plistGrid;
        Button btnConfirmYes, btnConfirmNo, btnRegOk, btnSwitch, btnRecalib;
        InputField regName;
        GameObject hud;
        Text hudWho, hudScore, hudBest, hudMode, status, countdownText, countdownHint, eyeLostText;
        RectTransform gazeCursor;

        bool quitting;

        void Show(Component o)
        {
            if (quitting && o != quitOv) return;   // 終了処理中は他の画面を出さない
            o.gameObject.SetActive(true);
        }

        static void Hide(Component o) => o.gameObject.SetActive(false);

        internal bool IsShown(string overlayName)
        {
            var t = canvasT ? canvasT.Find(overlayName) : null;
            return t && t.gameObject.activeSelf;
        }

        void SetStatus(string t) => status.text = t;

        void BuildUi()
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1;
            canvasT = canvasGo.transform;
            if (!FindObjectOfType<EventSystem>())
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            // ゲーム画面の上に出す文字（カウントダウン・目を検出できません）
            countdownText = Ui.Label(canvasT, "", 96, Gfx.Fg, FontStyle.Bold, TextAnchor.MiddleCenter);
            Place(countdownText.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400, 130));
            countdownHint = Ui.Label(canvasT, "頭を動かさず、目だけで青い丸を動かしてください", 18, Gfx.Muted, FontStyle.Normal, TextAnchor.MiddleCenter);
            Place(countdownHint.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, -80), new Vector2(900, 30));
            eyeLostText = Ui.Label(canvasT, "目を検出できません", 20, Gfx.Danger, FontStyle.Bold, TextAnchor.UpperCenter);
            Place(eyeLostText.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -16), new Vector2(600, 30));
            foreach (var t in new[] { countdownText, countdownHint, eyeLostText }) t.gameObject.SetActive(false);

            // HUD（左上）と状態表示（左下）
            var hudRt = Ui.Row(canvasT, 18, TextAnchor.UpperLeft);
            hudRt.anchorMin = hudRt.anchorMax = hudRt.pivot = new Vector2(0, 1);
            hudRt.anchoredPosition = new Vector2(16, -12);
            var fit = hudRt.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Text HudText()
            {
                var t = Ui.Label(hudRt, "", 18, Gfx.Fg, FontStyle.Normal, TextAnchor.UpperLeft);
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                var sh = t.gameObject.AddComponent<Shadow>();
                sh.effectColor = new Color(0, 0, 0, 0.8f); sh.effectDistance = new Vector2(0, -1);
                return t;
            }
            hudWho = HudText(); hudScore = HudText(); hudBest = HudText(); hudMode = HudText();
            hud = hudRt.gameObject;
            hud.SetActive(false);
            status = Ui.Label(canvasT, "", 13, Gfx.Muted, FontStyle.Normal, TextAnchor.LowerLeft);
            var srt = status.rectTransform;
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 0);
            srt.offsetMin = new Vector2(16, 10); srt.offsetMax = new Vector2(-16, 60);

            BuildTitle();
            BuildTobiiPanel();
            BuildSelect();
            BuildConfirmPlayer();
            BuildRegister();
            BuildManage();
            BuildRanking();
            BuildOver();
            BuildDialog();
            BuildQuit();

            // 見つめて選ぶときのカーソル（DWELL_ENABLED のときだけ）
            gazeCursor = Ui.Rect("GazeCursor", canvasT);
            gazeCursor.anchorMin = gazeCursor.anchorMax = new Vector2(0, 1);
            gazeCursor.sizeDelta = new Vector2(26, 26);
            var gi = gazeCursor.gameObject.AddComponent<Image>();
            gi.sprite = Gfx.CursorRing; gi.color = new Color(1, 1, 1, 0.7f); gi.raycastTarget = false;
            gazeCursor.gameObject.SetActive(false);
            quitOv.SetAsLastSibling();
        }

        static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        const string FG = "#e6edf3";
        static string Em(string s) => $"<b><color={FG}>{s}</color></b>";

        void BuildTitle()
        {
            titleOv = Ui.Overlay(canvasT, "Title");
            var p = Ui.Panel(titleOv, 560);
            Ui.H1(p, "Gaze Dodge");
            Ui.P(p, "視線でプレイヤー（青い丸）を動かし、上から降ってくる障害物を避け続けるゲームです。");
            Ui.Notice(p, "<b>プレイ中は、できるだけ頭を動かさず固定してください。</b>\n" +
                         "画面から 60 cm ほど離れて座り、楽な姿勢で目だけを動かすと、視線を正確に読み取れます。");
            Ui.P(p, $"{Em("Tobii モード")}：先に <color={FG}>tobii_bridge.py</color> を起動しておいてください。全画面で遊びます。\n" +
                    $"{Em("マウス")}：動作確認用です（ブリッジが動いていれば、プレイヤー登録・ランキングも使えます）。");
            var row = Ui.Row(p);
            Ui.Button(row, "Tobii で始める", OnTobiiClicked, ButtonStyle.Primary);
            Ui.Button(row, "マウス（テスト用）", OnMouseClicked);
            Ui.Button(Ui.Row(p), "ランキングを見る", OnRankingClicked);
            Ui.P(p, "視線データはこの PC 内でのみ処理され、外部に送信されません。\n" +
                    "プレイヤー登録では名前と「両目の間隔」の数値だけをこの PC に保存します。\n" +
                    $"{Em("ESC キーでいつでも終了")}（ゲームとブリッジを閉じます）", 13);
        }

        void BuildTobiiPanel()
        {
            tobiiOv = Ui.Overlay(canvasT, "TobiiPanel");
            var p = Ui.Panel(tobiiOv, 560);
            tobiiTitle = Ui.H1(p, "Tobii");
            tobiiMsg = Ui.P(p, "");
            tobiiBtns = Ui.Row(p);
        }

        void BuildDialog()
        {
            dialogOv = Ui.Overlay(canvasT, "Dialog");
            var p = Ui.Panel(dialogOv, 480);
            dialogTitle = Ui.H1(p, "");
            dialogMsg = Ui.P(p, "");
            dialogBtns = Ui.Row(p);
        }

        void BuildSelect()
        {
            selectOv = Ui.Overlay(canvasT, "Select");
            var p = Ui.Panel(selectOv, 820);
            Ui.H1(p, "だれがプレイしますか？");
            selectMsg = Ui.P(p, "");
            plist = Ui.Rect("PlayerList", p);
            plistGrid = plist.gameObject.AddComponent<GridLayoutGroup>();
            plistGrid.spacing = new Vector2(12, 12);
            plistGrid.padding = new RectOffset(0, 0, 8, 8);
            plistGrid.childAlignment = TextAnchor.UpperCenter;
            plistGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            float inner = ((RectTransform)p).sizeDelta.x - 64;
            int cols = Mathf.Max(1, Mathf.FloorToInt((inner + 12) / (170 + 12)));
            plistGrid.constraintCount = cols;
            plistGrid.cellSize = new Vector2((inner - 12 * (cols - 1)) / cols, 88);
            var row = Ui.Row(p);
            Ui.Button(row, "＋ 新規登録", OnNewPlayer, dwell: true);
            Ui.Button(row, "ゲストで遊ぶ", () => selectTcs?.TrySetResult(null), dwell: true);
            Ui.Button(row, "登録の削除…", OnManage);
        }

        void BuildConfirmPlayer()
        {
            confirmOv = Ui.Overlay(canvasT, "ConfirmPlayer");
            var p = Ui.Panel(confirmOv, 560);
            confirmName = Ui.H1(p, "");
            confirmMsg = Ui.P(p, "");
            var row = Ui.Row(p);
            btnConfirmYes = Ui.Button(row, "はい", () => confirmYes?.Invoke(), ButtonStyle.Primary, dwell: true);
            btnConfirmNo = Ui.Button(row, "いいえ（一覧から選ぶ）", () => { Hide(confirmOv); Show(selectOv); }, dwell: true);
        }

        Action confirmYes;

        void BuildRegister()
        {
            registerOv = Ui.Overlay(canvasT, "Register");
            var p = Ui.Panel(registerOv, 560);
            Ui.H1(p, "新規登録");
            Ui.P(p, "名前（ニックネーム）を入力してください");
            regName = Ui.Input(p, 20);
            regName.onEndEdit.AddListener(_ =>
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) OnRegOk();
            });
            regMsg = Ui.P(p, "");
            var row = Ui.Row(p);
            btnRegOk = Ui.Button(row, "登録（Enter）", OnRegOk, ButtonStyle.Primary);
            Ui.Button(row, "戻る", () => { Hide(registerOv); Show(selectOv); });
        }

        void BuildManage()
        {
            manageOv = Ui.Overlay(canvasT, "Manage");
            var p = Ui.Panel(manageOv, 560);
            Ui.H1(p, "登録の削除");
            Ui.P(p, "削除すると、その人のスコア記録も消えます。");
            manageList = Ui.Column(p, 4);
            Ui.Button(Ui.Row(p), "閉じる", () => { Hide(manageOv); Show(selectOv); });
        }

        void BuildRanking()
        {
            rankingOv = Ui.Overlay(canvasT, "Ranking");
            var p = Ui.Panel(rankingOv, 560);
            Ui.H1(p, "ランキング");
            rankingMsg = Ui.P(p, "");
            rankingTable = Ui.Column(p);
            Ui.Button(Ui.Row(p), "タイトルへ戻る", () => { Hide(rankingOv); Show(titleOv); }, ButtonStyle.Primary);
        }

        void BuildOver()
        {
            overOv = Ui.Overlay(canvasT, "Over");
            var p = Ui.Panel(overOv, 620);
            Ui.H1(p, "ゲームオーバー");
            overScore = Ui.P(p, "");
            overRank = Ui.Column(p);
            var row = Ui.Row(p, 10);
            Ui.Button(row, "もう一度", ResetGame, ButtonStyle.Primary, dwell: true);
            btnSwitch = Ui.Button(row, "プレイヤー交代", OnSwitchPlayer, dwell: true);
            btnRecalib = Ui.Button(row, "再校正", OnRecalib, dwell: true);
            Ui.Button(row, "タイトルへ", () => { Hide(overOv); hud.SetActive(false); Show(titleOv); state = State.Title; }, dwell: true);
            overHint = Ui.P(p, "", 13);
        }

        void BuildQuit()
        {
            quitOv = Ui.Overlay(canvasT, "Quit");
            var p = Ui.Panel(quitOv, 560);
            Ui.H1(p, "終了しています…");
            Ui.P(p, "後片付けをしています。そのままお待ちください。");
        }

        /// <summary>メッセージとボタンを出し、押されたボタンの値を返す（index.html の tobiiPanel）。</summary>
        Task<string> Choice(RectTransform ov, Text title, Text msg, RectTransform btns, string t, string m,
                            params (string label, string value, bool primary)[] buttons)
        {
            title.text = t; msg.text = m;
            Ui.Clear(btns);
            var tcs = new TaskCompletionSource<string>();
            foreach (var b in buttons)
            {
                var v = b.value;
                Ui.Button(btns, b.label, () => { Hide(ov); tcs.TrySetResult(v); },
                          b.primary ? ButtonStyle.Primary : ButtonStyle.Normal, dwell: true);
            }
            btns.gameObject.SetActive(buttons.Length > 0);
            Show(ov);
            return tcs.Task;
        }

        Task<string> TobiiPanel(string t, string m, params (string label, string value, bool primary)[] buttons)
            => Choice(tobiiOv, tobiiTitle, tobiiMsg, tobiiBtns, t, m, buttons);

        // ボタンなしでしばらく表示する
        async Task TobiiNotice(string t, string m, int ms)
        {
            _ = Choice(tobiiOv, tobiiTitle, tobiiMsg, tobiiBtns, t, m);
            await Wait(ms);
            Hide(tobiiOv);
        }

        // ---------- 画面遷移 ----------
        // マウスモードでもブリッジが動いていればプレイヤー登録・ランキングを使う
        async Task TryBridge()
        {
            try { await ConnectBridge(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* ブリッジなし → ゲスト */ }
        }

        async void OnTobiiClicked()
        {
            try
            {
                Hide(titleOv);
                inputMode = Mode.Tobii;
                try
                {
                    await ConnectBridge();
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    Debug.LogWarning(e);
                    await TobiiPanel("ブリッジに接続できません",
                        "Tobii ブリッジに接続できませんでした。\n先に「python -X utf8 tobii_bridge.py」を起動してから、もう一度お試しください。",
                        ("タイトルへ", "ok", true));
                    Show(titleOv); state = State.Title;
                    return;
                }
                await TobiiSetup(false);
                await ChoosePlayer();
                ResetGame();
            }
            catch (OperationCanceledException) { /* 終了した */ }
        }

        async void OnMouseClicked()
        {
            try
            {
                Hide(titleOv); inputMode = Mode.Mouse;
                await TryBridge();
                await ChoosePlayer();
                ResetGame();
            }
            catch (OperationCanceledException) { }
        }

        async void OnSwitchPlayer()
        {
            try
            {
                Hide(overOv); hud.SetActive(false);
                await ChoosePlayer();
                ResetGame();
            }
            catch (OperationCanceledException) { }
        }

        async void OnRecalib()
        {
            try
            {
                Hide(overOv); hud.SetActive(false);
                if (inputMode == Mode.Tobii) await TobiiSetup(true);
                ResetGame();
            }
            catch (OperationCanceledException) { }
        }

        // ---------- プレイヤー（登録・選択） ----------
        // 本人確認は「両目の間隔（瞳孔間距離）が近い人を上に並べる」だけにとどめ、最後は本人が名前を選ぶ。
        // 両目の間隔は人によって数 mm しか違わないため、自動で決め打ちはしない。
        const float IPD_MATCH_MM = 2.0f;   // この差以内なら「候補」として強調する
        PlayerInfo currentPlayer;          // null = ゲスト
        List<PlayerInfo> players = new List<PlayerInfo>();
        float? measuredIpd;
        TaskCompletionSource<PlayerInfo> selectTcs;
        bool regBusy;

        static List<PlayerInfo> ParsePlayers(JToken arr) =>
            (arr as JArray ?? new JArray()).Select(p => new PlayerInfo
            {
                id = (string)p["id"],
                name = (string)p["name"],
                ipd = (float?)p["ipd"],
                best = (int?)p["best"] ?? 0,
                plays = (int?)p["plays"] ?? 0,
            }).ToList();

        static string F1(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

        // プレイヤーを選んでもらう（ブリッジがなければゲスト）
        async Task ChoosePlayer()
        {
            if (!BridgeOK) { currentPlayer = null; return; }
            state = State.Select;
            Ui.Clear(plist);
            selectMsg.text = inputMode == Mode.Tobii ? "目の間隔を測っています…画面を見たまま少し待ってください" : "読み込み中…";
            Show(selectOv);
            selectTcs = new TaskCompletionSource<PlayerInfo>();
            var picked = selectTcs.Task;
            try
            {
                measuredIpd = await MeasureIpd(1.5f);
                players = ParsePlayers((await bridge.Request(new { type = "players_get" }, "players"))["players"]);
                if (!picked.IsCompleted)
                {
                    RenderPlayerList();
                    var cand = LikelyReturningPlayer();
                    if (cand != null) AskIfSamePlayer(cand);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                selectMsg.text = "エラー: " + e.Message;
            }
            currentPlayer = await picked;
            Hide(selectOv); Hide(registerOv); Hide(confirmOv); Hide(manageOv);
        }

        // 過去にプレイしたことがあり、目の間隔の差が IPD_MATCH_MM 以内の登録者のうち一番近い人（いなければ null）
        PlayerInfo LikelyReturningPlayer()
        {
            if (measuredIpd == null) return null;
            float m = measuredIpd.Value;
            return players.Where(p => p.plays > 0 && p.ipd != null && Mathf.Abs(p.ipd.Value - m) <= IPD_MATCH_MM)
                          .OrderBy(p => Mathf.Abs(p.ipd.Value - m)).FirstOrDefault();
        }

        // 一覧の前に「○○さんですか？」と聞く。はい → その人で開始、いいえ → 一覧から選ぶ
        void AskIfSamePlayer(PlayerInfo p)
        {
            confirmName.text = $"{p.name}さんですか？";
            confirmMsg.text = $"目の間隔が近い登録者です（差 {F1(Mathf.Abs(p.ipd.Value - measuredIpd.Value))} mm・自己ベスト {p.best}）。" +
                              (DwellOn ? "ボタンを 1 秒見つめても選べます。" : "");
            confirmYes = () => selectTcs?.TrySetResult(p);
            Hide(selectOv); Show(confirmOv);
        }

        void RenderPlayerList()
        {
            var list = players.Select(p => (p, diff: measuredIpd != null && p.ipd != null ? Mathf.Abs(p.ipd.Value - measuredIpd.Value) : (float?)null)).ToList();
            if (measuredIpd != null) list = list.OrderBy(x => x.diff ?? 99).ToList();
            string how = DwellOn ? "自分の名前を 1 秒見つめると選べます。" : "自分の名前をクリックして選んでください。";
            selectMsg.text = (players.Count > 0 ? how : "まだ登録がありません。「新規登録」から登録してください。") +
                             (measuredIpd != null ? $"（あなたの目の間隔：{F1(measuredIpd.Value)} mm）" : "");
            Ui.Clear(plist);
            for (int i = 0; i < list.Count; i++)
            {
                var (p, diff) = list[i];
                bool isCand = i == 0 && diff != null && diff <= IPD_MATCH_MM;
                var b = Ui.Button(plist, p.name, () => selectTcs?.TrySetResult(p),
                                  isCand ? ButtonStyle.Primary : ButtonStyle.Normal, dwell: true, fontSize: 22);
                // 名前の下に小さくベストと差を出す（横並びのレイアウトを縦並びに替える）
                DestroyImmediate(b.GetComponent<HorizontalLayoutGroup>());
                var col = b.gameObject.AddComponent<VerticalLayoutGroup>();
                Ui.Label(b.transform, $"ベスト {p.best}" + (diff != null ? $" ・ 差 {F1(diff.Value)} mm" : ""), 13,
                         Gfx.WithAlpha(isCand ? Gfx.Bg : Gfx.Fg, 0.75f), FontStyle.Normal, TextAnchor.MiddleCenter);
                col.padding = new RectOffset(10, 10, 14, 14);
                col.spacing = 2;
                col.childAlignment = TextAnchor.MiddleCenter;
                col.childControlWidth = true; col.childControlHeight = true;
                col.childForceExpandWidth = true; col.childForceExpandHeight = false;
                if (isCand)
                {
                    var tag = Ui.Label(b.transform, "候補", 11, Gfx.Bg, FontStyle.Bold, TextAnchor.UpperRight);
                    tag.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                    var trt = tag.rectTransform;
                    trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(1, 1);
                    trt.anchoredPosition = new Vector2(-8, -4);
                    trt.sizeDelta = new Vector2(60, 16);
                }
            }
        }

        void OnNewPlayer()
        {
            Hide(selectOv); Show(registerOv);
            regName.text = ""; regMsg.text = "";
            regName.ActivateInputField();
            regName.Select();
        }

        /// <summary>動作確認（-autotest）用：名前の欄に入力する</summary>
        internal void SetRegisterName(string name) => regName.text = name;

        async void OnRegOk()
        {
            if (regBusy) return;
            var name = regName.text.Trim();
            if (name.Length == 0) { regMsg.text = "名前を入力してください"; return; }
            regBusy = true; btnRegOk.interactable = false;
            try
            {
                float? ipd = null;
                if (inputMode == Mode.Tobii)
                {
                    regMsg.text = "目の間隔を測っています…画面の中央を見て、姿勢を保ってください（2 秒）";
                    ipd = await MeasureIpd(2.0f);
                    if (ipd == null) regMsg.text = "目を検出できなかったため、目の間隔なしで登録します";
                }
                players = ParsePlayers((await bridge.Request(new { type = "player_add", name, ipd }, "players"))["players"]);
                selectTcs?.TrySetResult(players.FirstOrDefault(p => p.name == name));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { regMsg.text = e.Message; }
            finally
            {
                regBusy = false;
                if (btnRegOk) btnRegOk.interactable = true;
            }
        }

        // 登録の削除（誤操作防止のため、見つめて押すのではなくクリック＋確認で削除する）
        void OnManage()
        {
            RenderManage();
            Hide(selectOv); Show(manageOv);
        }

        void RenderManage()
        {
            Ui.Clear(manageList);
            foreach (var p in players)
            {
                var row = Ui.Row(manageList, 12);
                var n = Ui.Label(row, p.name, 16, Gfx.Fg, FontStyle.Normal, TextAnchor.MiddleLeft);
                n.gameObject.AddComponent<LayoutElement>().preferredWidth = 160;
                var info = Ui.Label(row, $"{p.plays} 回 / ベスト {p.best}", 16, Gfx.Muted, FontStyle.Normal, TextAnchor.MiddleLeft);
                info.gameObject.AddComponent<LayoutElement>().preferredWidth = 170;
                Ui.Button(row, "削除", () => DeletePlayer(p), ButtonStyle.Danger);
            }
            if (players.Count == 0) Ui.P(manageList, "登録はありません");
        }

        async void DeletePlayer(PlayerInfo p)
        {
            try
            {
                Hide(manageOv);
                var c = await Choice(dialogOv, dialogTitle, dialogMsg, dialogBtns, "登録の削除",
                                     $"「{p.name}」の登録とスコア記録を削除しますか？", ("削除する", "yes", false), ("キャンセル", "no", true));
                if (c == "yes")
                {
                    players = ParsePlayers((await bridge.Request(new { type = "player_delete", id = p.id }, "players"))["players"]);
                    RenderPlayerList();
                }
                RenderManage();
                Show(manageOv);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                SetStatus("【注意】削除できませんでした：" + e.Message);
                Show(manageOv);
            }
        }

        // ---------- ランキング ----------
        static readonly float[] RANK_W = { 64, 180, 80 };
        static readonly TextAnchor[] RANK_A = { TextAnchor.MiddleRight, TextAnchor.MiddleLeft, TextAnchor.MiddleRight };

        // ゲームオーバー画面：上位 5 人（＋自分）
        void RenderRanking(List<PlayerInfo> list, string meId)
        {
            Ui.Clear(overRank);
            if (list == null || list.Count == 0) return;
            var top = list.Take(5).ToList();
            int meIdx = list.FindIndex(p => p.id == meId);
            if (meIdx >= 5) top.Add(list[meIdx]);
            foreach (var p in top)
            {
                bool me = p.id == meId;
                var cells = Ui.TableRow(overRank, RANK_W, new[] { (list.IndexOf(p) + 1) + "位", p.name, p.best.ToString() }, RANK_A,
                                        me ? Gfx.Gold : Gfx.Fg, me ? FontStyle.Bold : FontStyle.Normal);
                if (!me) cells[0].color = Gfx.Muted;
            }
        }

        // ランキングだけを見る（遊んだことのある登録者を自己ベスト順に表示）
        async void OnRankingClicked()
        {
            try
            {
                Hide(titleOv); Show(rankingOv);
                Ui.Clear(rankingTable);
                rankingMsg.text = "読み込み中…";
                try
                {
                    await ConnectBridge();
                    var list = ParsePlayers((await bridge.Request(new { type = "players_get" }, "players"))["players"])
                               .Where(p => p.plays > 0).ToList();
                    rankingMsg.text = list.Count > 0 ? "登録プレイヤーの自己ベスト順です（ゲストの記録は含みません）" : "まだ記録がありません。";
                    if (list.Count == 0) return;
                    var w = new float[] { 64, 180, 80, 70 };
                    var a = new[] { TextAnchor.MiddleRight, TextAnchor.MiddleLeft, TextAnchor.MiddleRight, TextAnchor.MiddleRight };
                    Ui.TableRow(rankingTable, w, new[] { "順位", "名前", "ベスト", "回数" }, a, Gfx.Muted, FontStyle.Normal, 13);
                    // 画面に収まる人数まで（index.html はスクロールで全員）
                    int maxRows = Mathf.Max(5, Mathf.FloorToInt((H - 320) / 28));
                    for (int i = 0; i < list.Count && i < maxRows; i++)
                    {
                        var p = list[i];
                        var cells = Ui.TableRow(rankingTable, w, new[] { (i + 1) + "位", p.name, p.best.ToString(), p.plays + " 回" }, a, Gfx.Fg);
                        cells[0].color = Gfx.Muted;
                        cells[3].color = Gfx.Muted; cells[3].fontSize = 14;
                        if (i == 0) { cells[1].color = Gfx.Gold; cells[1].fontStyle = FontStyle.Bold; }
                    }
                    if (list.Count > maxRows) Ui.P(rankingTable, $"…ほか {list.Count - maxRows} 人", 13);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    rankingMsg.text = "ランキングを読み込めませんでした。先に tobii_bridge.py を起動してください。";
                }
            }
            catch (OperationCanceledException) { }
        }

        // ---------- ゲームオーバー ----------
        async void GameOver()
        {
            state = State.Over;
            Explode(player.x, player.y);
            int sc = Mathf.FloorToInt(elapsed * 10);
            float sec = elapsed;
            var shown = Wait(700);
            string line = $"スコア {sc}（{F1(sec)} 秒生存）";
            Ui.Clear(overRank);
            try
            {
                if (currentPlayer != null && BridgeOK)
                {
                    var res = await bridge.Request(new { type = "score_add", id = currentPlayer.id, score = sc, seconds = sec }, "score_saved");
                    currentPlayer.best = (int?)res["best"] ?? sc;
                    int rank = (int?)res["rank"] ?? 0;
                    line += ((bool?)res["is_best"] ?? false) ? $"　自己ベスト更新！（{rank} 位）" : $"　自己ベスト {currentPlayer.best}（{rank} 位）";
                    RenderRanking(ParsePlayers(res["players"]), currentPlayer.id);
                }
                else
                {
                    if (sc > guestBest) { guestBest = sc; PlayerPrefs.SetInt("gazeDodgeBest", guestBest); PlayerPrefs.Save(); }
                    line += $"　ゲストのベスト {guestBest}（記録はランキングに残りません）";
                    if (BridgeOK) RenderRanking(ParsePlayers((await bridge.Request(new { type = "players_get" }, "players"))["players"]), null);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                line += $"　【注意】保存できませんでした：{e.Message}";
            }
            overScore.text = line;
            hudBest.text = "ベスト: " + (currentPlayer != null ? currentPlayer.best : guestBest);
            btnSwitch.gameObject.SetActive(BridgeOK);
            btnRecalib.gameObject.SetActive(inputMode != Mode.Mouse);
            try { await shown; } catch (OperationCanceledException) { return; }
            overHint.text = (DwellOn ? "ボタンは 1 秒見つめても押せます。" : "") + "Space キーでも再スタート";
            if (state == State.Over) Show(overOv);
        }

        // ---------- ESC で終了 ----------
        // ビルドした exe：ブリッジに終了を依頼してから閉じる（ブリッジはキャリブレーション中止・Tobii の受信停止・ポート解放をして終わる）
        // エディタ：再生を止めるだけ（ブリッジは動かしたままにして、続けて再生できるようにする）
        async void QuitGame()
        {
            if (quitting) return;
            quitting = true;
            state = State.Quit;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
            await Task.CompletedTask;
#else
            foreach (Transform t in canvasT) if (t.name != "Quit") t.gameObject.SetActive(false);
            Show(quitOv);
            if (BridgeOK)
            {
                try { await Task.WhenAny(bridge.Request(new { type = "shutdown" }, "bye"), Task.Delay(3000)); }
                catch (Exception) { /* 応答がなくても続ける */ }
            }
            Application.Quit();
#endif
        }

        /// <summary>動作確認（-autotest）用</summary>
        internal void RequestQuit() => QuitGame();
        internal string StateName => state.ToString();

        /// <summary>動作確認（-autotest）用：表示中のボタンのうち、文字が label を含むものを押す</summary>
        internal bool ClickButton(string label)
        {
            var b = canvasT.GetComponentsInChildren<Button>(false).FirstOrDefault(x => x.interactable && ButtonHas(x, label));
            if (b == null) return false;
            b.onClick.Invoke();
            return true;
        }

        internal bool HasButton(string label) => canvasT.GetComponentsInChildren<Button>(false).Any(x => ButtonHas(x, label));

        // JpText はスペースを U+00A0 に替えているので、普通のスペースに戻して比べる
        static bool ButtonHas(Button b, string label) =>
            b.GetComponentsInChildren<Text>().Any(t => t.text.Replace(' ', ' ').Contains(label));
    }
}
