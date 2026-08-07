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

// Replay drives PhotonRPC.DeleteStroke directly (see class comment below), so - like
// PhotonRPC.cs itself - this only compiles when Photon multiplayer is present.
#if MP_PHOTON

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using TiltBrush;
using OpenBrush.Multiplayer;

namespace OpenBrush.Multiplayer.Recording
{
    // Reads a flight recorder .jsonl log back and replays it into the current sketch.
    //
    // Strokes are replayed through Open Brush's own native timestamp-based playback system
    // (SketchMemoryScript.PlaybackMode.Timestamps / ScenePlaybackByTimeLayered) - the same
    // mechanism that makes a loaded .tilt file "draw itself" over time. Every recorded control
    // point already carries an accurate, translated tick-ms (see SessionRecorder's offset
    // correction), so handing the whole set of strokes to that system up front gives fluid,
    // point-by-point reveal for free, rather than the previous approach of materialising each
    // stroke instantly and all at once when its completion event's tickMs came due.
    //
    // Pose and stroke-delete events stay on a simple scheduled-immediate-apply path - there's
    // no equivalent native system for those, and deletion has no "fluid" dimension to reuse one
    // for anyway.
    //
    // Ghost rig visualisation is deliberately minimal - simple primitive markers at each
    // recorded player's head/hand/tool positions, not the full networked avatar prefab
    // (which can't easily be spawned outside Fusion's own spawn flow). This isn't a
    // shortcut so much as a preview of the real end state: the compositing design calls
    // for pose-driven occlusion proxy geometry, not a visible avatar, so simple markers
    // are actually the right shape of thing here, just not yet rendered depth-only.
    //
    // Not yet built: pause/seek and locking the camera to a recorded player's POV - both
    // called out in the design discussion as next steps once this core loop is proven.
    public class SessionPlaybackController
    {
        public static SessionPlaybackController m_Instance = new SessionPlaybackController();

        private List<(int tickMs, PoseRecord pose)> m_PoseRecords;
        private List<(int tickMs, StrokeRecord stroke)> m_DeleteRecords;
        private int m_NextPoseIndex;
        private int m_NextDeleteIndex;
        private bool m_StrokePlaybackActive;

        private double m_PlaybackStartRealtime;
        private int m_BaseTickMs;
        private readonly Dictionary<ulong, GhostRig> m_Ghosts = new();
        private Transform m_GhostRoot;

        public bool IsPlaying { get; private set; }
        public string CurrentFilePath { get; private set; }

        private static string RecordingsDirectory => Path.Combine(App.UserPath(), "FlightRecordings");

        // Newest first, filenames only (matches what StartReplay's filename param expects).
        public string[] ListRecordings()
        {
            if (!Directory.Exists(RecordingsDirectory)) return Array.Empty<string>();
            return Directory.GetFiles(RecordingsDirectory, "*.jsonl")
                .Select(Path.GetFileName)
                .OrderByDescending(f => f)
                .ToArray();
        }

