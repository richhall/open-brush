// Copyright 2026 The Open Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using UnityEngine;

namespace TiltBrush
{
    // Drives the post-production live preview on the camera already painting the
    // desktop window (POSTPROD_PREVIEW_PLAN.md, stage 6) - not a new in-app panel,
    // per the brief. Reuses App.VrSdk.GetVrCamera(), confirmed to be the camera
    // already painting the window in NoHeadsetMode - not the Spectator Camera
    // (DropCamWidget), which is a placeable 3D prop, not a "what does the window
    // show" switch (ruled out during design).
    // Runs before other scripts each frame so the sketch clock it holds or drives (see
    // UpdateTransport) is already set when stroke playback reads it.
    [DefaultExecutionOrder(-10000)]
    public class PostProductionPreviewController : MonoBehaviour
    {
        private static PostProductionPreviewController m_Instance;
        public static PostProductionPreviewController Instance
        {
            get
            {
                if (m_Instance == null)
                {
                    var go = new GameObject("PostProductionPreviewController");
                    Object.DontDestroyOnLoad(go);
                    m_Instance = go.AddComponent<PostProductionPreviewController>();
                }
                return m_Instance;
            }
        }

        public bool Enabled { get; private set; }

        // Which camera to drive, by exact GameObject name. Empty (the default)
        // means App.VrSdk.GetVrCamera(). Settable via postprod.preview.camera,
        // since which camera actually paints the window turned out NOT to be a
        // safe assumption to hardcode - this session found a second camera
        // ("CenterEyeAnchor", an Oculus rig camera left active in Monoscopic mode)
        // rendering on top of App.VrSdk.GetVrCamera() and obscuring it entirely.
        // Name is the sensible identifier here rather than an index into
        // Camera.allCameras: that array's order isn't guaranteed stable across
        // frames, whereas a camera's name is - and postprod.debug.dumpcameras /
        // query.postprod.debug.allcameras already reports names directly, so
        // there's nothing an index would add.
        private static string s_TargetCameraName = "";

        // The calibrated extrinsics (real-camera position/rotation), if set via
        // postprod.preview.pose. Applied the same way app.snapshot.quat positions
        // the separate capture rig - App.Scene.AsScene[transform] = TrTransform.TR
        // (position, rotation) - NOT a raw world-space Transform assignment, since
        // the calibrated pose is defined in scene space and a raw assignment would
        // be wrong if the scene has any colocation offset from world origin.
        private static bool s_HasTargetPose;
        private static Vector3 s_TargetPosition;
        private static Quaternion s_TargetRotation;

        // Stage 2 auto-plate-sync (see POSTPROD_PREVIEW_PLAN.md): when set, the plate
        // is kept in sync with the current sketch time automatically every frame,
        // using PostProductionSettings.SketchTimeToC006Frame - no more manually
        // computing and setting postprod.preview.plate by hand for every scrub. Empty
        // disables it (the manual postprod.preview.plate path keeps working either
        // way). Deliberately only the plate (color+depth file paths, cheap to swap) -
        // the CG capture stays an explicit postprod.preview.refreshcg call, since a
        // snapshot costs ~1.4s and doing that every frame would be wasteful (see the
        // plan's "Known issues to revisit" on why CG capture is on-demand).
        private static string s_AutoPlateClipDir = "";
        private static int s_LastAutoPlateFrame = int.MinValue;
        public static string AutoPlateClipDir => s_AutoPlateClipDir;
        public static string LastAutoPlateStatus { get; private set; } = "auto-plate sync not enabled";

        // Plain-data status, safe to read from the HTTP listener thread (see
        // query.postprod.preview.status in ApiManager.cs).
        public static string LastStatus { get; private set; } = "not started";

        public static void SetTargetCameraName(string name)
        {
            s_TargetCameraName = name ?? "";
        }

        // Raw getters for stage 8 persistence.
        public static string TargetCameraName => s_TargetCameraName;
        public static bool HasTargetPose => s_HasTargetPose;
        public static Vector3 TargetPosition => s_TargetPosition;
        public static Quaternion TargetRotation => s_TargetRotation;

        public static void SetTargetPose(Vector3 position, Quaternion rotation)
        {
            s_TargetPosition = position;
            s_TargetRotation = rotation;
            s_HasTargetPose = true;
        }

