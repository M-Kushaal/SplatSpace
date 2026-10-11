using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GSCapture.EditorTools
{
    /// <summary>
    /// Command-line Android build (APK goes outside OneDrive):
    ///   Unity -batchmode -buildTarget Android -projectPath app
    ///         -executeMethod GSCapture.EditorTools.BuildScript.BuildAndroid -quit
    /// Output path: env GS_APK_PATH, default C:/dev/unity_cache/Builds/gscapture.apk
    /// </summary>
    public static class BuildScript
    {
        [MenuItem("Capture/Build Android APK")]
        public static void BuildAndroidMenu()
        {
            Build();
        }

        public static void BuildAndroid()
        {
            EditorApplication.Exit(Build() ? 0 : 1);
        }

        static bool Build()
        {
            string path = Environment.GetEnvironmentVariable("GS_APK_PATH");
            if (string.IsNullOrEmpty(path)) path = "C:/dev/unity_cache/Builds/gscapture.apk";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ProjectSetup.ScenePath },
                locationPathName = path,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"[GSCapture] build {s.result}: {path} ({s.totalSize / 1e6:F1} MB, {s.totalErrors} errors, " +
                      $"{s.totalTime.TotalSeconds:F0}s)");
            return s.result == BuildResult.Succeeded;
        }
    }
}
