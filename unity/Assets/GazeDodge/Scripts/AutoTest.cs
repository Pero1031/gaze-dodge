using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace GazeDodge
{
    /// <summary>
    /// 動作確認用の自動操作。exe を「-autotest &lt;出力フォルダ&gt;」付きで起動したときだけ動く（ふだんは何もしない）。
    /// タイトル → ランキング → Tobii（キャリブレーション・検証）→ プレイヤー選択・登録 → プレイ → ゲームオーバー
    /// → マウスモード（ゲスト）→ 終了、の順にボタンを押し、各画面のスクリーンショットと記録（autotest.txt）を残す。
    /// ブリッジは「python tobii_bridge.py --mock --mock-pos 0.5,0.5 --data &lt;テスト用 json&gt;」で動かしておく。
    /// </summary>
    public class AutoTest : MonoBehaviour
    {
        public string outDir;
        GazeDodgeGame game;
        readonly StringBuilder log = new StringBuilder();
        float t0;

        IEnumerator Start()
        {
            game = GetComponent<GazeDodgeGame>();
            Directory.CreateDirectory(outDir);
            t0 = Time.realtimeSinceStartup;
            yield return new WaitUntil(() => UnityEngine.Rendering.SplashScreen.isFinished);
            yield return new WaitForSeconds(1.0f);
            yield return Shot("01_title");

            Click("ランキングを見る");
            yield return new WaitForSeconds(1.2f);
            yield return Shot("02_ranking");
            Click("タイトルへ戻る");
            yield return new WaitForSeconds(0.3f);

            Click("Tobii で始める");
            yield return Until(() => game.IsShown("TobiiPanel"), 5, "Tobii の説明");
            yield return Shot("03_tobii_intro");
            Click("キャリブレーション開始");
            yield return new WaitForSeconds(2.6f);
            yield return Shot("04_calibration");
            yield return Until(() => game.IsShown("TobiiPanel"), 20, "キャリブレーションの結果");
            yield return new WaitForSeconds(0.3f);
            yield return Shot("05_calib_result");
            yield return Until(() => game.HasButton("次へ") || game.HasButton("このまま進む"), 90, "結果の選択肢");
            yield return Shot("06_calib_final");
            if (!Click("次へ")) Click("このまま進む");

            yield return Until(() => game.IsShown("ConfirmPlayer") || (game.IsShown("Select") && game.HasButton("ゲストで遊ぶ")
                                                                         && !game.IsShown("TobiiPanel")), 10, "プレイヤー選択");
            yield return new WaitForSeconds(2.0f);   // 目の間隔の測定（1.5 秒）を待つ
            yield return Shot("07_select_or_confirm");
            if (game.IsShown("ConfirmPlayer"))
            {
                Click("いいえ");
                yield return new WaitForSeconds(0.5f);
                yield return Shot("08_select");
            }
            Click("新規登録");
            yield return new WaitForSeconds(0.3f);
            game.SetRegisterName("テスト" + DateTime.Now.ToString("HHmmss"));
            yield return new WaitForSeconds(0.2f);
            yield return Shot("09_register");
            Click("登録（Enter）");
            yield return Until(() => game.StateName == "Countdown", 10, "カウントダウン");
            yield return new WaitForSeconds(1.0f);
            yield return Shot("10_countdown");

            yield return PlayUntilOver("11_play", "12_over");

            Click("タイトルへ");
            yield return new WaitForSeconds(0.5f);
            Click("マウス（テスト用）");
            yield return Until(() => game.IsShown("Select"), 5, "プレイヤー選択（マウス）");
            yield return new WaitForSeconds(0.8f);
            yield return Shot("13_select_mouse");
            // 登録の削除（確認ダイアログ → 削除）。テスト用のデータなので消してよい
            Click("登録の削除");
            yield return new WaitForSeconds(0.4f);
            yield return Shot("13a_manage");
            Click("削除");
            yield return Until(() => game.IsShown("Dialog"), 3, "削除の確認");
            yield return Shot("13b_delete_confirm");
            Click("削除する");
            yield return Until(() => game.IsShown("Manage"), 5, "削除後の一覧");
            yield return new WaitForSeconds(0.3f);
            yield return Shot("13c_manage_after");
            Click("閉じる");
            yield return new WaitForSeconds(0.3f);
            Click("ゲストで遊ぶ");
            yield return Until(() => game.StateName == "Countdown", 5, "カウントダウン（マウス）");
            yield return PlayUntilOver("14_play_mouse", "15_over_guest");

            Log("終了します");
            File.WriteAllText(Path.Combine(outDir, "autotest.txt"), log.ToString(), new UTF8Encoding(false));
            game.RequestQuit();
        }

        IEnumerator PlayUntilOver(string playShot, string overShot)
        {
            float start = Time.realtimeSinceStartup;
            // 注視点を左右・上下にゆっくり動かす（避ける気はないので、いずれ当たる）
            game.AutoTarget = new Vector2(Screen.width / 2f, Screen.height * 0.75f);
            float shotAt = -1;
            while (game.StateName != "Over" && Time.realtimeSinceStartup - start < 90)
            {
                float t = Time.realtimeSinceStartup - start;
                game.AutoTarget = new Vector2(Screen.width * (0.5f + 0.35f * Mathf.Sin(t * 1.3f)),
                                              Screen.height * (0.75f + 0.1f * Mathf.Sin(t * 0.7f)));
                if (game.StateName == "Play" && shotAt < 0) shotAt = t + 4;
                if (shotAt > 0 && t >= shotAt) { shotAt = float.MaxValue; yield return Shot(playShot); }
                yield return null;
            }
            game.AutoTarget = null;
            Log(game.StateName == "Over" ? $"ゲームオーバー（{Time.realtimeSinceStartup - start:F1} 秒）" : "NG: 90 秒たってもゲームオーバーにならない");
            yield return new WaitForSeconds(1.5f);
            yield return Shot(overShot);
        }

        bool Click(string label)
        {
            bool ok = game.ClickButton(label);
            Log((ok ? "押した: " : "NG: ボタンがない: ") + label);
            return ok;
        }

        IEnumerator Until(Func<bool> cond, float timeoutSec, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!cond() && Time.realtimeSinceStartup - start < timeoutSec) yield return null;
            Log((cond() ? "OK: " : "NG: 待ちきれず: ") + what + $"（{Time.realtimeSinceStartup - start:F1} 秒）");
        }

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(outDir, name + ".png"));
            Log($"撮影: {name}（状態 {game.StateName}）");
            yield return null;
        }

        void Log(string s)
        {
            var line = $"[{Time.realtimeSinceStartup - t0,6:F1}s] {s}";
            log.AppendLine(line);
            Debug.Log("[autotest] " + line);
        }
    }
}