        // clipDir must contain color_frames/ and depth_frames/ subfolders following
        // the established true-frame-number naming convention (frame_NNNNN_color.png /
        // frame_NNNNN_depth.exr - see DepthFrameResolver). Empty string disables sync.
        public static void SetAutoPlateClipDir(string clipDir)
        {
            s_AutoPlateClipDir = clipDir ?? "";
            s_LastAutoPlateFrame = int.MinValue; // force a resync on the next Update()
            LastAutoPlateStatus = string.IsNullOrEmpty(s_AutoPlateClipDir)
                ? "auto-plate sync disabled"
                : $"auto-plate sync enabled on {s_AutoPlateClipDir}";
        }

        // Keeps the plate in sync with the current sketch time. Only does real work
        // (file checks, SetPlate) when the mapped frame actually changes, since this
        // runs every frame while enabled.
        private void UpdateAutoPlate()
        {
            if (string.IsNullOrEmpty(s_AutoPlateClipDir)) { return; }

            double sketchTimeMs = App.Instance.CurrentSketchTime * 1000.0;
            if (sketchTimeMs >= SketchTimeSentinelThresholdMs) { return; }
            int requestedFrame = PostProductionSettings.SketchTimeToC006Frame(sketchTimeMs);

            // Fast plates: the cache serves the frame itself (no per-change file loads).
            if (PostProductionPlateCache.Enabled)
            {
                PostProductionPlateCache.SetPlayhead(requestedFrame);
                LastAutoPlateStatus = $"sketchTime={sketchTimeMs:F0}ms -> c006Frame={requestedFrame} (fast plates)";
                s_LastAutoPlateFrame = int.MinValue; // full-res path resyncs when fast plates turn off
                return;
            }
            if (requestedFrame == s_LastAutoPlateFrame) { return; }
            s_LastAutoPlateFrame = requestedFrame;

            string depthFramesDir = System.IO.Path.Combine(s_AutoPlateClipDir, "depth_frames");
            string colorFramesDir = System.IO.Path.Combine(s_AutoPlateClipDir, "color_frames");
            try
            {
                bool inRange = DepthFrameResolver.TryResolve(depthFramesDir, requestedFrame, out int resolvedFrame);
                string depthPath = DepthFrameResolver.DepthPath(depthFramesDir, resolvedFrame);
                string colorPath = DepthFrameResolver.ColorPath(colorFramesDir, resolvedFrame);
                SetPlate(colorPath, depthPath);
                LastAutoPlateStatus = inRange
                    ? $"sketchTime={sketchTimeMs:F0}ms -> c006Frame={requestedFrame} (resolved {resolvedFrame})"
                    : $"sketchTime={sketchTimeMs:F0}ms -> c006Frame={requestedFrame} OUT OF RANGE, " +
                        $"clamped to nearest {resolvedFrame}";
            }
            catch (System.Exception e)
            {
                LastAutoPlateStatus = $"sketchTime={sketchTimeMs:F0}ms -> c006Frame={requestedFrame} ERROR: {e.Message}";
            }
        }

        private static Camera FindTargetCamera()
        {
            if (string.IsNullOrEmpty(s_TargetCameraName))
            {
                return App.VrSdk.GetVrCamera();
            }
            foreach (var c in Camera.allCameras)
            {
                if (c.name == s_TargetCameraName) { return c; }
            }
            return null;
        }

        // Camera pose (scene space) from just before Preview turned on, so Preview Off
        // can put it back - otherwise only the projection reset and the camera stayed
        // stuck at the calibrated pose.
        private bool m_HasPrePreviewPose;
        private TrTransform m_PrePreviewPose;

        public void SetEnabled(bool enabled)
        {
            bool wasEnabled = Enabled;
            Enabled = enabled;
            Camera cam = FindTargetCamera();
            if (cam == null)
            {
                LastStatus = $"SetEnabled: target camera not found (name=\"{s_TargetCameraName}\", " +
                    "empty means App.VrSdk.GetVrCamera())";
                return;
            }

            if (enabled)
            {
                if (!wasEnabled && s_HasTargetPose)
                {
                    m_PrePreviewPose = App.Scene.AsScene[cam.transform];
                    m_HasPrePreviewPose = true;
                }
                ScreenshotManager.ApplyCalibratedProjectionToCamera(cam);
                ApplyTargetPose(cam);
                if (cam.GetComponent<PostProductionCompositeImageEffect>() == null)
                {
                    cam.gameObject.AddComponent<PostProductionCompositeImageEffect>();
                }
            }
            else
            {
                ScreenshotManager.ClearCalibratedProjectionFromCamera(cam);
                if (wasEnabled && m_HasPrePreviewPose)
                {
                    App.Scene.AsScene[cam.transform] = m_PrePreviewPose;
                    m_HasPrePreviewPose = false;
                }
                var fx = cam.GetComponent<PostProductionCompositeImageEffect>();
                if (fx != null) { Destroy(fx); }
            }
            ReportStatus(cam, "SetEnabled");
        }

