using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GazeDodge
{
    /// <summary>
    /// Gaze Dodge 本体（index.html を Unity に移植したもの）。
    /// 視線でプレイヤー（青い丸）を動かし、上から降ってくる障害物を避け続ける。
    /// 座標はすべて画面の px（左上が原点・下向きが +y）で計算し、描画のときだけワールド座標 (x, -y) に直す。
    ///
    ///   GazeDodgeGame.cs       … ゲームの中身（障害物・当たり判定・描画）
    ///   GazeDodgeGame.Gaze.cs  … ブリッジとの通信・視線の処理（揺れ補正・瞬き対策）・キャリブレーション
    ///   GazeDodgeGame.Flow.cs  … 画面（UI）と画面遷移・プレイヤー登録・ランキング・終了
    /// </summary>
    public partial class GazeDodgeGame : MonoBehaviour
    {
        enum State { Title, Calib, Select, Countdown, Play, Over, Quit }
        enum Mode { None, Tobii, Mouse }

        enum ObType { Circle, Rect, Image }

        class Player
        {
            public float x, y, r = 18;
            public readonly List<Vector2> trail = new List<Vector2>();
        }

        class Obstacle
        {
            public ObType type;
            public float x, y, w, h, r;   // circle: (x, y) は中心・半径 r ／ rect: (x, y) は左上 ／ image: (x, y) は中心
            public float vx, vy, hue, rot, vr, homing;
            public ObstacleImage im;
        }

        class Particle { public float x, y, vx, vy, life; }

        State state = State.Title;
        Mode inputMode = Mode.None;
        float W, H;
        Vector2 target;                 // 注視点（平滑化済み）
        Vector3 lastMouse;

        Player player;
        readonly List<Obstacle> obstacles = new List<Obstacle>();
        readonly List<Particle> particles = new List<Particle>();
        float elapsed, spawnTimer, countdown;
        int guestBest;
        List<ObstacleImage> imageObstacles = new List<ObstacleImage>();

        Camera cam;
        SpriteBatch batch;

        /// <summary>動作確認（-autotest）用：設定すると注視点をこの位置に固定する</summary>
        internal Vector2? AutoTarget;

        // ---------- 起動 ----------
        void Awake()
        {
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = -1;
            W = Screen.width; H = Screen.height;
            target = new Vector2(W / 2, H * 0.75f);

            cam = new GameObject("Camera").AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Gfx.Bg;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 100f;
            UpdateCamera();
            batch = new SpriteBatch(new GameObject("World").transform);

            guestBest = PlayerPrefs.GetInt("gazeDodgeBest", 0);
            blinkFix = !Args.Has("-noblink");
            InitBridge();
            BuildUi();
            Show(titleOv);

            var (images, warning) = ObstacleImages.Load();
            imageObstacles = images;
            if (warning != null) SetStatus(warning);

            var autotest = Args.Get("-autotest");
            if (autotest != null) gameObject.AddComponent<AutoTest>().outDir = autotest;
        }

        void OnDestroy()
        {
            bridge?.Dispose();
        }

        void UpdateCamera()
        {
            cam.orthographicSize = H / 2;
            cam.transform.position = new Vector3(W / 2, -H / 2, -10);
        }

        // ---------- 毎フレーム ----------
        void Update()
        {
            if (Screen.width != (int)W || Screen.height != (int)H)
            {
                W = Screen.width; H = Screen.height;
                UpdateCamera();
            }
            bridge.Pump();   // 届いた視線サンプル・返事を処理する

            if (Input.GetKeyDown(KeyCode.Escape)) QuitGame();
            else if (Input.GetKeyDown(KeyCode.Space) && state == State.Over) ResetGame();

            float dt = Mathf.Min(0.05f, Time.unscaledDeltaTime);
            GameUpdate(dt);
            Draw();
        }

        void GameUpdate(float dt)
        {
            if (state == State.Quit) return;
            if (inputMode == Mode.Mouse)
            {
                var mp = Input.mousePosition;
                if (mp != lastMouse) { lastMouse = mp; target = new Vector2(mp.x, H - mp.y); }
            }
            UpdateGazeTarget();
            if (AutoTarget.HasValue) target = AutoTarget.Value;
            UpdateDwell(dt);
            // カウントダウン中・プレイ中はマウスカーソルを消す（視線のじゃまにならないように）
            Cursor.visible = !(state == State.Countdown || state == State.Play);

            if (state == State.Countdown)
            {
                countdown -= dt;
                MovePlayer(dt);
                if (countdown <= 0) state = State.Play;
            }
            else if (state == State.Play)
            {
                elapsed += dt;
                MovePlayer(dt);
                spawnTimer -= dt;
                if (spawnTimer <= 0) { SpawnObstacle(); spawnTimer = Mathf.Max(0.18f, 0.9f / Difficulty()); }
                foreach (var o in obstacles)
                {
                    if (o.homing > 0) SteerToPlayer(o, dt);
                    o.x += o.vx * dt; o.y += o.vy * dt;
                    if (o.vr != 0) o.rot += o.vr * dt;
                }
                obstacles.RemoveAll(o => (o.type == ObType.Image ? o.y - Mathf.Max(o.w, o.h) / 2 : o.y) >= H + 80);
                if (obstacles.Any(o => Hit(o, player))) GameOver();
                hudScore.text = "スコア: " + Mathf.FloorToInt(elapsed * 10);
            }

            foreach (var p in particles)
            {
                p.x += p.vx * dt; p.y += p.vy * dt;
                p.vx *= 0.96f; p.vy *= 0.96f;
                p.life -= dt * 1.2f;
            }
            particles.RemoveAll(p => p.life <= 0);
        }

        // ---------- ゲーム本体 ----------
        void ResetGame()
        {
            if (quitting) return;
            player = new Player { x = W / 2, y = H * 0.75f };
            playerHist.Clear();
            target = new Vector2(player.x, player.y);
            obstacles.Clear(); particles.Clear();
            elapsed = 0; spawnTimer = 0; countdown = 3;
            state = State.Countdown;
            hud.SetActive(true); Hide(overOv);
            hudWho.text = "プレイヤー: " + (currentPlayer != null ? currentPlayer.name : "ゲスト");
            hudScore.text = "スコア: 0";
            hudBest.text = "ベスト: " + (currentPlayer != null ? currentPlayer.best : guestBest);
            hudMode.text = inputMode == Mode.Tobii ? "Tobii" : "マウス";
        }

        // 難易度: 時間とともに出現頻度・速度が上がる
        float Difficulty() => 1 + elapsed / 25;

        // 横バーの隙間の幅 [px]。序盤は一定で、BAR_NARROW_FROM 秒を過ぎたら少しずつ狭まる。
        // 最小幅はプレイヤーの直径(36px)と視線の誤差を見込んだ値
        const float BAR_GAP_START = 260, BAR_GAP_MIN = 100, BAR_NARROW_FROM = 20, BAR_NARROW_PER_SEC = 1.5f;
        float BarGap() => Mathf.Max(BAR_GAP_MIN, BAR_GAP_START - Mathf.Max(0, elapsed - BAR_NARROW_FROM) * BAR_NARROW_PER_SEC);

        static float Rnd() => Random.value;

        // 障害物を 1 つ出すたびに、各画像を probability の確率で選ぶ（全障害物のうちの割合）。
        // どれも選ばれなければ null（岩か横バーになる）
        ObstacleImage PickObstacleImage()
        {
            float r = Rnd();
            foreach (var im in imageObstacles)
                if ((r -= im.probability) < 0) return im;
            return null;
        }

        void SpawnObstacle()
        {
            float d = Difficulty();
            var im = PickObstacleImage();
            string kind = im != null ? "image" : Rnd() < Mathf.Min(0.35f, elapsed / 120) ? "bar" : "rock";
            if (kind == "bar")
            {
                // 横長のバー（隙間あり）
                float gap = BarGap();
                float gx = 40 + Rnd() * (W - gap - 80);
                float h = 22, vy = 140 * d;
                obstacles.Add(new Obstacle { type = ObType.Rect, x = 0, y = -h, w = gx, h = h, vy = vy });
                obstacles.Add(new Obstacle { type = ObType.Rect, x = gx + gap, y = -h, w = W - gx - gap, h = h, vy = vy });
            }
            else if (kind == "image")
            {
                // 画像の障害物（x, y は中心）。fall_speed があればその速さで落ち、homing_speed があればプレイヤーに横から寄っていく
                float w = im.size.x + Rnd() * (im.size.y - im.size.x), h = w * im.aspect;
                float fall = im.fallSpeed.HasValue
                    ? im.fallSpeed.Value.x + Rnd() * (im.fallSpeed.Value.y - im.fallSpeed.Value.x)
                    : 160 + Rnd() * 140;
                obstacles.Add(new Obstacle
                {
                    type = ObType.Image, im = im, x = Rnd() * W, y = -Mathf.Max(w, h) / 2, w = w, h = h,
                    vx = im.homingSpeed > 0 ? 0 : (Rnd() - 0.5f) * 80 * d, vy = fall * d,
                    homing = im.homingSpeed * d,
                    rot = im.spin ? Rnd() * Mathf.PI * 2 : 0, vr = im.spin ? (Rnd() - 0.5f) * 3 : 0,
                });
            }
            else
            {
                float r = 14 + Rnd() * 26;
                obstacles.Add(new Obstacle
                {
                    type = ObType.Circle, x = Rnd() * W, y = -r, r = r,
                    vx = (Rnd() - 0.5f) * 80 * d, vy = (160 + Rnd() * 140) * d,
                    hue = Rnd() * 40,
                });
            }
        }

        static bool Hit(Obstacle o, Player p)
        {
            if (o.type == ObType.Circle) return Dist(o.x - p.x, o.y - p.y) < o.r + p.r * 0.85f;
            if (o.type == ObType.Image) return HitImage(o, p);
            float cx = Mathf.Max(o.x, Mathf.Min(p.x, o.x + o.w));
            float cy = Mathf.Max(o.y, Mathf.Min(p.y, o.y + o.h));
            return Dist(cx - p.x, cy - p.y) < p.r * 0.85f;
        }

        static float Dist(float dx, float dy) => Mathf.Sqrt(dx * dx + dy * dy);

        // プレイヤーにゆっくり寄っていく障害物。縦は常に下向き（vy は変えない）で、横の速さだけを
        // プレイヤーの方向へ少しずつ変える（最大 o.homing px/s、急に曲がらないよう HOMING_TURN [1/s] でなめらかに）。
        // プレイヤーより下に来たら追うのをやめ、そのまま下へ消える
        const float HOMING_TURN = 1.5f;
        void SteerToPlayer(Obstacle o, float dt)
        {
            if (player == null || o.y > player.y) return;
            float dx = player.x - o.x;
            float want = Mathf.Sign(dx) * Mathf.Min(Mathf.Abs(dx) * 1.2f, o.homing);   // 近いほどゆっくり（行き過ぎない）
            if (dx == 0) want = 0;
            o.vx += (want - o.vx) * Mathf.Min(1, HOMING_TURN * dt);
        }

        static bool HitImage(Obstacle o, Player p)
        {
            float pr = p.r * 0.85f;
            // プレイヤーの中心を画像の座標系（画像の中心が原点、回転を戻した向き）に移す
            float c = Mathf.Cos(-o.rot), s = Mathf.Sin(-o.rot), dx = p.x - o.x, dy = p.y - o.y;
            float lx = dx * c - dy * s, ly = dx * s + dy * c;
            if (Mathf.Abs(lx) > o.w / 2 + pr || Mathf.Abs(ly) > o.h / 2 + pr) return false;
            if (o.im.hitbox == "circle") return Dist(lx, ly) < Mathf.Min(o.w, o.h) / 2 + pr;
            if (o.im.hitbox == "box")
            {
                float cx = Mathf.Max(-o.w / 2, Mathf.Min(lx, o.w / 2)), cy = Mathf.Max(-o.h / 2, Mathf.Min(ly, o.h / 2));
                return Dist(cx - lx, cy - ly) < pr;
            }
            // 形どおり：プレイヤーの円の中心・半径の半分・周上の点のどれかが、画像の不透明な部分に重なれば当たり
            var m = o.im.mask;
            bool Opaque(float x, float y)
            {
                int u = Mathf.FloorToInt((x / o.w + 0.5f) * m.w), v = Mathf.FloorToInt((y / o.h + 0.5f) * m.h);
                return u >= 0 && v >= 0 && u < m.w && v < m.h && m.a[v * m.w + u] >= 128;
            }
            if (Opaque(lx, ly)) return true;
            for (int k = 0; k < 16; k++)
            {
                float a = k * Mathf.PI / 8, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
                if (Opaque(lx + pr * cs, ly + pr * sn) || (k % 2 == 0 && Opaque(lx + pr / 2 * cs, ly + pr / 2 * sn))) return true;
            }
            return false;
        }

        void Explode(float x, float y)
        {
            for (int i = 0; i < 40; i++)
            {
                float a = Rnd() * Mathf.PI * 2, s = 80 + Rnd() * 320;
                particles.Add(new Particle { x = x, y = y, vx = Mathf.Cos(a) * s, vy = Mathf.Sin(a) * s, life = 1 });
            }
        }

        // プレイヤーは注視点に向かって最大速度付きで追従（視線の飛びで瞬間移動しないように）
        //   rate [1/s]：残りの距離を毎秒どれだけ詰めるか（大きいほど遅れが小さい。30 なら遅れの目安は約 33 ms）
        //   maxSpeed [px/s]：最大速度
        const float FOLLOW_RATE = 30, FOLLOW_MAX_SPEED = 3000;   // Tobii・マウス共通（index.html の FOLLOW と同じ）
        void MovePlayer(float dt)
        {
            float dx = target.x - player.x, dy = target.y - player.y;
            float dist = Dist(dx, dy);
            float step = Mathf.Min(dist, Mathf.Min(FOLLOW_MAX_SPEED * dt, dist * FOLLOW_RATE * dt + 0.5f));
            if (dist > 0.5f) { player.x += dx / dist * step; player.y += dy / dist * step; }
            player.x = Mathf.Clamp(player.x, player.r, W - player.r);
            player.y = Mathf.Clamp(player.y, player.r, H - player.r);
            player.trail.Add(new Vector2(player.x, player.y));
            if (player.trail.Count > 14) player.trail.RemoveAt(0);
            double now = Clock.Ms;
            playerHist.Add(new TimedPos { t = now, x = player.x, y = player.y });
            while (playerHist[0].t < now - 500) playerHist.RemoveAt(0);
        }

        // ---------- 描画（index.html の draw() と同じ順に塗り重ねる） ----------
        void Draw()
        {
            batch.Begin();
            DrawWorld();
            batch.End();

            bool inGame = player != null && state != State.Title && state != State.Calib && state != State.Select && state != State.Quit;
            // 姿勢の崩れなどで目が検出できないとき（直前の位置で止まる）。普通の瞬き（0.1〜0.4 秒）では出さない
            eyeLostText.gameObject.SetActive(inGame && inputMode == Mode.Tobii && state != State.Over && Clock.Ms - lastValidGaze > 600);
            bool cd = inGame && state == State.Countdown;
            countdownText.gameObject.SetActive(cd);
            countdownHint.gameObject.SetActive(cd);
            if (cd) countdownText.text = Mathf.CeilToInt(countdown).ToString();
        }

        void DrawWorld()
        {
            // 背景グリッド（スクロール）
            float off = (float)((Clock.Ms / 20) % 40);
            for (float y = off - 40; y < H; y += 40) batch.Draw(Gfx.Square, W / 2, y + 0.5f, W, 1, Gfx.Grid);
            for (float x = 0; x < W; x += 40) batch.Draw(Gfx.Square, x + 0.5f, H / 2, 1, H, Gfx.Grid);

            if (state == State.Calib && calibDot != null)
            {
                float p = Mathf.Min(1, (float)((Clock.Ms - calibDot.t0) / calibDot.dur));
                float r = 24 - 16 * p;
                batch.Draw(Gfx.Circle, calibDot.x, calibDot.y, 2 * r, 2 * r, calibDot.color);
                batch.Draw(Gfx.Circle, calibDot.x, calibDot.y, 6, 6, Color.white);
            }
            if (player == null || state == State.Title || state == State.Calib || state == State.Select) return;

            // 障害物
            foreach (var o in obstacles)
            {
                if (o.type == ObType.Circle)
                    batch.Draw(Gfx.Circle, o.x, o.y, 2 * o.r, 2 * o.r, Gfx.Hsl(o.hue, 0.85f, 0.6f));
                else if (o.type == ObType.Image)
                    batch.Draw(o.im.sprite, o.x, o.y, o.w / o.im.tex.width, o.h / o.im.tex.height, Color.white, -o.rot * Mathf.Rad2Deg);
                else
                    batch.Draw(Gfx.Square, o.x + o.w / 2, o.y + o.h / 2, o.w, o.h, Gfx.Bar);
            }

            if (state != State.Over)
            {
                // 注視点マーカー
                batch.Draw(Gfx.MarkerRing, target.x, target.y, 22, 22, new Color(1, 1, 1, 0.35f));
                // プレイヤー（残像 → 光 → 本体）
                int n = player.trail.Count;
                for (int i = 0; i < n; i++)
                {
                    var t = player.trail[i];
                    float rr = player.r * i / n;
                    batch.Draw(Gfx.Circle, t.x, t.y, 2 * rr, 2 * rr, Gfx.WithAlpha(Gfx.Accent, (float)i / n * 0.3f));
                }
                batch.Draw(Gfx.Glow, player.x, player.y, Gfx.GlowSize, Gfx.GlowSize, Gfx.Accent);
                batch.Draw(Gfx.Circle, player.x, player.y, 2 * player.r, 2 * player.r, Gfx.Accent);
            }

            foreach (var p in particles)
                batch.Draw(Gfx.Square, p.x, p.y, 4, 4, Gfx.WithAlpha(Gfx.Accent, p.life));
        }

        // ---------- 見つめて押すボタン（dwell） ----------
        // 表示中の dwell ボタンを注視点が DWELL_SEC 秒見続けたらクリックする。
        // 今は使わない設定（DWELL_ENABLED = false）。ボタンはマウスでクリックして選ぶ。true にすると index.html と同じく使える
        static readonly bool DWELL_ENABLED = false;
        const float DWELL_SEC = 1.0f;
        DwellTarget dwellEl;
        float dwellT;
        bool DwellOn => DWELL_ENABLED && inputMode != Mode.None && inputMode != Mode.Mouse;

        void UpdateDwell(float dt)
        {
            var btns = DwellOn
                ? canvasT.GetComponentsInChildren<DwellTarget>(false).Where(d => d.GetComponent<Button>().interactable).ToList()
                : new List<DwellTarget>();
            bool active = btns.Count > 0;
            gazeCursor.gameObject.SetActive(active);
            if (!active) { SetDwell(null); return; }
            gazeCursor.anchoredPosition = new Vector2(target.x, -target.y);
            var sp = new Vector2(target.x, H - target.y);
            var el = btns.FirstOrDefault(b => RectTransformUtility.RectangleContainsScreenPoint((RectTransform)b.transform, sp, null));
            if (el != dwellEl) { SetDwell(el); return; }
            if (el == null) return;
            dwellT += dt;
            el.SetProgress(dwellT / DWELL_SEC);
            if (dwellT >= DWELL_SEC) { SetDwell(null); el.GetComponent<Button>().onClick.Invoke(); }
        }

        void SetDwell(DwellTarget el)
        {
            if (dwellEl) dwellEl.SetProgress(0);
            dwellEl = el; dwellT = 0;
        }

        Task Wait(int ms) => Task.Delay(ms, destroyCancellationToken);
    }
}
