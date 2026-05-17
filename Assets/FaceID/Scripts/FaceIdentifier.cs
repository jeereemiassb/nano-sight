// Copyright (c) NanoSight.
//
// Orchestrator: wires the passthrough camera, the YuNet face detector, the IoU tracker,
// the identification server and the floating labels into one pipeline.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Heart of the FaceID feature. Every <see cref="m_detectionInterval"/> seconds it:
    ///   1. grabs the current passthrough camera frame (and caches the matching camera pose),
    ///   2. runs the on-device <see cref="YuNetFaceDetector"/> on it,
    ///   3. feeds the boxes to a lightweight IoU <see cref="FaceTracker"/> for stable ids,
    ///   4. for each NEW track, crops the face, POSTs it to the identification server and shows a label,
    ///   5. for every live track, keeps the label + box anchored to the real face in 3D.
    ///
    /// It reuses Meta's <see cref="PassthroughCameraAccess"/> for the camera feed and the 2D->3D
    /// unprojection, and <see cref="EnvironmentRaycastManager"/> for real depth; the rest is glue.
    /// </summary>
    public class FaceIdentifier : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("Passthrough Camera Access component providing the camera feed, intrinsics and pose.")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Tooltip("Manager that pools and positions the floating identity labels.")]
        [SerializeField] private FaceLabelManager m_labelManager;

        [Tooltip("(Optional) Environment Raycast Manager. When assigned, the Quest's real depth is " +
                 "used to place labels and boxes ON the face instead of estimating distance from the " +
                 "bounding-box size. Leave empty to fall back to the size-based estimate.")]
        [SerializeField] private EnvironmentRaycastManager m_environmentRaycast;

        [Header("Face detector (YuNet)")]
        [Tooltip("On-device YuNet face detector. Assign the yunet.onnx model on this foldout.")]
        [SerializeField] private YuNetFaceDetector m_faceDetector = new();

        [Header("Detection")]
        [Tooltip("Idle time between detection passes, in seconds. Lower = more responsive tracking. " +
                 "Passes never overlap regardless of this value.")]
        [SerializeField, Range(0.02f, 2f)] private float m_detectionInterval = 0.1f;

        [Tooltip("Detections smaller than this fraction of the frame (width OR height) are discarded as noise.")]
        [SerializeField, Range(0f, 0.5f)] private float m_minNormalizedBoxSize = 0.04f;

        [Tooltip("Flip detection boxes from image space (origin top-left, y down) to viewport space " +
                 "(origin bottom-left, y up). If labels end up vertically mirrored, toggle this.")]
        [SerializeField] private bool m_flipDetectionY = true;

        [Tooltip("Show a placeholder label on every detected face immediately — before, and even " +
                 "without, a server response. Lets you confirm detection works on its own.")]
        [SerializeField] private bool m_showPlaceholderWhileIdentifying = true;

        [Tooltip("Text shown on the placeholder label while waiting for the server identification.")]
        [SerializeField] private string m_placeholderText = "...";

        [Header("HUD mode")]
        [Tooltip("Head-locked layout: the box and label are parented to the eye camera and placed " +
                 "at a fixed distance using the eye's viewport mapping. This makes them feel like " +
                 "a flat HUD BUT cannot stay on the face under head motion (parallax between the " +
                 "passthrough and eye cameras drifts the box). Leave OFF for world-anchored " +
                 "placement (uses the env raycast for real face depth); the box still renders as " +
                 "an overlay thanks to ZTest=Always on its material.")]
        [SerializeField] private bool m_hudMode = false;

        [Tooltip("Distance from the eye camera at which HUD elements are placed. Closer = flatter " +
                 "HUD feel (less stereoscopic separation from the real face); too close strains " +
                 "the eyes. ~0.35 m works as a near-eye overlay.")]
        [SerializeField, Range(0.2f, 3f)] private float m_hudDistanceMeters = 0.35f;

        [Header("3D placement")]
        [Tooltip("Assumed real-world width of a human face, used to estimate its distance from bbox size.")]
        [SerializeField] private float m_averageFaceWidthMeters = 0.16f;

        [Tooltip("Distance used when the bbox-based estimate cannot be computed.")]
        [SerializeField] private float m_fallbackDistanceMeters = 1.5f;

        [Tooltip("Clamp range for the estimated face distance, in metres.")]
        [SerializeField] private float m_minFaceDistanceMeters = 0.3f;
        [SerializeField] private float m_maxFaceDistanceMeters = 6f;

        [Header("Server")]
        [Tooltip("Identification responses below this confidence are recorded but no label is shown.")]
        [SerializeField, Range(0f, 1f)] private float m_minConfidenceToShow;

        [Tooltip("Isolated HTTP client. Configure the server URL / timeout here.")]
        [SerializeField] private FaceServerClient m_serverClient = new();

        [Header("Tracking")]
        [Tooltip("Lightweight IoU tracker that keeps face ids stable between detection passes.")]
        [SerializeField] private FaceTracker m_tracker = new();

        [Header("Debug visuals")]
        [Tooltip("Draw a 3D square outline that tracks each detected face.")]
        [SerializeField] private bool m_showFaceBoxes = true;

        [Tooltip("Colour of the face-tracking corner brackets.")]
        [SerializeField] private Color m_faceBoxColor = new Color(0.31f, 0.93f, 0.78f, 1f);

        [Tooltip("Line width of the face-tracking square, in metres.")]
        [SerializeField, Range(0.001f, 0.05f)] private float m_faceBoxLineWidth = 0.006f;

        [Tooltip("Show a permanent status text in front of the camera, so you can tell the app is running.")]
        [SerializeField] private bool m_showStatusHud = true;

        [Tooltip("Log the detection->viewport->depth->world chain to the console. Use to diagnose " +
                 "mis-placed labels/boxes; spammy, so leave off in normal use.")]
        [SerializeField] private bool m_verbosePlacementLog;

        // --- Runtime state -------------------------------------------------------------------

        // Reusable CPU copy of the latest camera frame (detection input + crop source).
        private Texture2D m_frameBuffer;
        // Reusable texture used to encode face crops to JPEG.
        private Texture2D m_cropTexture;
        // Camera pose captured at the same instant as m_frameBuffer. Detection is async, so the
        // unprojection later MUST use this cached pose, not the pose at result time.
        private Pose m_capturePose;

        private bool m_started;
        private bool m_passInFlight;
        private float m_lastPassTime;

        // trackId -> cancellation source for its in-flight server request.
        private readonly Dictionary<int, CancellationTokenSource> m_pendingQueries = new();
        // trackId -> the 3D square outline drawn around that face.
        private readonly Dictionary<int, FaceBoxMarker> m_markers = new();
        // Reused every pass so box filtering doesn't allocate.
        private readonly List<Rect> m_filteredScratch = new();
        // Permanent status text floating in front of the camera.
        private TextMeshProUGUI m_hud;
        private GameObject m_hudRoot;
        // Eye camera + its transform. HUD elements are parented to the transform; the Camera
        // itself is needed to do viewport->world projection for HUD-mode placement.
        private Camera m_eyeCamera;
        private Transform m_hudParent;
        // Passthrough-camera pose at capture time, used for env-raycast (world-anchored mode).
        private Vector3 m_captureEyePos;
        private Quaternion m_captureEyeRot;

        // --- Unity lifecycle -----------------------------------------------------------------

        private void OnEnable()
        {
            m_tracker.TrackCreated += OnTrackCreated;
            m_tracker.TrackLost += OnTrackLost;
        }

        private void OnDisable()
        {
            m_tracker.TrackCreated -= OnTrackCreated;
            m_tracker.TrackLost -= OnTrackLost;
            CancelAllQueries();
        }

        private void OnDestroy()
        {
            CancelAllQueries();
            m_faceDetector?.Dispose();
            foreach (var marker in m_markers.Values)
            {
                if (marker != null)
                    Destroy(marker.gameObject);
            }
            m_markers.Clear();
            if (m_hudRoot != null) Destroy(m_hudRoot);
            if (m_frameBuffer != null) Destroy(m_frameBuffer);
            if (m_cropTexture != null) Destroy(m_cropTexture);
        }

        private IEnumerator Start()
        {
            if (m_cameraAccess == null || m_labelManager == null || m_faceDetector == null || !m_faceDetector.IsReady)
            {
                Debug.LogError($"[{nameof(FaceIdentifier)}] Missing required reference(s) — " +
                               $"cameraAccess set: {m_cameraAccess != null}, " +
                               $"labelManager set: {m_labelManager != null}, " +
                               $"YuNet model assigned: {m_faceDetector != null && m_faceDetector.IsReady}. " +
                               "Disabling component.", this);
                enabled = false;
                yield break;
            }

            // Wait until the passthrough camera is actually streaming frames.
            while (!m_cameraAccess.IsPlaying)
                yield return null;

            // Resolve the eye camera that HUD elements ride on, and tell the label manager so
            // every pooled label is configured for the chosen mode from the start.
            m_eyeCamera = Camera.main;
            m_hudParent = m_eyeCamera != null ? m_eyeCamera.transform : transform;
            if (m_hudMode)
                m_labelManager.SetHudMode(true, m_hudParent);

            if (m_showStatusHud)
                SetupHud();

            // Both EnvironmentRaycastManager._isSupported (static bool? cache) and
            // EnvironmentDepthManager._provider (static, captured subsystem in its ctor) are
            // built on first access. If that happens during scene-load (race vs OpenXR subsystem
            // init), the provider captures a null occlusion subsystem and IsSupported is stuck
            // false forever. Wait until subsystems are settled, reset BOTH caches via reflection,
            // and re-toggle the manager so OnEnable->SetProviderEnabled runs with a fresh state.
            if (m_environmentRaycast != null)
            {
                yield return new WaitForSeconds(1.5f);

                const System.Reflection.BindingFlags PrivStatic =
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

                typeof(EnvironmentRaycastManager).GetField("_isSupported", PrivStatic)?.SetValue(null, null);

                var depthMgrType = System.Type.GetType("Meta.XR.EnvironmentDepth.EnvironmentDepthManager, Meta.XR.EnvironmentDepth");
                depthMgrType?.GetField("_provider", PrivStatic)?.SetValue(null, null);

                if (m_environmentRaycast.enabled)
                {
                    m_environmentRaycast.enabled = false;
                    m_environmentRaycast.enabled = true;
                }

                Debug.Log($"[{nameof(FaceIdentifier)}] Reset env-depth caches. IsSupported now = " +
                          $"{EnvironmentRaycastManager.IsSupported}");
            }

            m_started = true;
            Debug.Log($"[{nameof(FaceIdentifier)}] Camera streaming — face identification active.");
        }

        private void Update()
        {
            if (!m_started || m_passInFlight)
                return;
            if (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
                return;
            if (Time.time - m_lastPassTime < m_detectionInterval)
                return;

            RunDetectionPass();
        }

        // --- Detection pass ------------------------------------------------------------------

        /// <summary>
        /// One full detection pass. Fire-and-forget from <see cref="Update"/>; the
        /// <see cref="m_passInFlight"/> guard makes sure passes never overlap.
        /// </summary>
        private async void RunDetectionPass()
        {
            m_passInFlight = true;
            m_lastPassTime = Time.time;

            try
            {
                // 1. Snapshot the camera frame + the pose that matches it.
                if (!CaptureFrame())
                    return;

                // 2. Run the on-device YuNet detector. Inference is GPU-scheduled and the result
                //    is awaited via async readback, so this does not stall the main thread.
                var rawBoxes = await m_faceDetector.Detect(m_frameBuffer);

                // The component may have been disabled/destroyed while we awaited.
                if (this == null || !isActiveAndEnabled)
                    return;

                // 3. Drop noise-sized boxes, then update the tracker (this raises TrackCreated/TrackLost).
                var boxes = FilterBoxes(rawBoxes);
                m_tracker.Update(boxes, Time.time);

                // 4. Keep every live label + face box anchored to its real face.
                foreach (var track in m_tracker.Tracks)
                {
                    var labelPos = GetLabelTargetPosition(track.Box);
                    m_labelManager.UpdateLabelTarget(track.Id, labelPos);

                    if (m_showFaceBoxes)
                        UpdateMarker(track);
                }

                if (m_hud != null)
                {
                    var n = m_tracker.Tracks.Count;
                    m_hud.text = n == 0 ? "BUSCANDO…" : (n == 1 ? "RECONOCIENDO" : $"RECONOCIENDO · {n}");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                m_passInFlight = false;
            }
        }

        /// <summary>
        /// Copies the latest camera image into <see cref="m_frameBuffer"/> and caches the matching
        /// camera pose. Mirrors the snapshot approach used by Meta's CameraToWorld sample.
        /// </summary>
        private bool CaptureFrame()
        {
            if (!m_cameraAccess.IsPlaying)
                return false;

            var resolution = m_cameraAccess.CurrentResolution;
            if (resolution.x <= 0 || resolution.y <= 0)
                return false;

            if (m_frameBuffer == null || m_frameBuffer.width != resolution.x || m_frameBuffer.height != resolution.y)
            {
                if (m_frameBuffer != null)
                    Destroy(m_frameBuffer);
                m_frameBuffer = new Texture2D(resolution.x, resolution.y, TextureFormat.RGBA32, false);
            }

            var pixels = m_cameraAccess.GetColors();
            if (!pixels.IsCreated || pixels.Length == 0)
                return false;

            m_frameBuffer.LoadRawTextureData(pixels);
            m_frameBuffer.Apply(false);

            // Cache the pose now: detection runs async and the headset will have moved by the
            // time the result arrives, so all unprojection must use this snapshot pose.
            m_capturePose = m_cameraAccess.GetCameraPose();
            if (m_hudParent != null)
            {
                m_captureEyePos = m_hudParent.position;
                m_captureEyeRot = m_hudParent.rotation;
            }
            return true;
        }

        /// <summary>Removes detections that are too small to be real faces.</summary>
        private List<Rect> FilterBoxes(IReadOnlyList<Rect> rawBoxes)
        {
            m_filteredScratch.Clear();
            for (var i = 0; i < rawBoxes.Count; i++)
            {
                var box = rawBoxes[i];
                if (box.width >= m_minNormalizedBoxSize && box.height >= m_minNormalizedBoxSize)
                    m_filteredScratch.Add(box);
            }
            return m_filteredScratch;
        }

        // --- Server identification (per new track) -------------------------------------------

        /// <summary>
        /// A brand-new face appeared: crop it, POST it to the server and show a label with the
        /// returned identity. Cancellable through <see cref="m_pendingQueries"/> if the face is
        /// lost before the server answers.
        /// </summary>
        private async void OnTrackCreated(TrackedFace track)
        {
            if (track.IdentificationRequested)
                return;
            track.IdentificationRequested = true;

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            m_pendingQueries[track.Id] = cts;

            try
            {
                // Show a placeholder label right away, so detection is visually confirmed even
                // before — or entirely without — a server response. It is replaced with the real
                // identity once the server answers (ShowLabel de-duplicates by track id).
                if (m_showPlaceholderWhileIdentifying)
                    m_labelManager.ShowLabel(track.Id, m_placeholderText, 0f, GetLabelTargetPosition(track.Box));

                var jpeg = CropFaceJpeg(track.Box);
                if (jpeg == null)
                {
                    // Could not crop — allow another attempt next time this face appears.
                    track.IdentificationRequested = false;
                    return;
                }

                var result = await m_serverClient.IdentifyAsync(jpeg, token);

                if (token.IsCancellationRequested || this == null)
                    return;

                if (!result.Success)
                {
                    Debug.LogWarning($"[{nameof(FaceIdentifier)}] Identification failed for track " +
                                     $"{track.Id}: {result.Error}");
                    return;
                }

                track.Name = result.Name;
                track.Confidence = result.Confidence;

                if (result.Confidence < m_minConfidenceToShow)
                    return;

                // The face may have been lost while we waited for the server response.
                if (!IsTrackAlive(track.Id))
                    return;

                var labelPos = GetLabelTargetPosition(track.Box);
                m_labelManager.ShowLabel(track.Id, result.Name, result.Confidence, labelPos);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                // Only clear the dictionary slot if it still points at THIS request.
                if (m_pendingQueries.TryGetValue(track.Id, out var stored) && stored == cts)
                    m_pendingQueries.Remove(track.Id);
                cts.Dispose();
            }
        }

        /// <summary>A tracked face was lost: cancel any pending request and drop its label.</summary>
        private void OnTrackLost(TrackedFace track)
        {
            if (m_pendingQueries.TryGetValue(track.Id, out var cts))
            {
                cts.Cancel();
                m_pendingQueries.Remove(track.Id);
                // Disposal is handled by the request's own finally block.
            }

            if (m_labelManager != null)
                m_labelManager.RemoveLabel(track.Id);

            RemoveMarker(track.Id);
        }

        private bool IsTrackAlive(int trackId)
        {
            var tracks = m_tracker.Tracks;
            for (var i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Id == trackId)
                    return true;
            }
            return false;
        }

        private void CancelAllQueries()
        {
            foreach (var cts in m_pendingQueries.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            m_pendingQueries.Clear();
        }

        // --- Debug visuals: status HUD + per-face tracking square ----------------------------

        /// <summary>
        /// Builds a small floating "status chip" — dark translucent rounded pill anchored to the
        /// top-left of the eye view, with a cyan status dot and a TMP label. Same look as the
        /// "RECONOCIENDO…" chip in the design mock.
        /// </summary>
        private void SetupHud()
        {
            var parent = Camera.main != null ? Camera.main.transform : transform;

            // Canvas root: world-space, head-locked, positioned ahead and up-left of the eye.
            m_hudRoot = new GameObject("FaceID HUD");
            m_hudRoot.transform.SetParent(parent, false);
            m_hudRoot.transform.localPosition = new Vector3(-0.28f, 0.16f, 0.8f);
            m_hudRoot.transform.localRotation = Quaternion.identity;
            m_hudRoot.transform.localScale = Vector3.one * 0.001f;  // 1 unit = 1 mm

            var canvas = m_hudRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;
            m_hudRoot.AddComponent<CanvasScaler>();
            m_hudRoot.AddComponent<GraphicRaycaster>();

            var canvasRect = m_hudRoot.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(280f, 60f);  // mm

            // Rounded dark background using Unity's built-in UISprite (9-sliced rounded rect).
            var bg = new GameObject("Background", typeof(RectTransform));
            bg.transform.SetParent(m_hudRoot.transform, false);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bg.AddComponent<Image>();
            bgImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            bgImg.type = Image.Type.Sliced;
            bgImg.color = new Color(0.06f, 0.08f, 0.10f, 0.78f);

            // Cyan status dot on the left.
            var dot = new GameObject("Dot", typeof(RectTransform));
            dot.transform.SetParent(m_hudRoot.transform, false);
            var dotRect = dot.GetComponent<RectTransform>();
            dotRect.anchorMin = new Vector2(0f, 0.5f);
            dotRect.anchorMax = new Vector2(0f, 0.5f);
            dotRect.pivot = new Vector2(0f, 0.5f);
            dotRect.anchoredPosition = new Vector2(18f, 0f);
            dotRect.sizeDelta = new Vector2(16f, 16f);
            var dotImg = dot.AddComponent<Image>();
            dotImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            dotImg.color = m_faceBoxColor;

            // Label text.
            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(m_hudRoot.transform, false);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(44f, 0f);
            labelRect.offsetMax = new Vector2(-18f, 0f);
            m_hud = label.AddComponent<TextMeshProUGUI>();
            m_hud.fontSize = 22f;
            m_hud.fontStyle = FontStyles.Bold;
            m_hud.alignment = TextAlignmentOptions.MidlineLeft;
            m_hud.color = Color.white;
            m_hud.text = "BUSCANDO…";
        }

        /// <summary>Creates (if needed) and repositions the 3D square that hugs a tracked face.</summary>
        private void UpdateMarker(TrackedFace track)
        {
            if (!m_markers.TryGetValue(track.Id, out var marker) || marker == null)
            {
                // In HUD mode the marker is parented to the eye camera and renders in LOCAL space.
                var parent = m_hudMode ? m_hudParent : null;
                marker = FaceBoxMarker.Create($"FaceBox {track.Id}", m_faceBoxColor, m_faceBoxLineWidth, parent);
                m_markers[track.Id] = marker;
            }

            var viewportBox = ToViewportRect(track.Box);

            if (m_hudMode)
            {
                // Head-locked: corners in eye-local space at a fixed distance. Depth is irrelevant
                // — any positive distance projects to the same eye viewport, so the box overlays
                // the detected face exactly, no env-raycast or size estimate needed.
                var d = m_hudDistanceMeters;
                marker.SetCorners(
                    CornerToEyeLocal(viewportBox.xMin, viewportBox.yMin, d),
                    CornerToEyeLocal(viewportBox.xMin, viewportBox.yMax, d),
                    CornerToEyeLocal(viewportBox.xMax, viewportBox.yMax, d),
                    CornerToEyeLocal(viewportBox.xMax, viewportBox.yMin, d));
                return;
            }

            var centerRay = m_cameraAccess.ViewportPointToRay(viewportBox.center, m_capturePose);
            var distance = GetFaceDistance(centerRay, viewportBox);
            marker.SetCorners(
                CornerToWorld(viewportBox.xMin, viewportBox.yMin, distance),
                CornerToWorld(viewportBox.xMin, viewportBox.yMax, distance),
                CornerToWorld(viewportBox.xMax, viewportBox.yMax, distance),
                CornerToWorld(viewportBox.xMax, viewportBox.yMin, distance));
        }

        /// <summary>Unprojects a viewport-space corner to a world point at the given distance.</summary>
        private Vector3 CornerToWorld(float u, float v, float distance)
        {
            var ray = m_cameraAccess.ViewportPointToRay(new Vector2(u, v), m_capturePose);
            return ray.origin + ray.direction.normalized * distance;
        }

        /// <summary>
        /// Unprojects a viewport-space corner into the EYE camera's LOCAL space at the given
        /// distance, using the eye camera's frustum directly (not the passthrough's). The result
        /// is pose-invariant: it's just where viewport (u, v) lands at distance d for the eye's
        /// FoV, which is exactly what the user sees on screen. Used by HUD mode so the marker
        /// can be parented to the eye and stay head-locked.
        /// </summary>
        private Vector3 CornerToEyeLocal(float u, float v, float distance)
        {
            if (m_eyeCamera == null)
                return new Vector3(0f, 0f, distance);
            var worldPoint = m_eyeCamera.ViewportToWorldPoint(new Vector3(u, v, distance));
            return m_eyeCamera.transform.InverseTransformPoint(worldPoint);
        }

        /// <summary>
        /// Position to feed a label's <see cref="FaceLabel.SetTarget"/>: a WORLD point in
        /// 3D-placement mode, or an eye-LOCAL point in HUD mode (FaceLabel interprets it
        /// according to its own HUD-mode flag).
        /// </summary>
        private Vector3 GetLabelTargetPosition(Rect imageBox)
        {
            if (m_hudMode)
            {
                var vp = ToViewportRect(imageBox);
                return CornerToEyeLocal(vp.center.x, vp.center.y, m_hudDistanceMeters);
            }
            return EstimateWorldPosition(imageBox);
        }

        private void RemoveMarker(int trackId)
        {
            if (!m_markers.TryGetValue(trackId, out var marker))
                return;

            m_markers.Remove(trackId);
            if (marker != null)
                Destroy(marker.gameObject);
        }

        // --- Geometry: detection box -> 3D world position ------------------------------------

        /// <summary>
        /// Converts a detection box from image space (origin top-left, y down — RetinaFace
        /// convention) to viewport space (origin bottom-left, y up — what
        /// <see cref="PassthroughCameraAccess"/> and Unity textures expect).
        /// </summary>
        private Rect ToViewportRect(Rect imageBox)
        {
            if (!m_flipDetectionY)
                return imageBox;

            var flippedY = 1f - imageBox.yMax;
            return new Rect(imageBox.xMin, flippedY, imageBox.width, imageBox.height);
        }

        /// <summary>
        /// Unprojects the centre of a detection box into a world-space position, using the
        /// camera intrinsics + the pose cached at capture time. Distance is estimated from the
        /// apparent face width so labels sit roughly on top of the real face.
        /// </summary>
        private Vector3 EstimateWorldPosition(Rect imageBox)
        {
            var viewportBox = ToViewportRect(imageBox);
            var centerRay = m_cameraAccess.ViewportPointToRay(viewportBox.center, m_capturePose);
            var distance = GetFaceDistance(centerRay, viewportBox);
            return centerRay.origin + centerRay.direction.normalized * distance;
        }

        /// <summary>
        /// Distance from the camera to the face. Prefers the Quest's real environment depth
        /// (an <see cref="EnvironmentRaycastManager"/> raycast against the live depth map, which
        /// includes people); falls back to estimating it from the apparent face size when no
        /// raycast manager is wired or the ray misses.
        /// </summary>
        private float GetFaceDistance(Ray centerRay, Rect viewportBox)
        {
            var raycastSupported = m_environmentRaycast != null && EnvironmentRaycastManager.IsSupported;
            if (raycastSupported && m_environmentRaycast.Raycast(centerRay, out var hit))
            {
                var depthDistance = Vector3.Distance(centerRay.origin, hit.point);
                if (m_verbosePlacementLog)
                    Debug.Log($"[{nameof(FaceIdentifier)}] vp={viewportBox} ray.o={centerRay.origin} " +
                              $"ray.dir={centerRay.direction} -> ENV DEPTH hit @ {hit.point}, dist={depthDistance:F2}m");
                return depthDistance;
            }

            var estimated = EstimateDistance(viewportBox);
            if (m_verbosePlacementLog)
                Debug.Log($"[{nameof(FaceIdentifier)}] vp={viewportBox} ray.o={centerRay.origin} " +
                          $"ray.dir={centerRay.direction} -> NO env hit (raycastSupported={raycastSupported}), " +
                          $"size-estimate dist={estimated:F2}m");
            return estimated;
        }

        /// <summary>
        /// Estimates how far a face is by comparing the angular width of its bounding box
        /// (two rays through its left and right edges) against an assumed real face width.
        /// </summary>
        private float EstimateDistance(Rect viewportBox)
        {
            var leftRay = m_cameraAccess.ViewportPointToRay(
                new Vector2(viewportBox.xMin, viewportBox.center.y), m_capturePose);
            var rightRay = m_cameraAccess.ViewportPointToRay(
                new Vector2(viewportBox.xMax, viewportBox.center.y), m_capturePose);

            var angleDegrees = Vector3.Angle(leftRay.direction, rightRay.direction);
            if (angleDegrees < 0.01f)
                return m_fallbackDistanceMeters;

            // half_width / tan(half_angle) = distance to the face plane.
            var distance = (m_averageFaceWidthMeters * 0.5f) /
                           Mathf.Tan(angleDegrees * 0.5f * Mathf.Deg2Rad);
            return Mathf.Clamp(distance, m_minFaceDistanceMeters, m_maxFaceDistanceMeters);
        }

        // --- Cropping ------------------------------------------------------------------------

        /// <summary>
        /// Crops the region of <see cref="m_frameBuffer"/> covered by <paramref name="imageBox"/>
        /// and encodes it as JPEG. Returns null if the crop is degenerate.
        /// </summary>
        private byte[] CropFaceJpeg(Rect imageBox)
        {
            if (m_frameBuffer == null)
                return null;

            // Texture2D pixels are y-up (bottom-left origin), same as viewport space.
            var viewportBox = ToViewportRect(imageBox);
            var width = m_frameBuffer.width;
            var height = m_frameBuffer.height;

            var px = Mathf.Clamp(Mathf.RoundToInt(viewportBox.xMin * width), 0, width - 1);
            var py = Mathf.Clamp(Mathf.RoundToInt(viewportBox.yMin * height), 0, height - 1);
            var pw = Mathf.Clamp(Mathf.RoundToInt(viewportBox.width * width), 1, width - px);
            var ph = Mathf.Clamp(Mathf.RoundToInt(viewportBox.height * height), 1, height - py);

            var pixels = m_frameBuffer.GetPixels(px, py, pw, ph);

            if (m_cropTexture == null || m_cropTexture.width != pw || m_cropTexture.height != ph)
            {
                if (m_cropTexture != null)
                    Destroy(m_cropTexture);
                m_cropTexture = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
            }

            m_cropTexture.SetPixels(pixels);
            m_cropTexture.Apply(false);
            return m_cropTexture.EncodeToJPG();
        }
    }
}