        // Cached every frame regardless of Enabled, so query.sketch.currenttime works
        // for basic scrub testing even before postprod.preview is turned on -
        // App.Instance.CurrentSketchTime reads Unity's Time API, so it can only be
        // read here on the main thread, not directly from the HTTP listener thread.
        private static double s_LastSketchTimeMs;
        public static double LastSketchTimeMs => s_LastSketchTimeMs;

        // App.Instance.CurrentSketchTime is set to float.MaxValue by
        // SketchMemoryScript/ScenePlaybackByTime as an internal "fully loaded, past
        // the last stroke" sentinel (seen transiently during save/load) - never meant
        // to be user-facing. Reporting it raw through query.sketch.currenttime broke
        // a client UI (a scrub slider forced to ~3.4e+41 = float.MaxValue*1000 right
        // after a save). Clamp it out here rather than trusting every future caller
        // to know about this sentinel.
        private const double SketchTimeSentinelThresholdMs = float.MaxValue / 2.0;

        // Transport: holds (rate 0) or plays (any other rate, negative = reverse) the sketch
        // clock, driven here each frame rather than by the control page, so playback never
        // depends on HTTP round trips. Only the sketch clock is controlled: Unity's game
        // clock keeps running, because freezing it (Time.timeScale = 0) also froze Open
        // Brush's lighting fade-in after a load and left strokes grey (found 2026-10-21).
        //
        // The sketch clock is Time.timeSinceLevelLoad minus a base, so it advances by
        // Time.deltaTime each frame on its own; that advance is cancelled here and replaced
        // with rate x real time. A seek made by anything else (slider, API) shows up as the
        // clock not being where it was left, and is adopted rather than overridden. This
        // runs first each frame (DefaultExecutionOrder) so stroke playback sees the result.
        private enum Transport { Off, Hold, Shuttle }
        private static Transport s_Transport = Transport.Off;
        private static float s_ShuttleRate;
        private static double s_LastSetSketchTime;
        private static bool s_HaveLastSet;
        public static float ShuttleRate => s_Transport == Transport.Shuttle ? s_ShuttleRate : 0f;

        public static void SetShuttleRate(float rate)
        {
            s_Transport = rate == 0f ? Transport.Hold : Transport.Shuttle;
            s_ShuttleRate = rate;
            s_HaveLastSet = false;
            Time.timeScale = 1f;
        }

        private static void UpdateTransport()
        {
            if (s_Transport == Transport.Off || !SketchMemoryScript.m_Instance.IsPlayingBack) { return; }
            double current = App.Instance.CurrentSketchTime;
            double from;
            if (!s_HaveLastSet)
            {
                from = current;
            }
            else
            {
                double expected = s_LastSetSketchTime + Time.deltaTime;
                from = System.Math.Abs(current - expected) < 1e-4
                    ? s_LastSetSketchTime
                    : current - Time.deltaTime; // seeked since last frame: adopt the seek
            }
            double next = s_Transport == Transport.Shuttle
                ? from + s_ShuttleRate * Time.unscaledDeltaTime
                : from;
            next = System.Math.Max(0.0, next);
            App.Instance.CurrentSketchTime = next;
            s_LastSetSketchTime = next;
            s_HaveLastSet = true;
        }

        // TEMP profiling: frame-time stats over the last ~2 seconds, read via
        // query.postprod.debug.timing, so performance can be measured remotely while
        // features are switched on and off.
        private const int kFrameSamples = 120;
        private static readonly float[] s_FrameTimes = new float[kFrameSamples];
        private static int s_FrameIndex;
        public static volatile string FrameStats = "no frames yet";
        private static int s_LastGcCount;