        // filename: bare filename only (e.g. "session_20260801_114758.jsonl"), resolved inside
        // RecordingsDirectory - Path.GetFileName() strips any directory component so this can't
        // be used to read files outside FlightRecordings. Empty/null replays the most recent
        // recording, which is the common case when testing.
        public bool StartReplay(string filename)
        {
            if (IsPlaying)
            {
                Debug.LogWarning("[SessionPlaybackController] StartReplay called while already playing - stopping the previous replay first.");
                Stop();
            }

            try
            {
                string path = ResolveRecordingPath(filename);
                if (path == null) return false;

                var (poseRecords, deleteRecords, strokes) = ParseFile(path);
                if (poseRecords.Count == 0 && deleteRecords.Count == 0 && strokes.Count == 0)
                {
                    Debug.LogWarning($"[SessionPlaybackController] Recording has no records: {path}");
                    return false;
                }

                poseRecords.Sort((a, b) => a.tickMs.CompareTo(b.tickMs));
                deleteRecords.Sort((a, b) => a.tickMs.CompareTo(b.tickMs));
                strokes.Sort((a, b) => a.HeadTimestampMs.CompareTo(b.HeadTimestampMs));

                m_PoseRecords = poseRecords;
                m_DeleteRecords = deleteRecords;
                m_NextPoseIndex = 0;
                m_NextDeleteIndex = 0;

                int earliestTickMs = int.MaxValue;
                if (poseRecords.Count > 0) earliestTickMs = Math.Min(earliestTickMs, poseRecords[0].tickMs);
                if (deleteRecords.Count > 0) earliestTickMs = Math.Min(earliestTickMs, deleteRecords[0].tickMs);
                if (strokes.Count > 0) earliestTickMs = Math.Min(earliestTickMs, (int)strokes[0].HeadTimestampMs);
                m_BaseTickMs = earliestTickMs == int.MaxValue ? 0 : earliestTickMs;

                if (strokes.Count > 0)
                {
                    // Strokes must already be in the memory list (as NotCreated) before handing
                    // them to BeginDrawingFromMemory - matches how .tilt loading does it
                    // (SaveLoadScript.Load populates m_MemoryList, then SketchControlsScript
                    // calls BeginDrawingFromMemory with that same set).
                    foreach (var stroke in strokes)
                    {
                        SketchMemoryScript.m_Instance.MemoryListAdd(stroke);
                    }
                    SketchMemoryScript.m_Instance.SetPlaybackMode(SketchMemoryScript.PlaybackMode.Timestamps, 0f);
                    // forEdit: true - skips SketchMemoryScript's normal "hide the user's pointer
                    // during playback" behaviour, so painting during replay keeps working.
                    SketchMemoryScript.m_Instance.BeginDrawingFromMemory(strokes, bDrawFromStart: true, forEdit: true, playAudio: false);
                    // BeginDrawingFromMemory sets CurrentSketchTime from GetEarliestTimestamp(),
                    // which reads the *whole* memory list, not just our strokes - override with
                    // our own clock's actual start so playback timing is exactly right regardless
                    // of anything else already in the sketch.
                    App.Instance.CurrentSketchTime = m_BaseTickMs / 1000.0;
                    m_StrokePlaybackActive = true;
                }
                else
                {
                    m_StrokePlaybackActive = false;
                }

                m_PlaybackStartRealtime = Time.realtimeSinceStartupAsDouble;
                CurrentFilePath = path;
                m_GhostRoot = new GameObject("FlightRecorderGhosts").transform;

                IsPlaying = true;
                Debug.Log($"[SessionPlaybackController] Replay started: {path} ({strokes.Count} strokes, {poseRecords.Count} pose samples, {deleteRecords.Count} deletes).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SessionPlaybackController] StartReplay failed: {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            if (!IsPlaying && m_GhostRoot == null) return;

            if (m_StrokePlaybackActive)
            {
                // Force any still-revealing strokes to complete instantly rather than leaving
                // them half-drawn and frozen forever once Tick() stops calling
                // ContinueDrawingFromMemory().
                SketchMemoryScript.m_Instance.QuickLoadDrawingMemory();
                SketchMemoryScript.m_Instance.ContinueDrawingFromMemory();
                m_StrokePlaybackActive = false;
            }

            foreach (var ghost in m_Ghosts.Values)
            {
                ghost.Destroy();
            }
            m_Ghosts.Clear();

            if (m_GhostRoot != null)
            {
                UnityEngine.Object.Destroy(m_GhostRoot.gameObject);
                m_GhostRoot = null;
            }

            m_PoseRecords = null;
            m_DeleteRecords = null;
            bool wasPlaying = IsPlaying;
            IsPlaying = false;
            if (wasPlaying)
            {
                Debug.Log("[SessionPlaybackController] Replay stopped.");
            }
        }

        // Called every frame from FlightRecorderBootstrap.Update() - no-ops unless IsPlaying.
        public void Tick()
        {
            if (!IsPlaying) return;

            int nowTickMs = m_BaseTickMs +
                (int)((Time.realtimeSinceStartupAsDouble - m_PlaybackStartRealtime) * 1000.0);

            while (m_NextPoseIndex < m_PoseRecords.Count && m_PoseRecords[m_NextPoseIndex].tickMs <= nowTickMs)
            {
                ApplyPose(m_PoseRecords[m_NextPoseIndex].pose);
                m_NextPoseIndex++;
            }

            while (m_NextDeleteIndex < m_DeleteRecords.Count && m_DeleteRecords[m_NextDeleteIndex].tickMs <= nowTickMs)
            {
                ApplyStrokeDelete(m_DeleteRecords[m_NextDeleteIndex].stroke);
                m_NextDeleteIndex++;
            }

            if (m_StrokePlaybackActive)
            {
                App.Instance.CurrentSketchTime = nowTickMs / 1000.0;
                if (!SketchMemoryScript.m_Instance.ContinueDrawingFromMemory())
                {
                    m_StrokePlaybackActive = false;
                }
            }

            bool posesDone = m_NextPoseIndex >= m_PoseRecords.Count;
            bool deletesDone = m_NextDeleteIndex >= m_DeleteRecords.Count;
            if (posesDone && deletesDone && !m_StrokePlaybackActive)
            {
                Debug.Log("[SessionPlaybackController] Replay finished.");
                Stop();
            }
        }

        private string ResolveRecordingPath(string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                string latest = ListRecordings().FirstOrDefault();
                if (latest == null)
                {
                    Debug.LogError("[SessionPlaybackController] No recordings found in " + RecordingsDirectory);
                    return null;
                }
                return Path.Combine(RecordingsDirectory, latest);
            }

            string path = Path.Combine(RecordingsDirectory, Path.GetFileName(filename));
            if (!File.Exists(path))
            {
                Debug.LogError($"[SessionPlaybackController] Recording not found: {path}");
                return null;
            }
            return path;
        }

        private (List<(int tickMs, PoseRecord pose)>, List<(int tickMs, StrokeRecord stroke)>, List<Stroke>) ParseFile(string path)
        {
            var poseRecords = new List<(int, PoseRecord)>();
            var deleteRecords = new List<(int, StrokeRecord)>();
            var strokes = new List<Stroke>();

            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string type;
                try
                {
                    type = JsonUtility.FromJson<RecordTypePeek>(line).type;
                }
                catch (Exception)
                {
                    // Tolerates a truncated final line left by a hard crash mid-write.
                    continue;
                }

                switch (type)
                {
                    case "pose":
                        var pose = JsonUtility.FromJson<PoseRecord>(line);
                        poseRecords.Add((pose.tickMs, pose));
                        break;
                    case "strokeComplete":
                        var strokeRec = JsonUtility.FromJson<StrokeRecord>(line);
                        var stroke = BuildStroke(strokeRec);
                        if (stroke != null) strokes.Add(stroke);
                        break;
                    case "strokeDelete":
                        var deleteRec = JsonUtility.FromJson<StrokeRecord>(line);
                        deleteRecords.Add((deleteRec.tickMs, deleteRec));
                        break;
                    default:
                        Debug.LogWarning($"[SessionPlaybackController] Unknown record type '{type}' - skipped.");
                        break;
                }
            }
            return (poseRecords, deleteRecords, strokes);
        }

