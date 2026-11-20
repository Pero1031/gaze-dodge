using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GazeDodge.EditorTools
{
    /// <summary>
    /// シーンとプレイヤー設定を作る・Windows 版をビルドする。メニュー「Gaze Dodge」から、またはコマンドラインから使う。
    ///   Unity.exe -batchmode -quit -projectPath unity -executeMethod GazeDodge.EditorTools.ProjectSetup.Run
    ///   Unity.exe -batchmode -quit -projectPath unity -executeMethod GazeDodge.EditorTools.ProjectSetup.BuildWindows
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/GazeDodge/Scenes/Main.unity";
        public const string BuildPath = "Build/GazeDodge.exe";

        [MenuItem("Gaze Dodge/Setup (scene and player settings)")]
        public static void Run()
        {
            PlayerSettings.productName = "Gaze Dodge";
            PlayerSettings.companyName = "GazeDodge";
            PlayerSettings.runInBackground = true;           // フォーカスが外れても視線の受信・描画を止めない
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.defaultIsNativeResolution = true; // モニターの解像度そのまま（Tobii の座標と 1:1 にする）
            PlayerSettings.resizableWindow = true;
            PlayerSettings.allowFullscreenSwitch = true;

            // シーンには本体の GameObject を 1 つ置くだけ（カメラ・UI は実行時に作る）
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("GazeDodge").AddComponent<GazeDodgeGame>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[setup] シーンとプレイヤー設定を作りました: " + ScenePath);
        }

        [MenuItem("Gaze Dodge/Build Windows (unity/Build)")]
        public static void BuildWindows()
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = BuildPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"[build] {s.result}：{BuildPath}（{s.totalSize / 1024 / 1024} MB、エラー {s.totalErrors} 件）");
            if (Application.isBatchMode && s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }
    }
}
