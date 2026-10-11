using System;
using System.IO;
using GSCapture;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;

namespace GSCapture.EditorTools
{
    /// <summary>
    /// One-click project configuration: Android player settings, ARCore XR loader, and the capture
    /// scene, all from code so nothing has to be wired by hand. Safe to run repeatedly.
    ///   Menu: Capture > Setup Project
    ///   CLI:  Unity -batchmode -projectPath app -executeMethod GSCapture.EditorTools.ProjectSetup.RunBatch -quit
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/Scenes/Capture.unity";
        const string GeneratedDir = "Assets/GSCapture/Generated";

        [MenuItem("Capture/Setup Project")]
        public static void Setup()
        {
            ConfigurePlayer();
            ConfigureXR();
            BuildScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[GSCapture] project setup complete");
        }

        public static void RunBatch()
        {
            try
            {
                Setup();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "GSCapture";
            PlayerSettings.productName = "GS Capture";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.gscapture.app");
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25; // Unity 6.3 minimum
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            // Phase-1 dev: the app talks to the backend on the user's PC over plain http:// on the LAN.
            // Switch back to HTTPS-only (InsecureHttpOption.NotAllowed) once the backend is deployed with TLS.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            // "Both": legacy Input (touches, gyro) + Input System (AR tracked pose driver).
            var playerSettings = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
            var so = new SerializedObject(playerSettings);
            var prop = so.FindProperty("activeInputHandler");
            if (prop != null && prop.intValue != 2)
            {
                prop.intValue = 2;
                so.ApplyModifiedProperties();
            }
        }

        static void ConfigureXR()
        {
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey,
                out XRGeneralSettingsPerBuildTarget perTarget);
            if (perTarget == null)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
                perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);

            var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            settings.InitManagerOnStart = true;
            bool ok = XRPackageMetadataStore.AssignLoader(settings.Manager, "UnityEngine.XR.ARCore.ARCoreLoader",
                BuildTargetGroup.Android);
            if (!ok && settings.Manager.activeLoaders.Count == 0)
                throw new Exception("Could not enable the ARCore loader (Project Settings > XR Plug-in Management > Android)");
            EditorUtility.SetDirty(perTarget);
            EditorUtility.SetDirty(settings);

            // ARCore itself is required, but we never use the Depth API: don't exclude phones without it.
            var arcore = UnityEditor.XR.ARCore.ARCoreSettings.GetOrCreateSettings();
            arcore.requirement = UnityEditor.XR.ARCore.ARCoreSettings.Requirement.Required;
            arcore.depth = UnityEditor.XR.ARCore.ARCoreSettings.Requirement.Optional;
            EditorUtility.SetDirty(arcore);
        }

        static void BuildScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            Directory.CreateDirectory(GeneratedDir);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));

            var originGo = new GameObject("XR Origin", typeof(XROrigin));
            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(originGo.transform, false);
            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(ARCameraManager),
                typeof(ARCameraBackground), typeof(TrackedPoseDriver));
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(offset.transform, false);
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;

            var tpd = camGo.GetComponent<TrackedPoseDriver>();
            tpd.positionInput = new InputActionProperty(new InputAction("Position", InputActionType.Value,
                "<HandheldARInputDevice>/devicePosition"));
            tpd.rotationInput = new InputActionProperty(new InputAction("Rotation", InputActionType.Value,
                "<HandheldARInputDevice>/deviceRotation"));
            tpd.trackingStateInput = new InputActionProperty(new InputAction("Tracking State", InputActionType.Value,
                "<HandheldARInputDevice>/trackingState"));

            var origin = originGo.GetComponent<XROrigin>();
            origin.Camera = cam;
            origin.CameraFloorOffsetObject = offset;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.NotSpecified;

            var planes = originGo.AddComponent<ARPlaneManager>();
            planes.planePrefab = PlanePrefab();
            planes.requestedDetectionMode = UnityEngine.XR.ARSubsystems.PlaneDetectionMode.Horizontal;
            originGo.AddComponent<ARRaycastManager>();
            originGo.AddComponent<ARPointCloudManager>();
            originGo.AddComponent<ARAnchorManager>();

            new GameObject("GSCapture", typeof(CaptureApp));

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        static GameObject PlanePrefab()
        {
            string matPath = GeneratedDir + "/PlaneMaterial.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 1f, 1f, 0.18f) };
                AssetDatabase.CreateAsset(mat, matPath);
            }
            var go = new GameObject("ARPlane", typeof(ARPlane), typeof(ARPlaneMeshVisualizer), typeof(MeshFilter),
                typeof(MeshRenderer));
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, GeneratedDir + "/ARPlane.prefab");
            UnityEngine.Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