        private static Stroke BuildStroke(StrokeRecord rec)
        {
            if (rec.points == null || rec.points.Length == 0) return null;

            var stroke = new Stroke
            {
                m_Type = Stroke.Type.NotCreated,
                m_IntendedCanvas = App.Scene.MainCanvas,
                m_BrushGuid = Guid.TryParse(rec.brushGuid, out var bg) ? bg : Guid.Empty,
                m_BrushScale = rec.brushScale,
                m_BrushSize = rec.brushSize,
                m_Color = rec.color,
                m_Seed = rec.seed,
                m_ControlPoints = new PointerManager.ControlPoint[rec.points.Length],
                m_ControlPointsToDrop = new bool[rec.points.Length],
            };

            for (int i = 0; i < rec.points.Length; i++)
            {
                stroke.m_ControlPoints[i] = new PointerManager.ControlPoint
                {
                    m_Pos = rec.points[i].pos,
                    m_Orient = rec.points[i].orient,
                    m_Pressure = rec.points[i].pressure,
                    m_TimestampMs = (uint)Mathf.Max(0, rec.points[i].tickMs),
                };
            }

            return stroke;
        }

        private void ApplyPose(PoseRecord pose)
        {
            if (!m_Ghosts.TryGetValue(pose.player, out var ghost))
            {
                ghost = new GhostRig(pose.player, m_GhostRoot);
                m_Ghosts[pose.player] = ghost;
            }
            ghost.ApplyPose(pose);
        }