        private static void RecordFrameTime()
        {
            s_FrameTimes[s_FrameIndex % kFrameSamples] = Time.unscaledDeltaTime;
            s_FrameIndex++;
            if (s_FrameIndex % 30 != 0) { return; }
            int n = Mathf.Min(s_FrameIndex, kFrameSamples);
            float sum = 0f, max = 0f;
            for (int i = 0; i < n; i++) { sum += s_FrameTimes[i]; max = Mathf.Max(max, s_FrameTimes[i]); }
            float avg = sum / n;
            // TEMP (stutter diagnosis 2026-10-23): garbage collections and the slowest fast
            // plate decode/upload in the last 30 frames.
            int gcCount = System.GC.CollectionCount(0);
            int gcs = gcCount - s_LastGcCount;
            s_LastGcCount = gcCount;
            FrameStats = $"frames: avg {avg * 1000f:F1}ms ({1f / avg:F0} fps), worst {max * 1000f:F1}ms, over last {n} frames" +
                $" | last 30 frames: {gcs} GC, heap {System.GC.GetTotalMemory(false) / (1024 * 1024)}MB, " +
                $"plate upload max {PostProductionPlateCache.TakeMaxMainThreadMs():F1}ms";
        }

        private void Update()
        {
            RecordFrameTime();
            UpdateTransport();

            double rawSketchTimeMs = App.Instance.CurrentSketchTime * 1000.0;
            s_LastSketchTimeMs = rawSketchTimeMs >= SketchTimeSentinelThresholdMs ? 0.0 : rawSketchTimeMs;
            // Keeps the alignment readout current after loads, resets or direct sets.
            if (Time.frameCount % 15 == 0)
            {
                PostProductionAlignment.RefreshReadouts();
                RefreshLayerVisibility();
            }
            if (!Enabled) { return; }
            Camera cam = FindTargetCamera();
            if (cam == null)
            {
                LastStatus = $"Update: target camera not found (name=\"{s_TargetCameraName}\")";
                return;
            }

            // Reapplied every frame rather than once: setting fieldOfView (which
            // other, unrelated camera code may do) can invalidate an
            // explicitly-assigned projection matrix, so this guards against drift
            // instead of trusting a one-off assignment to stick. Same reasoning
            // applies to the pose - reapplied every frame rather than trusting a
            // one-off assignment, in case anything else (XR Origin tracking sync,
            // etc.) drives this camera's transform independently.
            ScreenshotManager.ApplyCalibratedProjectionToCamera(cam);
            ApplyTargetPose(cam);
            UpdateAutoPlate();
            ReportStatus(cam, "Update");
        }

        // Live CG renders after every Update (stroke playback included) has run this
        // frame, so it reflects the current sketch time.
        // "1,1,0,1": each layer's visibility, for query.postprod.layers.visible (the HTTP
        // thread can't read canvases itself).
        public static string LayerVisibility { get; private set; } = "";

        private static void RefreshLayerVisibility()
        {
            if (App.Scene == null) { return; }
            var flags = new System.Text.StringBuilder();
            foreach (var canvas in App.Scene.LayerCanvases)
            {
                if (flags.Length > 0) { flags.Append(','); }
                flags.Append(canvas.gameObject.activeSelf ? '1' : '0');
            }
            LayerVisibility = flags.ToString();
        }

        private void LateUpdate()
        {
            if (!Enabled || !PostProductionLiveCg.Enabled) { return; }
            Camera cam = FindTargetCamera();
            if (cam == null) { return; }
            PostProductionLiveCg.Render(cam, s_HasTargetPose, s_TargetPosition, s_TargetRotation);
        }

        private void ApplyTargetPose(Camera cam)
        {
            if (!s_HasTargetPose) { return; }
            App.Scene.AsScene[cam.transform] = TrTransform.TR(s_TargetPosition, s_TargetRotation);
        }

        // "Live" CG capture for the preview (see the plan's "Known issues to
        // revisit" for why this is a snapshot-on-demand rather than a true
        // per-frame GPU capture): reuses the existing, already-correct
        // ScreenshotManager.TakeSnapshot (same mechanism app.snapshot.quat already
        // uses - background-sentinel detection and culling mask handling are both
        // already solved there, not reimplemented here) against our stored
        // calibrated pose, to a fixed filename so callers always know where to
        // find the latest capture rather than tracking a changing name.
        // Incremented after every finished capture, so a client can wait for the
        // previous one to complete before queueing the next (the command queue is
        // FIFO, so a flood of captures would otherwise delay every later command -
        // including Pause - until the whole backlog has run).
        public static volatile int CgCaptureCount;

        private const string CgCaptureBaseName = "postprod_live_cg";
        // Explicit extension is load-bearing, not decorative: a filename with no
        // extension silently saves as .jpg instead (a documented gotcha in this
        // codebase's own API - re-hit twice already this session by omitting it).
        private const string CgCaptureFilename = CgCaptureBaseName + ".png";

