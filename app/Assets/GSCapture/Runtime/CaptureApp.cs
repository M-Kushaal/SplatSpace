using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using GSCapture.Core;
using GSCapture.Core.Quality;
using GSCapture.Net;
using GSCapture.UI;
using Unity.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GSCapture
{
    /// <summary>
    /// App flow: Home (library + upload status) -> New capture: Object (guided orbit: surface, place, capture)
    /// or Scene (a room: floor, spots, capture; see SceneCaptureFlow) -> Review.
    /// Frames are auto-captured only when aimed, in range, steady and sharp, and saved with their AR pose +
    /// intrinsics into a capture bundle (manifest.json + images + points3d.ply), then uploaded to the backend.
    /// </summary>
    public sealed class CaptureApp : MonoBehaviour
    {
        enum Mode { Home, Scanning, Placing, Capturing, Review }
        enum Kind { Object, Scene }

        [Header("Image <-> pose alignment (verify with `gs_pipeline overlay` on the first capture)")]
        [Tooltip("Reverses image rows so the JPEG encoder (which expects bottom-up rows) writes an upright image.")]
        [SerializeField] XRCpuImage.Transformation imageTransformation = XRCpuImage.Transformation.MirrorX;
        [Tooltip("Extra rotation about the viewing axis if a device's sensor is mounted differently.")]
        [SerializeField] int imageRollDegrees = 0;

        [Header("Capture")]
        [SerializeField] int jpegQuality = 92;
        [SerializeField] float minSecondsBetweenShots = 0.35f;
        [SerializeField] int minFramesToFinish = 20;
        [SerializeField] int maxPoints = 200_000;

        ARSession session;
        ARCameraManager cameraManager;
        ARRaycastManager raycaster;
        ARPlaneManager planeManager;
        ARPointCloudManager pointCloudManager;
        ARAnchorManager anchorManager;
        Camera arCamera;
        bool arSupported = true, cameraConfigured;

        // UI
        HomeScreen home;
        CaptureHud hud;
        SceneHud sceneHud;
        CaptureTypeSheet typeSheet;
        ReviewScreen review;
        SettingsScreen settings;
        Toast toast;
        UploadManager uploads;
        List<CaptureEntry> entries = new List<CaptureEntry>();
        bool libraryDirty;
        float closeArmedUntil;
        string deleteArmedId;
        float deleteArmedUntil;

        Mode mode;
        Kind kind;
        GateConfig gateConfig = GateConfig.Default;

        // scene capture (the second capture type)
        SceneCaptureFlow scene;
        BestFrameBuffer<PendingShot> bestShot;

        // room check (scene captures): features are extracted in the background as photos are saved, so the
        // check at Finish only has to match them
        CheckSheet checkSheet;
        bool checking;
        int nextImageIndex, pendingFeatures;
        readonly ConcurrentDictionary<string, FrameFeatures> features = new ConcurrentDictionary<string, FrameFeatures>();
        static readonly SemaphoreSlim ExtractGate = new SemaphoreSlim(1, 1); // one photo at a time: never starve ARCore
        RoomReport lastReport;
        List<CheckPhoto> lastPhotos;

        // pose integrity (scene captures): a shake once made ARCore's map jump 16 m mid-capture
        PoseGuard poseGuard;
        int mapEpoch;                    // only changes when the user continues without a confirmed position
        bool relocBusy;
        float lastRelocTry, lastRelocTime = -1f;
        RelocResult lastReloc;
        readonly Dictionary<string, (int anchor, Vector3 pos, Vector3 fwd)> frameAnchors =
            new Dictionary<string, (int, Vector3, Vector3)>();
        ARPlane floorPlane;
        float surfaceDistance = 10f, lastMapTime;

        // placement: everything object-related lives under boundsRoot, in the anchor's local frame
        // (object base = local origin), so it follows ARCore's map corrections instead of drifting.
        GameObject boundsRoot, cylinder;
        LineRenderer ringTop, ringBottom;
        ARAnchor anchor;
        AnchoredFrame frame;
        ARPlane lastHitPlane;
        Pose lastHitPose;
        bool placed;

        // capture
        ViewpointDome dome;
        readonly List<GameObject> markers = new List<GameObject>();
        Material markerMaterial;
        string captureId, captureDir;
        float captureStartTime;
        readonly List<CapturedFrame> frames = new List<CapturedFrame>();
        readonly Dictionary<ulong, Vector3> points = new Dictionary<ulong, Vector3>();
        readonly Dictionary<string, int> stats = new Dictionary<string, int>();
        readonly SharpnessTracker sharpness = new SharpnessTracker();
        readonly ExposureWatch exposure = new ExposureWatch();
        readonly Queue<(float t, Vector3 p)> poseHistory = new Queue<(float, Vector3)>();
        int pendingWrites;
        float lastShotTime = -10f, lastPointsTime, lastUnsteadyTime;
        int frameCounter, suggestedSlot = -1;

        static readonly List<ARRaycastHit> Hits = new List<ARRaycastHit>();

        void Awake()
        {
            Screen.orientation = ScreenOrientation.LandscapeLeft;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Input.gyro.enabled = true;
            Application.targetFrameRate = 30;

            session = FindFirstObjectByType<ARSession>();
            cameraManager = FindFirstObjectByType<ARCameraManager>();
            raycaster = FindFirstObjectByType<ARRaycastManager>();
            planeManager = FindFirstObjectByType<ARPlaneManager>();
            pointCloudManager = FindFirstObjectByType<ARPointCloudManager>();
            anchorManager = FindFirstObjectByType<ARAnchorManager>();
            if (anchorManager == null && raycaster != null) anchorManager = raycaster.gameObject.AddComponent<ARAnchorManager>();
            arCamera = cameraManager != null ? cameraManager.GetComponent<Camera>() : Camera.main;
            markerMaterial = new Material(Shader.Find("Sprites/Default"));

            BuildUI();
        }

        void BuildUI()
        {
            UIKit.EnsureEventSystem(transform);
            var canvas = UIKit.Canvas(transform, "UI").transform;
            var safe = UIKit.SafeArea(canvas);
            hud = new CaptureHud(canvas, safe, this);
            sceneHud = new SceneHud(safe, SceneConfig.Default.Sectors);
            sceneHud.Show(false);
            home = new HomeScreen(canvas, safe);
            typeSheet = new CaptureTypeSheet(canvas);
            review = new ReviewScreen(canvas);
            checkSheet = new CheckSheet(canvas);
            checkSheet.FixNow += FixFromReport;
            checkSheet.Save += () =>
            {
                checking = false;
                checkSheet.Hide();
                StartCoroutine(FinishRoutine());
            };
            checkSheet.KeepCapturing += () =>
            {
                checking = false;
                checkSheet.Hide();
            };
            settings = new SettingsScreen(canvas);
            toast = new Toast(canvas, this);
            uploads = gameObject.AddComponent<UploadManager>();
            uploads.Changed += () => libraryDirty = true;

            hud.Close += OnClose;
            hud.StartCapture += () =>
            {
                if (kind == Kind.Scene) EnterSceneCapturing();
                else EnterCapturing();
            };
            sceneHud.NextSpot += () =>
            {
                FlushBestShot();
                scene?.LeaveStation();
            };
            sceneHud.StartSpot += () =>
            {
                if (scene == null) return;
                scene.StartStation(arCamera.transform.position, arCamera.transform.forward);
                toast.Show("New spot - hold the phone out and turn slowly", Theme.Info);
            };
            sceneHud.ContinueHere += ContinueAsNewSpot;
            sceneHud.ManualShot += ManualSceneShot;
            sceneHud.ToggleCloseups += () =>
            {
                FlushBestShot();
                scene?.ToggleCloseups();
            };
            typeSheet.ObjectChosen += () => { typeSheet.Hide(); NewCapture(Kind.Object); };
            typeSheet.SceneChosen += () => { typeSheet.Hide(); NewCapture(Kind.Scene); };
            typeSheet.Cancelled += () => typeSheet.Hide();
            hud.Finish += () => StartCoroutine(kind == Kind.Scene ? CheckRoutine() : FinishRoutine());
            hud.Radius.onValueChanged.AddListener(_ => UpdateCylinder());
            hud.Height.onValueChanged.AddListener(_ => UpdateCylinder());

            home.NewCapture += () => typeSheet.Show();
            home.OpenSettings += ShowSettings;
            home.Upload += e => { uploads.Upload(e); toast.Show("Uploading… keep the app open", Theme.Info); };
            home.Export += e => ExportZip(e.Dir, e.Id);
            home.Delete += DeleteEntry;
            home.View += e => StartCoroutine(uploads.ViewerLink(e, (url, err) =>
            {
                if (err != null) toast.Show($"Can't open the viewer: {err}", Theme.Danger);
                else Application.OpenURL(url); // phone browser; the viewer is served by the backend at /viewer
            }));
            home.Download += e => StartCoroutine(uploads.DownloadSplat(e, err =>
                toast.Show(err == null ? "Splat saved on this phone" : $"Download failed: {err}", err == null ? Theme.Success : Theme.Danger)));

            review.Upload += () =>
            {
                review.Hide();
                ShowHome();
                var e = entries.Find(x => x.Dir == captureDir);
                if (e != null)
                {
                    uploads.Upload(e);
                    toast.Show("Uploading… keep the app open", Theme.Info);
                }
            };
            review.Export += () => ExportZip(captureDir, captureId);
            review.Discard += () =>
            {
                review.Hide();
                if (Directory.Exists(captureDir)) Directory.Delete(captureDir, true);
                Thumbnails.Forget(captureDir);
                ShowHome();
                toast.Show("Capture discarded", Theme.TextDim);
            };
            review.Done += () => { review.Hide(); ShowHome(); };

            settings.Closed += () => settings.Hide();
            settings.Test += url => StartCoroutine(new BackendClient(url).Health((ok, msg) =>
                settings.SetStatus(ok ? "✓ Connected" : msg, ok ? Theme.Success : Theme.Danger)));
            settings.Save += url =>
            {
                BackendClient.SavedServer = url;
                settings.Hide();
                CheckServer();
                toast.Show("Server saved", Theme.Success);
            };

            review.Hide();
            settings.Hide();
        }

        IEnumerator Start()
        {
            ShowHome();
            if (session == null || cameraManager == null || raycaster == null)
            {
                arSupported = false;
                toast.Show("Scene is missing AR components - run Capture > Setup Project", Theme.Danger, 8f);
                yield break;
            }
            yield return ARSession.CheckAvailability();
            arSupported = ARSession.state != ARSessionState.Unsupported;
            StartCoroutine(PollLoop());
        }

        // ------------------------------------------------------------------ home / library

        void ShowHome()
        {
            mode = Mode.Home;
            ClearScene();
            if (session != null) session.enabled = false; // camera off while browsing: saves battery and heat
            hud.Show(false);
            home.Root.gameObject.SetActive(true);
            RefreshLibrary();
            CheckServer();
        }

        void RefreshLibrary()
        {
            entries = CaptureLibrary.Scan(CaptureLibrary.Root);
            home.Refresh(entries, uploads.Progress);
            libraryDirty = false;
        }

        void CheckServer()
        {
            string url = BackendClient.SavedServer;
            home.SetServer(url, null);
            StartCoroutine(new BackendClient(url).Health((ok, _) => home.SetServer(url, ok)));
        }

        IEnumerator PollLoop()
        {
            var wait = new WaitForSecondsRealtime(5f);
            while (true)
            {
                yield return wait;
                if (mode != Mode.Home) continue;
                yield return uploads.Poll(entries);
            }
        }

        void ShowSettings()
        {
            settings.Show(BackendClient.SavedServer,
                $"Device account {BackendClient.DeviceEmail.Substring(0, 15)}… · {entries.Count} captures on this phone");
        }

        void DeleteEntry(CaptureEntry e)
        {
            if (deleteArmedId != e.Id || Time.unscaledTime > deleteArmedUntil)
            {
                deleteArmedId = e.Id;
                deleteArmedUntil = Time.unscaledTime + 3f;
                toast.Show("Tap Delete again to remove this capture", Theme.Warning);
                return;
            }
            deleteArmedId = null;
            CaptureLibrary.Delete(e);
            Thumbnails.Forget(e.Dir);
            RefreshLibrary();
            toast.Show("Capture deleted", Theme.TextDim);
        }

        void ExportZip(string dir, string id)
        {
            try
            {
                string exportDir = Path.Combine(Application.persistentDataPath, "exports");
                Directory.CreateDirectory(exportDir);
                string zip = Path.Combine(exportDir, id + ".zip");
                if (File.Exists(zip)) File.Delete(zip);
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    foreach (var f in CaptureLibrary.UploadFiles(dir))
                        archive.CreateEntryFromFile(Path.Combine(dir, f.path), f.path, System.IO.Compression.CompressionLevel.NoCompression);
                toast.Show($"Exported to Android/data/{Application.identifier}/files/exports/{id}.zip", Theme.Success, 5f);
            }
            catch (Exception ex)
            {
                toast.Show($"Export failed: {ex.Message}", Theme.Danger);
            }
        }

        // ------------------------------------------------------------------ capture flow

        void NewCapture(Kind k)
        {
            kind = k;
            if (!arSupported)
            {
                toast.Show("This phone does not support ARCore", Theme.Danger);
                return;
            }
            home.Root.gameObject.SetActive(false);
            hud.Show(true);
            session.enabled = true;
            if (!cameraConfigured) StartCoroutine(ConfigureCameraWhenTracking());
            EnterScanning();
        }

        IEnumerator ConfigureCameraWhenTracking()
        {
            while (ARSession.state != ARSessionState.SessionTracking) yield return null;
            SelectHighestResolutionCamera();
            cameraConfigured = true;
        }

        void SelectHighestResolutionCamera()
        {
            using (var configs = cameraManager.GetConfigurations(Allocator.Temp))
            {
                if (!configs.IsCreated || configs.Length == 0) return;
                var best = configs[0];
                foreach (var c in configs)
                    if (c.width * c.height > best.width * best.height) best = c;
                try
                {
                    cameraManager.currentConfiguration = best;
                    Debug.Log($"[GSCapture] camera configuration {best}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GSCapture] could not set camera configuration: {e.Message}");
                }
            }
        }

        void EnterScanning()
        {
            mode = Mode.Scanning;
            ClearScene();
            hud.SetStepNames(kind == Kind.Scene ? CaptureHud.SceneSteps : CaptureHud.ObjectSteps);
            hud.SetStep(0);
            if (kind == Kind.Scene)
                hud.SetHint("Find the floor", "Point the phone at the floor and slowly sweep it around");
            else
                hud.SetHint("Find the surface", "Slowly sweep the phone over the table or floor the object stands on");
            hud.SetCoach(null, Theme.Info);
            hud.SetPlacement(false, false);
            hud.SetCapturing(false);
            if (planeManager != null)
            {
                planeManager.enabled = true;
                foreach (var p in planeManager.trackables) p.gameObject.SetActive(true);
            }
        }

        void EnterPlacing()
        {
            mode = Mode.Placing;
            if (kind == Kind.Scene)
            {
                hud.SetStep(1);
                hud.SetHint("Floor found", "Lights on, nothing moving. Start near a corner, hold the phone out at arm's length, then tap Start");
                hud.SetCoach(null, Theme.Info);
                hud.SetStartOnly(true, true, "Start scene");
                return;
            }
            hud.SetStep(1);
            hud.SetHint(null);
            hud.SetCoach("Tap the surface right under the object's centre", Theme.Info);
            hud.SetPlacement(true, false);
        }

        void EnterCapturing()
        {
            if (!placed) return;
            gateConfig = GateConfig.Default;
            mode = Mode.Capturing;
            hud.SetStep(2);
            hud.SetPlacement(false, false);
            hud.SetCapturing(true);
            hud.SetCoach("Walk to the glowing marker", Theme.Info);
            SetCylinderStyle(capturing: true);
            if (planeManager != null)
            {
                planeManager.enabled = false;
                foreach (var p in planeManager.trackables) p.gameObject.SetActive(false);
            }

            captureId = CaptureBundleWriter.NewCaptureId();
            captureDir = Path.Combine(CaptureLibrary.Root, captureId);
            Directory.CreateDirectory(Path.Combine(captureDir, "images"));
            frames.Clear();
            features.Clear();
            nextImageIndex = 0;
            stats.Clear();
            captureStartTime = Time.time;

            AnchorToLastHit(); // final placement -> AR anchor
            dome = new ViewpointDome(Vector3.zero, hud.Radius.value, hud.Height.value, MinFovDeg());
            BuildMarkers();
        }

        /// <summary>Lowest upward-facing plane = the floor (tables are higher).</summary>
        ARPlane FindFloor()
        {
            ARPlane best = null;
            if (planeManager == null) return null;
            foreach (var p in planeManager.trackables)
            {
                if (p.alignment != PlaneAlignment.HorizontalUp || p.size.x * p.size.y < 0.25f) continue;
                if (best == null || p.center.y < best.center.y) best = p;
            }
            return best;
        }

        void EnterSceneCapturing()
        {
            floorPlane = FindFloor();
            float floorY = floorPlane != null ? floorPlane.center.y : arCamera.transform.position.y - 1.4f;
            gateConfig = GateConfig.Scene;
            mode = Mode.Capturing;
            hud.SetStep(2);
            hud.SetHint(null);
            hud.SetStartOnly(false, false, "");
            hud.SetCapturing(true);
            hud.SetCoach("Arm out - turn slowly on the spot", Theme.Info);
            if (planeManager != null)
            {
                planeManager.enabled = false;
                foreach (var p in planeManager.trackables) p.gameObject.SetActive(p == floorPlane);
            }

            captureId = CaptureBundleWriter.NewCaptureId();
            captureDir = Path.Combine(CaptureLibrary.Root, captureId);
            Directory.CreateDirectory(Path.Combine(captureDir, "images"));
            frames.Clear();
            features.Clear();
            nextImageIndex = 0;
            stats.Clear();
            captureStartTime = Time.time;
            scene = new SceneCaptureFlow(sceneHud, anchorManager, markerMaterial);
            bestShot = new BestFrameBuffer<PendingShot>(shot => shot.Dispose());
            poseGuard = new PoseGuard();
            mapEpoch = 0;
            relocBusy = false;
            lastRelocTime = -1f;
            frameAnchors.Clear();
            scene.Begin(arCamera.transform.position, arCamera.transform.forward, floorY, floorPlane);
        }

        /// <summary>Distance to whatever is in the middle of the view (AR feature points / planes).</summary>
        float CenterSurfaceDistance()
        {
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (raycaster != null && raycaster.Raycast(center, Hits, TrackableType.FeaturePoint | TrackableType.PlaneWithinPolygon))
                return Hits[0].distance;
            return 10f; // nothing known: don't block
        }

        void OnClose()
        {
            if (mode == Mode.Capturing && frames.Count > 0 && Time.unscaledTime > closeArmedUntil)
            {
                closeArmedUntil = Time.unscaledTime + 3f;
                toast.Show("Tap ✕ again to discard this scan", Theme.Warning);
                return;
            }
            if (mode == Mode.Capturing && captureDir != null && !File.Exists(Path.Combine(captureDir, "manifest.json")))
                StartCoroutine(DeleteWhenSaved(captureDir));
            ShowHome();
        }

        IEnumerator DeleteWhenSaved(string dir)
        {
            while (pendingWrites > 0) yield return null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (mode == Mode.Home) RefreshLibrary();
        }

        void ClearScene()
        {
            ClearMarkers();
            if (boundsRoot != null) Destroy(boundsRoot);
            boundsRoot = cylinder = null;
            RemoveAnchor();
            frame = null;
            lastHitPlane = null;
            placed = false;
            dome = null;
            checking = false;
            checkSheet?.Hide();
            bestShot?.Clear();
            bestShot = null;
            scene?.Clear();
            scene = null;
            floorPlane = null;
            sceneHud?.Show(false);
            points.Clear();
        }

        // ------------------------------------------------------------------ per-frame

        void Update()
        {
            frameCounter++;
            if (Input.GetKeyDown(KeyCode.Escape)) OnBack();

            if (mode == Mode.Home)
            {
                if (libraryDirty) home.Refresh(entries, uploads.Progress);
                libraryDirty = false;
                return;
            }
            if (Time.time - lastPointsTime > 0.5f && mode != Mode.Review)
            {
                lastPointsTime = Time.time;
                AccumulatePoints();
            }

            FollowAnchor();
            switch (mode)
            {
                case Mode.Scanning:
                    if (planeManager != null && planeManager.trackables.count > 0) EnterPlacing();
                    break;
                case Mode.Placing:
                    if (kind == Kind.Object) HandlePlacementTouch(); // scene captures have nothing to place
                    break;
                case Mode.Capturing:
                    if (kind == Kind.Scene)
                    {
                        if (!checking) UpdateSceneCapture(); // paused while the room check sheet is up
                    }
                    else UpdateCapture();
                    break;
            }
        }

        void OnBack()
        {
            if (settings.Root.gameObject.activeSelf) settings.Hide();
            else if (checkSheet.Visible)
            {
                checking = false;
                checkSheet.Hide();
            }
            else if (mode == Mode.Review) { review.Hide(); ShowHome(); }
            else if (mode != Mode.Home) OnClose();
        }

        void HandlePlacementTouch()
        {
            if (Input.touchCount != 1) return;
            var touch = Input.GetTouch(0);
            if (touch.phase != TouchPhase.Began && touch.phase != TouchPhase.Moved) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(touch.fingerId)) return;
            if (!raycaster.Raycast(touch.position, Hits, TrackableType.PlaneWithinPolygon)) return;

            // While the finger moves the base follows the hit; the anchor is created once, when capture starts.
            lastHitPose = Hits[0].pose;
            lastHitPlane = Hits[0].trackable as ARPlane;
            RemoveAnchor();
            frame = new AnchoredFrame(lastHitPose.position);
            if (cylinder == null) CreateCylinder();
            boundsRoot.transform.position = frame.Display;
            placed = true;
            UpdateCylinder();
            hud.SetPlacement(true, true);
            hud.SetCoach("Resize the cylinder so the whole object fits inside", Theme.Info);
        }

        /// <summary>Pin the placed base to the tapped plane with an AR anchor (falls back to a fixed position).</summary>
        void AnchorToLastHit()
        {
            if (anchor != null || frame == null || anchorManager == null || lastHitPlane == null) return;
            try
            {
                anchor = anchorManager.AttachAnchor(lastHitPlane, lastHitPose);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GSCapture] could not create anchor: {e.Message}");
            }
            if (anchor == null) Count("NoAnchor");
        }

        void RemoveAnchor()
        {
            if (anchor == null) return;
            if (anchorManager == null || !anchorManager.TryRemoveAnchor(anchor)) Destroy(anchor.gameObject);
            anchor = null;
        }

        /// <summary>Track the anchor (capture math) and ease the visuals toward it.</summary>
        void FollowAnchor()
        {
            if (frame == null || boundsRoot == null) return;
            Vector3 target = anchor != null && anchor.trackingState != TrackingState.None ? anchor.transform.position : frame.Origin;
            boundsRoot.transform.position = frame.Update(target, Time.deltaTime);
        }

        void CreateCylinder()
        {
            boundsRoot = new GameObject("ObjectAnchor");
            cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylinder.name = "ObjectBounds";
            cylinder.transform.SetParent(boundsRoot.transform, false);
            Destroy(cylinder.GetComponent<Collider>());
            cylinder.GetComponent<Renderer>().material = new Material(Shader.Find("Sprites/Default"));
            ringTop = MakeRing("RingTop");
            ringBottom = MakeRing("RingBottom");
            SetCylinderStyle(capturing: false);
        }

        LineRenderer MakeRing(string name)
        {
            var go = new GameObject(name); // positions are local to boundsRoot (the anchor frame)
            go.transform.SetParent(boundsRoot.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = markerMaterial;
            lr.loop = true;
            lr.positionCount = 64;
            lr.useWorldSpace = false;
            lr.widthMultiplier = 0.004f;
            lr.numCapVertices = 2;
            return lr;
        }

        void SetCylinderStyle(bool capturing)
        {
            if (cylinder == null) return;
            cylinder.GetComponent<Renderer>().material.color = Theme.WithAlpha(Theme.Accent, capturing ? 0.06f : 0.16f);
            var c = Theme.WithAlpha(Theme.Accent, capturing ? 0.55f : 0.95f);
            foreach (var lr in new[] { ringTop, ringBottom }) lr.startColor = lr.endColor = c;
        }

        void UpdateCylinder()
        {
            if (cylinder == null) return;
            float r = hud.Radius.value, h = hud.Height.value;
            cylinder.transform.localPosition = Vector3.up * (h * 0.5f);
            cylinder.transform.localScale = new Vector3(2 * r, h * 0.5f, 2 * r); // Unity cylinder: height 2, diameter 1
            SetRing(ringBottom, Vector3.zero, r);
            SetRing(ringTop, Vector3.up * h, r);
        }

        static void SetRing(LineRenderer lr, Vector3 center, float r)
        {
            for (int i = 0; i < lr.positionCount; i++)
            {
                float a = 2 * Mathf.PI * i / lr.positionCount;
                lr.SetPosition(i, center + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r));
            }
        }

        void UpdateCapture()
        {
            Vector3 camWorld = arCamera.transform.position;
            Vector3 camPos = frame.ToLocal(camWorld); // dome math is in the anchor frame
            Vector3 camFwd = arCamera.transform.forward;
            float now = Time.time;

            poseHistory.Enqueue((now, camWorld));
            while (poseHistory.Count > 1 && now - poseHistory.Peek().t > 0.15f) poseHistory.Dequeue();
            var oldest = poseHistory.Peek();
            float linearSpeed = now - oldest.t > 1e-3f ? Vector3.Distance(camWorld, oldest.p) / (now - oldest.t) : 0f;
            float angularSpeed = Input.gyro.rotationRateUnbiased.magnitude;
            if (angularSpeed > gateConfig.MaxAngularSpeed || linearSpeed > gateConfig.MaxLinearSpeed)
                lastUnsteadyTime = now;

            var match = dome.Evaluate(camPos, camFwd);
            var input = new GateInput
            {
                Busy = pendingWrites > 2 || now - lastShotTime < minSecondsBetweenShots,
                Tracking = ARSession.state == ARSessionState.SessionTracking &&
                           ARSession.notTrackingReason == NotTrackingReason.None,
                AimAngleDeg = match.AimAngleDeg,
                DistanceRatio = match.DistanceRatio,
                SlotAngleDeg = match.SlotAngleDeg,
                SlotFilled = match.SlotIndex >= 0 && dome.Slots[match.SlotIndex].Filled,
                AngularSpeed = angularSpeed,
                LinearSpeed = linearSpeed,
                SteadySeconds = now - lastUnsteadyTime,
                SharpnessRatio = 1f,
                Brightness = 255f,
            };
            var reason = CaptureGate.EvaluateGeometry(input, gateConfig);

            // Keep the sharpness baseline fresh even when not shooting.
            bool sample = reason == GateReason.Ok || frameCounter % 10 == 0;
            if (sample && cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                using (image)
                {
                    var y = image.GetPlane(0);
                    var quality = BlurMeter.Measure(y.data, image.width, image.height, y.rowStride, y.pixelStride);
                    double score = quality.Sharpness;
                    input.SharpnessRatio = sharpness.Ratio(score);
                    input.Brightness = quality.Brightness;
                    sharpness.Add(score);
                    if (reason == GateReason.Ok)
                    {
                        reason = CaptureGate.Evaluate(input, gateConfig);
                        int slot = match.SlotIndex;
                        if (reason == GateReason.Ok) Shoot(image, () => { dome.MarkFilled(slot); return slot; }, (float)score, angularSpeed);
                    }
                }
            }

            if (reason != GateReason.Busy)
            {
                Count(reason.ToString());
                if (reason != GateReason.Ok) hud.SetCoach(CaptureGate.Coaching(reason), CaptureHud.CoachColor(reason));
            }
            suggestedSlot = dome.SuggestNext(camPos);
            UpdateMarkers(match.SlotIndex);
            hud.SetProgress(frames.Count, dome.Coverage, frames.Count >= minFramesToFinish, Direction(camPos), pendingWrites > 0);
        }

        /// <summary>Scene captures: same quality gate as objects, but SceneCaptureFlow decides where to shoot.</summary>
        void UpdateSceneCapture()
        {
            Vector3 lens = arCamera.transform.position, fwd = arCamera.transform.forward;
            float now = Time.time;
            poseHistory.Enqueue((now, lens));
            while (poseHistory.Count > 1 && now - poseHistory.Peek().t > 0.15f) poseHistory.Dequeue();
            var oldest = poseHistory.Peek();
            float linearSpeed = now - oldest.t > 1e-3f ? Vector3.Distance(lens, oldest.p) / (now - oldest.t) : 0f;
            float angularSpeed = Input.gyro.rotationRateUnbiased.magnitude;
            if (angularSpeed > gateConfig.MaxAngularSpeed || linearSpeed > gateConfig.MaxLinearSpeed)
                lastUnsteadyTime = now;
            if (frameCounter % 5 == 0) surfaceDistance = CenterSurfaceDistance();

            // Pose integrity first: if the AR pose can't be trusted, nothing is shot until it is confirmed again.
            float anchorJump = scene.SyncAnchors();
            bool trackingOk = ARSession.state == ARSessionState.SessionTracking &&
                              ARSession.notTrackingReason == NotTrackingReason.None;
            var poseEvent = poseGuard.Update(now, lens, arCamera.transform.rotation, angularSpeed, trackingOk, anchorJump);
            if (poseEvent != PoseEvent.None) OnPoseEvent(poseEvent);
            if (poseGuard.Paused)
            {
                UpdateRelocalize(now, lens, fwd);
                bool mapDuePaused = now - lastMapTime > 0.25f;
                if (mapDuePaused) lastMapTime = now;
                scene.UpdateHud(lens, fwd, points.Values, mapDuePaused);
                return;
            }
            sceneHud.ShowContinue(false);

            if (bestShot.Tick(now, out PendingShot due)) SaveStationShot(due);
            // Station sectors are buffered (best frame per sector), so only the walls pass needs shot spacing.
            bool spacing = scene.Phase != ScenePhase.Spin && now - lastShotTime < minSecondsBetweenShots;
            var input = new GateInput
            {
                Busy = pendingWrites > 2 || spacing || frames.Count >= SceneCaptureFlow.MaxFrames,
                Tracking = ARSession.state == ARSessionState.SessionTracking &&
                           ARSession.notTrackingReason == NotTrackingReason.None,
                AngularSpeed = angularSpeed,
                LinearSpeed = linearSpeed,
                SteadySeconds = now - lastUnsteadyTime,
                SharpnessRatio = 1f,
                Brightness = 255f,
            };
            var placement = scene.Placement(lens, fwd, surfaceDistance);
            var reason = CaptureGate.EvaluateSceneGeometry(input, placement, gateConfig);

            bool sample = reason == GateReason.Ok || frameCounter % 10 == 0;
            if (sample && cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                using (image)
                {
                    var y = image.GetPlane(0);
                    var quality = BlurMeter.Measure(y.data, image.width, image.height, y.rowStride, y.pixelStride);
                    double score = quality.Sharpness;
                    // light changed (window): wait until exposure settles, then judge blur against a fresh baseline
                    if (exposure.Update(now, quality.Brightness)) sharpness.Clear();
                    input.SharpnessRatio = sharpness.Ratio(score);
                    input.Brightness = quality.Brightness;
                    if (!exposure.Adjusting) sharpness.Add(score);
                    if (reason == GateReason.Ok && exposure.Adjusting) reason = GateReason.AdjustingLight;
                    if (reason == GateReason.Ok)
                    {
                        reason = CaptureGate.EvaluateImage(input, gateConfig);
                        if (reason == GateReason.Ok && scene.LastWasStation)
                        {
                            var match = scene.LastMatch;
                            int key = scene.SectorKey(match);
                            if (bestShot.Wants(key, (float)score))
                            {
                                var shot = Grab(image, (float)score, angularSpeed);
                                if (shot != null)
                                {
                                    shot.Match = match;
                                    shot.Lens = lens;
                                    shot.Fwd = fwd;
                                    if (bestShot.Offer(key, (float)score, now, shot, out PendingShot ready)) SaveStationShot(ready);
                                }
                            }
                        }
                        else if (reason == GateReason.Ok) Shoot(image, () => scene.Commit(lens, fwd), (float)score, angularSpeed);
                    }
                }
            }

            if (reason != GateReason.Busy)
            {
                Count(reason.ToString());
                if (reason != GateReason.Ok) hud.SetCoach(scene.Coach(reason), CaptureHud.CoachColor(reason));
            }
            else if (frames.Count >= SceneCaptureFlow.MaxFrames) hud.SetCoach("Enough photos - tap Finish", Theme.Success);

            bool mapDue = now - lastMapTime > 0.25f;
            if (mapDue) lastMapTime = now;
            scene.UpdateHud(lens, fwd, points.Values, mapDue);
            hud.SetProgress(frames.Count, scene.Coverage, scene.CanFinish(frames.Count), scene.Hint(lens, fwd, frames.Count),
                pendingWrites > 0, scene.CoverageLabel, scene.FinishBlockedLabel(frames.Count));
        }

        string Direction(Vector3 camPos)
        {
            if (suggestedSlot < 0) return "✓  All markers captured - tap Finish";
            dome.CameraAngles(camPos, out float el, out float az);
            var s = dome.Slots[suggestedSlot];
            float dAz = Mathf.DeltaAngle(az, s.AzimuthDeg), dEl = s.ElevationDeg - el;
            if (Mathf.Abs(dEl) > Mathf.Abs(dAz) * 0.5f && Mathf.Abs(dEl) > 6f)
                return dEl > 0 ? "↑  Raise the phone and look down at the object" : "↓  Lower the phone";
            return dAz > 0 ? "→  Walk right around the object" : "←  Walk left around the object";
        }

        /// <summary>A converted camera image + its pose, not yet saved (owns a native buffer until saved or
        /// disposed). Scene sectors keep the sharpest of these per sector (BestFrameBuffer).</summary>
        sealed class PendingShot
        {
            public NativeArray<byte> Buffer;
            public CapturedFrame Frame;
            public SceneMatch Match;
            public Vector3 Lens, Fwd;

            public void Dispose()
            {
                if (Buffer.IsCreated) Buffer.Dispose();
            }
        }

        /// <summary>Save the frame with its pose. `commit` marks the target as captured and returns its id;
        /// it runs only once the image is known to be good.</summary>
        void Shoot(XRCpuImage image, Func<int> commit, float blurScore, float angularSpeed)
        {
            var shot = Grab(image, blurScore, angularSpeed);
            if (shot != null) Save(shot, commit);
        }

        void SaveStationShot(PendingShot shot)
        {
            Save(shot, () => scene != null ? scene.CommitStation(shot.Match, shot.Lens, shot.Fwd) : -1);
        }

        /// <summary>Commit the buffered best frame of the current sector (before leaving a spot / finishing).</summary>
        void FlushBestShot()
        {
            if (bestShot != null && bestShot.Flush(out PendingShot shot)) SaveStationShot(shot);
        }

        /// <summary>Convert the camera image and record the current pose + intrinsics (null on failure).</summary>
        PendingShot Grab(XRCpuImage image, float blurScore, float angularSpeed)
        {
            if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr))
            {
                Count("NoIntrinsics");
                return null;
            }
            var conv = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32, imageTransformation);
            var buffer = new NativeArray<byte>(image.GetConvertedDataSize(conv), Allocator.Persistent);
            try
            {
                image.Convert(conv, buffer);
            }
            catch (Exception e)
            {
                buffer.Dispose();
                Debug.LogError($"[GSCapture] image conversion failed: {e}");
                Count("ConvertFailed");
                return null;
            }

            int w = image.width, h = image.height;
            float sx = (float)w / intr.resolution.x, sy = (float)h / intr.resolution.y;
            var pose = PoseConverter.ApplyImageRoll(
                PoseConverter.ToOpenGLCameraToWorld(arCamera.transform.position, arCamera.transform.rotation),
                imageRollDegrees);
            return new PendingShot
            {
                Buffer = buffer,
                Lens = arCamera.transform.position,
                Fwd = arCamera.transform.forward,
                Frame = new CapturedFrame
                {
                    CameraToWorldGL = pose,
                    Fx = intr.focalLength.x * sx,
                    Fy = intr.focalLength.y * sy,
                    Cx = intr.principalPoint.x * sx,
                    Cy = intr.principalPoint.y * sy,
                    Width = w,
                    Height = h,
                    Timestamp = image.timestamp,
                    BlurScore = blurScore,
                    AngularVelocity = angularSpeed,
                },
            };
        }

        /// <summary>Mark the target (commit returns its id, or &lt; 0 if it no longer needs a photo) and write
        /// the JPEG in the background.</summary>
        void Save(PendingShot shot, Func<int> commit)
        {
            int slot = commit();
            if (slot < 0)
            {
                shot.Dispose();
                return;
            }
            var buffer = shot.Buffer;
            var cf = shot.Frame;
            int w = cf.Width, h = cf.Height;
            string rel = $"images/{nextImageIndex++:D6}.jpg"; // not frames.Count: the room check can delete photos
            cf.FilePath = rel;
            cf.TargetId = slot;
            cf.AnchorId = -1;
            if (kind == Kind.Scene && scene != null && poseGuard != null)
            {
                cf.TrackingEpoch = poseGuard.Epoch;
                var st = scene.Planner.Current;
                int a = st != null ? st.Index : scene.Planner.NearestStation(shot.Lens, shot.Fwd);
                cf.AnchorId = a;
                if (a >= 0)
                {
                    var sa = scene.Planner.Stations[a];
                    frameAnchors[rel] = (a, sa.ToLocal(shot.Lens), sa.DirToLocal(shot.Fwd));
                }
                else frameAnchors[rel] = (-1, shot.Lens, shot.Fwd);
            }
            frames.Add(cf);
            lastShotTime = Time.time;
            hud.SetCoach("Captured", Theme.Success);
            hud.Flash();
            Haptics.Tick();

            string path = Path.Combine(captureDir, rel);
            int quality = jpegQuality;
            bool extract = kind == Kind.Scene;
            Interlocked.Increment(ref pendingWrites);
            if (extract) Interlocked.Increment(ref pendingFeatures);
            Task.Run(() =>
            {
                byte[] thumb = null;
                int tw = 0, th = 0;
                try
                {
                    using (var jpg = ImageConversion.EncodeNativeArrayToJPG(buffer, GraphicsFormat.R8G8B8A8_SRGB,
                               (uint)w, (uint)h, 0, quality))
                        File.WriteAllBytes(path, jpg.ToArray());
                    if (extract) thumb = OrbLite.GrayThumb(buffer, w, h, out tw, out th);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[GSCapture] saving {path} failed: {e}");
                }
                finally
                {
                    buffer.Dispose();
                    Interlocked.Decrement(ref pendingWrites);
                }
                if (!extract) return;
                ExtractGate.Wait();
                try
                {
                    if (thumb != null) features[rel] = OrbLite.Extract(thumb, tw, th);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[GSCapture] features for {rel} failed: {e}");
                }
                finally
                {
                    ExtractGate.Release();
                    Interlocked.Decrement(ref pendingFeatures);
                }
            });
        }

        void AccumulatePoints()
        {
            if (pointCloudManager == null) return;
            foreach (var pc in pointCloudManager.trackables)
            {
                if (!pc.positions.HasValue) continue;
                var pos = pc.positions.Value;
                var ids = pc.identifiers;
                for (int i = 0; i < pos.Length; i++)
                {
                    Vector3 p = pos[i];
                    if (!(float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z))) continue; // ARCore emits some NaNs
                    ulong id = ids.HasValue ? ids.Value[i] : (ulong)points.Count;
                    if (points.Count >= maxPoints && !points.ContainsKey(id)) return;
                    points[id] = pc.transform.TransformPoint(p);
                }
            }
        }

        /// <summary>Shutter button: take the photo now, skipping the automatic aim / motion / blur checks. Only an
        /// unreadable camera image or lost tracking (no pose at all) stop it.</summary>
        void ManualSceneShot()
        {
            if (mode != Mode.Capturing || kind != Kind.Scene || scene == null || checking) return;
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                toast.Show("Wait for tracking, then try again", Theme.Warning);
                return;
            }
            if (pendingWrites > 2 || frames.Count >= SceneCaptureFlow.MaxFrames) return;
            FlushBestShot();
            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return;
            using (image)
            {
                var y = image.GetPlane(0);
                var q = BlurMeter.Measure(y.data, image.width, image.height, y.rowStride, y.pixelStride);
                Vector3 lens = arCamera.transform.position, fwd = arCamera.transform.forward;
                var shot = Grab(image, (float)q.Sharpness, Input.gyro.rotationRateUnbiased.magnitude);
                if (shot == null) return;
                shot.Frame.Manual = true;
                Save(shot, () => scene.CommitManual(lens, fwd));
            }
            Count("ManualShot");
        }

        // ------------------------------------------------------------------ pose integrity (scene captures)

        void OnPoseEvent(PoseEvent e)
        {
            Count("Pose" + e);
            bestShot?.Clear(); // the pending sector photo may carry a bad pose
            Haptics.Tick(e == PoseEvent.Shake ? 30 : 80);
            hud.SetCoach(PoseGuard.Coaching(e, poseGuard.NeedsVisualCheck), Theme.Warning);
        }

        /// <summary>While paused: wait for steady tracking, then confirm the position by matching the live view with
        /// earlier photos (RelocCheck) before shooting again.</summary>
        void UpdateRelocalize(float now, Vector3 lens, Vector3 fwd)
        {
            string coach = PoseGuard.Coaching(poseGuard.Reason, poseGuard.NeedsVisualCheck);
            bool offerContinue = false;
            if (poseGuard.ReadyToResume)
            {
                if (!poseGuard.NeedsVisualCheck || features.Count == 0)
                {
                    ResumeCapture(null);
                    return;
                }
                if (!relocBusy && now - lastRelocTry > 0.5f) StartRelocCheck(lens, fwd);
                bool fresh = lastRelocTime > now - 1.5f;
                if (fresh && lastReloc.Consistent)
                {
                    poseGuard.ConfirmVisually();
                    Count("PoseConfirmed");
                    ResumeCapture("Position confirmed - carry on");
                    return;
                }
                if (fresh && lastReloc.Matched)
                    coach = "The camera recognises this area but AR disagrees - keep pointing at scanned areas";
                offerContinue = poseGuard.PausedSeconds > 8f;
                if (offerContinue) coach = "Can't confirm your position - point at a scanned area, or tap Continue here (your photos are safe)";
            }
            sceneHud.ShowContinue(offerContinue);
            hud.SetCoach(coach, Theme.Warning);
        }

        void ResumeCapture(string message)
        {
            poseGuard.Resume();
            sceneHud.ShowContinue(false);
            Haptics.Tick();
            if (message != null) toast.Show(message, Theme.Success, 2.5f);
        }

        /// <summary>The position couldn't be confirmed: keep the photos so far, carry on as a new spot in the new map.
        /// Spots from the old map are hidden (their rings would be in the wrong place); the PC links both parts by
        /// matching photos, not AR poses.</summary>
        void ContinueAsNewSpot()
        {
            if (poseGuard == null || scene == null || !poseGuard.Paused) return;
            poseGuard.ContinueAsNewEpoch();
            mapEpoch++;
            scene.Planner.Epoch = mapEpoch;
            scene.LeaveStation();
            Count("PoseNewMap");
            sceneHud.ShowContinue(false);
            Haptics.Tick();
            toast.Show("Continuing as a new spot. All your photos are kept - processing joins both parts by matching " +
                       "the photos themselves, so nothing is lost.", Theme.Info, 7f);
        }

        /// <summary>Grab the live view as a small grey thumbnail (same look as the saved photos' thumbnails) and match it
        /// against earlier photos on a background thread.</summary>
        void StartRelocCheck(Vector3 lens, Vector3 fwd)
        {
            lastRelocTry = Time.time;
            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return;
            byte[] gray;
            int tw, th;
            using (image)
            {
                int f = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(image.width, image.height) / (float)OrbLite.ThumbWidth));
                tw = image.width / f;
                th = image.height / f;
                var conv = new XRCpuImage.ConversionParams(image, TextureFormat.R8, imageTransformation)
                {
                    outputDimensions = new Vector2Int(tw, th),
                };
                var buf = new NativeArray<byte>(image.GetConvertedDataSize(conv), Allocator.Temp);
                try
                {
                    image.Convert(conv, buf);
                    gray = buf.ToArray();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GSCapture] reloc image: {e.Message}");
                    return;
                }
                finally
                {
                    buf.Dispose();
                }
            }

            // earlier photos, re-expressed in today's coordinates through their spot anchors
            var cands = new List<RelocCandidate>(frames.Count);
            foreach (var fr in frames)
            {
                features.TryGetValue(fr.FilePath, out var ft);
                if (!frameAnchors.TryGetValue(fr.FilePath, out var fa)) ft = null; // no pose record: can't be compared
                Vector3 p = fa.pos, d = fa.fwd;
                if (fa.anchor >= 0 && fa.anchor < scene.Planner.Stations.Count)
                {
                    var st = scene.Planner.Stations[fa.anchor];
                    p = st.ToWorld(fa.pos);
                    d = st.AnchorRotation * fa.fwd;
                }
                cands.Add(new RelocCandidate { Features = ft, Position = p, Forward = d });
            }
            var pick = RelocCheck.PickCandidates(cands, lens, fwd);
            relocBusy = true;
            var task = Task.Run(() =>
            {
                var live = OrbLite.Extract(gray, tw, th);
                foreach (int i in RelocCheck.PickByLooks(live, cands)) if (!pick.Contains(i)) pick.Add(i);
                return RelocCheck.Run(live, lens, fwd, cands, pick);
            });
            StartCoroutine(CollectReloc(task));
        }

        /// <summary>Hand the background result over on the main thread.</summary>
        IEnumerator CollectReloc(Task<RelocResult> task)
        {
            while (!task.IsCompleted) yield return null;
            relocBusy = false;
            if (task.IsFaulted)
            {
                Debug.LogWarning($"[GSCapture] reloc check: {task.Exception?.GetBaseException().Message}");
                yield break;
            }
            lastReloc = task.Result;
            lastRelocTime = Time.time;
        }

        // ------------------------------------------------------------------ room check (scene captures)

        /// <summary>Finish on a room: check the photos while the user is still in the room (see RoomCheck).</summary>
        IEnumerator CheckRoutine()
        {
            if (mode != Mode.Capturing || checking || scene == null) yield break;
            checking = true;
            FlushBestShot();
            checkSheet.ShowChecking(frames.Count);
            while (pendingWrites > 0 || pendingFeatures > 0) yield return null;

            var photos = new List<CheckPhoto>(frames.Count);
            foreach (var f in frames)
            {
                features.TryGetValue(f.FilePath, out var ft);
                photos.Add(new CheckPhoto
                {
                    Features = ft, CameraToWorldGL = f.CameraToWorldGL, Sharpness = f.BlurScore, TargetId = f.TargetId,
                    Epoch = f.TrackingEpoch,
                    Name = f.FilePath,
                });
            }
            var coverage = scene.CheckCoverage();
            var task = Task.Run(() => new RoomCheck().Run(photos, coverage));
            while (!task.IsCompleted) yield return null;
            if (!checking) yield break; // the user went back to capturing meanwhile
            if (task.IsFaulted)
            {
                Debug.LogError($"[GSCapture] room check failed: {task.Exception}");
                checking = false;
                checkSheet.Hide();
                StartCoroutine(FinishRoutine());
                yield break;
            }

            var rep = task.Result;
            lastReport = rep;
            lastPhotos = photos;
            stats["RoomCheckMs"] = (int)rep.Millis;
            stats["RoomCheckLinked"] = rep.Linked;
            stats["RoomCheckPhotos"] = rep.Photos;
            stats["RoomCheckPartial"] = rep.Partial ? 1 : 0;
            int bad = 0;
            foreach (var i in rep.Issues) if (i.Severity == Severity.Bad) bad++;
            stats["RoomCheckBadIssues"] = bad;

            var flagged = new List<string>();
            var shown = rep.PhotosToRemove();
            if (shown.Count == 0)
                foreach (var i in rep.Issues) if (i.Severity >= Severity.Warn) shown.AddRange(i.Photos);
            foreach (int i in shown)
            {
                if (flagged.Count >= 6) break;
                flagged.Add(Path.Combine(captureDir, photos[i].Name));
            }
            checkSheet.ShowReport(rep, flagged);
        }

        /// <summary>"Fix now": delete the flagged photos, re-open their sectors and send the user to the first problem.</summary>
        void FixFromReport()
        {
            var rep = lastReport;
            checking = false;
            checkSheet.Hide();
            if (rep == null || scene == null) return;
            var remove = new HashSet<string>();
            foreach (int i in rep.PhotosToRemove()) remove.Add(lastPhotos[i].Name);
            for (int k = frames.Count - 1; k >= 0; k--)
            {
                if (!remove.Contains(frames[k].FilePath)) continue;
                scene.Planner.Unfill(frames[k].TargetId);
                features.TryRemove(frames[k].FilePath, out _);
                try { File.Delete(Path.Combine(captureDir, frames[k].FilePath)); }
                catch (Exception e) { Debug.LogWarning($"[GSCapture] delete {frames[k].FilePath}: {e.Message}"); }
                frames.RemoveAt(k);
            }
            stats["RoomCheckRemoved"] = (stats.TryGetValue("RoomCheckRemoved", out int r) ? r : 0) + remove.Count;

            CheckIssue first = null;
            foreach (var i in rep.Issues)
                if (i.Severity >= Severity.Warn) { first = i; break; }
            if (first == null) return;
            var current = scene.Planner.Current;
            if (first.Station >= 0 && (current == null || current.Index != first.Station)) scene.LeaveStation();
            string where = first.Station >= 0 ? $"Spot {first.Station + 1}: " : "";
            toast.Show(remove.Count > 0 ? $"Removed {remove.Count} photo{(remove.Count == 1 ? "" : "s")}. {where}{first.Fix}"
                                        : where + first.Fix, Theme.Warning, 9f);
        }

        // ------------------------------------------------------------------ finish / review

        IEnumerator FinishRoutine()
        {
            if (mode != Mode.Capturing) yield break;
            FlushBestShot();
            mode = Mode.Review;
            hud.SetCoach("Saving…", Theme.Info);
            while (pendingWrites > 0) yield return null;

            var pts = new List<Vector3>(points.Count);
            foreach (var p in points.Values) pts.Add(PoseConverter.ToOpenGLPoint(p));
            string pointsPath = null;
            if (pts.Count > 0)
            {
                pointsPath = "points3d.ply";
                CaptureBundleWriter.WritePointsPly(Path.Combine(captureDir, pointsPath), pts);
            }

            var device = new Dictionary<string, string>
            {
                ["model"] = SystemInfo.deviceModel,
                ["os"] = SystemInfo.operatingSystem,
                ["unity"] = Application.unityVersion,
                ["app"] = Application.version,
                ["image_transformation"] = imageTransformation.ToString(),
                ["image_roll_degrees"] = imageRollDegrees.ToString(),
            };
            string source = Application.platform == RuntimePlatform.IPhonePlayer ? "arkit" : "arcore";
            string json = kind == Kind.Scene
                ? CaptureBundleWriter.ManifestJson(captureId, source, device, frames, Vector3.zero, 0f, 0f, pointsPath, stats,
                    "scene", scene.ExtensionJson(poseGuard?.Events))
                : CaptureBundleWriter.ManifestJson(captureId, source, device, frames,
                    PoseConverter.ToOpenGLPoint(frame.ToWorld(dome.Center - Vector3.up * (dome.ObjectHeight * 0.5f))),
                    dome.ObjectRadius, dome.ObjectHeight, pointsPath, stats);
            File.WriteAllText(Path.Combine(captureDir, "manifest.json"), json);

            float coverage = kind == Kind.Scene ? scene.Coverage : dome.Coverage;
            var duration = TimeSpan.FromSeconds(Time.time - captureStartTime);
            ClearScene();
            if (session != null) session.enabled = false;
            hud.SetStep(3);
            hud.Show(false);

            var thumbs = new List<string>();
            for (int k = 0; k < 6 && frames.Count > 0; k++)
                thumbs.Add(Path.Combine(captureDir, frames[k * (frames.Count - 1) / 5].FilePath));
            long bytes = 0;
            foreach (var f in Directory.GetFiles(captureDir, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
            entries = CaptureLibrary.Scan(CaptureLibrary.Root);
            review.Show(frames.Count, coverage, duration, pts.Count, CaptureLibrary.FormatBytes(bytes), thumbs);
        }

        // ------------------------------------------------------------------ helpers

        float MinFovDeg()
        {
            if (cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr) && intr.focalLength.y > 0)
            {
                float vfov = 2f * Mathf.Atan(0.5f * intr.resolution.y / intr.focalLength.y) * Mathf.Rad2Deg;
                float hfov = 2f * Mathf.Atan(0.5f * intr.resolution.x / intr.focalLength.x) * Mathf.Rad2Deg;
                return Mathf.Min(vfov, hfov);
            }
            return arCamera.fieldOfView;
        }

        void BuildMarkers()
        {
            ClearMarkers();
            float size = Mathf.Clamp(dome.IdealDistance * 0.022f, 0.008f, 0.035f);
            for (int i = 0; i < dome.Slots.Count; i++)
            {
                var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                m.name = $"Slot{i}";
                Destroy(m.GetComponent<Collider>());
                m.transform.SetParent(boundsRoot.transform, false);
                m.transform.localPosition = dome.SlotPosition(i);
                m.transform.localScale = Vector3.one * size;
                m.GetComponent<Renderer>().material = new Material(markerMaterial);
                markers.Add(m);
            }
        }

        void UpdateMarkers(int currentSlot)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 5f);
            float baseSize = Mathf.Clamp(dome.IdealDistance * 0.022f, 0.008f, 0.035f);
            for (int i = 0; i < markers.Count; i++)
            {
                Color c;
                float scale = 1f;
                if (dome.Slots[i].Filled) c = Theme.WithAlpha(Theme.Success, 0.85f);
                else if (i == suggestedSlot)
                {
                    c = Theme.WithAlpha(Theme.Warning, 0.65f + 0.35f * pulse);
                    scale = 1.5f + 0.4f * pulse;
                }
                else if (i == currentSlot) c = new Color(1f, 1f, 1f, 0.9f);
                else c = new Color(1f, 1f, 1f, 0.22f);
                markers[i].GetComponent<Renderer>().material.color = c;
                markers[i].transform.localScale = Vector3.one * baseSize * scale;
            }
        }

        void ClearMarkers()
        {
            foreach (var m in markers) Destroy(m);
            markers.Clear();
        }

        void Count(string key)
        {
            stats.TryGetValue(key, out int n);
            stats[key] = n + 1;
        }
    }
}