        private void ApplyStrokeDelete(StrokeRecord rec)
        {
            try
            {
                PhotonRPC.DeleteStroke(rec.seed, Guid.NewGuid(), rec.tickMs);
            }
            catch (InvalidOperationException)
            {
                Debug.LogWarning($"[SessionPlaybackController] No stroke with seed {rec.seed} found to delete during replay - skipped.");
            }
        }

        // Simple primitive markers at head/left/right/tool - see class-level comment on why
        // this stands in for a full avatar for now.
        private class GhostRig
        {
            private readonly Transform m_Head;
            private readonly Transform m_Left;
            private readonly Transform m_Right;
            private readonly Transform m_Tool;

            public GhostRig(ulong playerId, Transform root)
            {
                m_Head = MakeMarker($"Player{playerId}_Head", 0.18f, root);
                m_Left = MakeMarker($"Player{playerId}_Left", 0.08f, root);
                m_Right = MakeMarker($"Player{playerId}_Right", 0.08f, root);
                m_Tool = MakeMarker($"Player{playerId}_Tool", 0.05f, root);
            }

            public void ApplyPose(PoseRecord pose)
            {
                m_Head.SetPositionAndRotation(pose.headPos, pose.headRot);
                m_Left.SetPositionAndRotation(pose.leftPos, pose.leftRot);
                m_Right.SetPositionAndRotation(pose.rightPos, pose.rightRot);
                m_Tool.SetPositionAndRotation(pose.toolPos, pose.toolRot);

                // Same setting and same split as PhotonPlayerRig.UpdateControllerVisibility() -
                // only the head/hand markers hide, the tool/cursor marker never does, since that's
                // what's actually tracking where strokes are being drawn. Checked every call
                // (not just once at construction) so toggling mid-replay takes effect immediately,
                // matching the live behaviour.
                bool hideRemoteAvatars = MultiplayerManager.m_Instance != null &&
                    MultiplayerManager.m_Instance.HideRemotePlayerAvatars;
                m_Head.gameObject.SetActive(!hideRemoteAvatars);
                m_Left.gameObject.SetActive(!hideRemoteAvatars);
                m_Right.gameObject.SetActive(!hideRemoteAvatars);
            }

            public void Destroy()
            {
                if (m_Head != null) UnityEngine.Object.Destroy(m_Head.gameObject);
                if (m_Left != null) UnityEngine.Object.Destroy(m_Left.gameObject);
                if (m_Right != null) UnityEngine.Object.Destroy(m_Right.gameObject);
                if (m_Tool != null) UnityEngine.Object.Destroy(m_Tool.gameObject);
            }

            private static Transform MakeMarker(string name, float scale, Transform root)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = name;
                var collider = go.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.Destroy(collider);
                go.transform.localScale = Vector3.one * scale;
                go.transform.SetParent(root, worldPositionStays: true);
                return go.transform;
            }
        }
    }
}

#endif // MP_PHOTON