        public static string CgCaptureColorPath =>
            System.IO.Path.Combine(App.SnapshotPath(), CgCaptureFilename);
        public static string CgCaptureDepthPath =>
            System.IO.Path.Combine(App.SnapshotPath(), CgCaptureBaseName + "_depth.exr");

        // Separate from LastStatus deliberately - LastStatus is overwritten every
        // frame by Update()'s continuous camera report, which would clobber a
        // one-off capture result before it could ever be read back (confirmed
        // happening the first time this was tried).
        public static string LastCgCaptureStatus { get; private set; } = "not captured yet";

        public static void RefreshCgCapture(int width, int height)
        {
            // The live CG supersedes the snapshot; a ~1s snapshot here would only stall
            // the fast preview (the control page's auto-refresh and Play loop still ask
            // for them). Turn live CG off to take one, e.g. for a parity check.
            if (PostProductionLiveCg.Enabled)
            {
                LastCgCaptureStatus = "skipped: live CG is on (postprod.preview.livecg=0 to take a snapshot)";
                return;
            }
            if (!s_HasTargetPose)
            {
                LastCgCaptureStatus = "no target pose set - call postprod.preview.pose first";
                return;
            }
            TrTransform tr = TrTransform.TR(s_TargetPosition, s_TargetRotation);
            ScreenshotManager.TakeSnapshot(
                tr, CgCaptureFilename, width, height,
                superSampling: 1f, removeBackground: false, renderDepth: true, renderNormals: false);
            PostProductionCompositeImageEffect.InvalidateCgCache();
            CgCaptureCount++;
            LastCgCaptureStatus = $"wrote {CgCaptureColorPath} + {CgCaptureDepthPath} at {width}x{height}";
        }

        public static void SetPlate(string colorPath, string depthPath)
        {
            PostProductionCompositeImageEffect.SetPlatePaths(colorPath, depthPath);
        }

        public static void SetNaiveMode(bool naive)
        {
            PostProductionCompositeImageEffect.NaiveMode = naive;
        }

        public static void SetCgOnlyMode(bool cgOnly)
        {
            PostProductionCompositeImageEffect.CgOnlyMode = cgOnly;
        }

        private void ReportStatus(Camera cam, string source)
        {
            LastStatus = $"[{source}] enabled={Enabled} cam={cam.name} " +
                $"pixelSize={cam.pixelWidth}x{cam.pixelHeight} fov={cam.fieldOfView:F3} " +
                $"usePhysical={cam.usePhysicalProperties} " +
                $"projMatrix={MatrixSummary(cam.projectionMatrix)} " +
                $"targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "null")} " +
                $"enabled={cam.enabled} gameObjectActive={cam.gameObject.activeInHierarchy} " +
                $"stereoTargetEye={cam.stereoTargetEye} " +
                $"worldPos={cam.transform.position} worldRot={cam.transform.rotation} " +
                $"hasTargetPose={s_HasTargetPose}";
        }

        private static string MatrixSummary(Matrix4x4 m)
        {
            return $"[{m[0, 0]:F4},{m[0, 2]:F4}|{m[1, 1]:F4},{m[1, 2]:F4}]";
        }

        // TEMP DEBUG: caches a listing of every enabled Camera in the scene, to
        // check whether App.VrSdk.GetVrCamera() is really the only candidate for
        // what's shown - e.g. a separate left/right eye camera, or another camera
        // with a higher depth rendering on top and obscuring it. Must run on the
        // main thread (Camera.allCameras is a Unity API) - called from the
        // postprod.debug.dumpcameras command, not directly from a query.
        public static string AllCamerasReport { get; private set; } = "not captured yet";

        public static void CaptureAllCamerasReport()
        {
            var cams = Camera.allCameras;
            var sb = new System.Text.StringBuilder();
            sb.Append($"count={cams.Length}\n");
            Camera ours = App.VrSdk.GetVrCamera();
            foreach (var c in cams)
            {
                sb.Append(
                    $"name={c.name} isOurs={(c == ours)} depth={c.depth} " +
                    $"targetDisplay={c.targetDisplay} enabled={c.enabled} " +
                    $"fov={c.fieldOfView:F2} pixelSize={c.pixelWidth}x{c.pixelHeight} " +
                    $"clearFlags={c.clearFlags} cullingMask={c.cullingMask} " +
                    $"targetTexture={(c.targetTexture != null ? c.targetTexture.name : "null")}\n");
            }
            AllCamerasReport = sb.ToString();
        }
    }
}
