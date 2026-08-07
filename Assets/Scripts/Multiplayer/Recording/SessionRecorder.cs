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

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using TiltBrush;

namespace OpenBrush.Multiplayer.Recording
{
    // Records a multiplayer session (player pose + stroke events) to a newline-delimited
    // JSON log for later "fix it in post" video compositing - see the flight recorder
    // design notes. This is a capture-only layer: it writes what arrives, tagged with the
    // shared Fusion tick clock, and does no live scheduling/buffering - a replay step reads
    // the log later and schedules everything against tickMs, so arrival lag here doesn't
    // matter.
    //
    // Known limitation: stroke events aren't attributed to a player id today, because none
    // of the stroke RPCs in PhotonRPC.cs carry sender identity (no RpcInfo param) - only
    // pose is per-player. Not needed for the current design (per-player POV during replay
    // is driven by pose, not by filtering strokes), but would need RpcInfo added to the
    // stroke RPCs if per-player stroke attribution is ever required.
    public class SessionRecorder
    {
        public static SessionRecorder m_Instance = new SessionRecorder();

        // Caps pose sampling per player so a 90Hz Render() loop doesn't write 90 lines/sec/player -
        // nothing downstream (video compositing) needs finer than this, and unthrottled writes
        // risk frame-time hitches on Quest hardware.
        private const int kMinPoseIntervalMs = 33;
        private const int kFlushEveryNWrites = 200;

        private StreamWriter m_Writer;
        private string m_CurrentFilePath;
        private int m_WritesSinceFlush;
        private readonly Dictionary<ulong, int> m_LastPoseTickMsPerPlayer = new();

        public bool IsRecording { get; private set; }
        public string CurrentFilePath => m_CurrentFilePath;

        public bool StartRecording()
        {
            if (IsRecording)
            {
                Debug.LogWarning("[SessionRecorder] StartRecording called while already recording - ignored.");
                return false;
            }

            try
            {
                string dir = Path.Combine(App.UserPath(), "FlightRecordings");
                Directory.CreateDirectory(dir);
                m_CurrentFilePath = Path.Combine(dir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
                // AutoFlush is deliberately off - flushed periodically in WriteLine() and on
                // Stop() instead, so a 90Hz pose stream doesn't hit disk synchronously every line.
                m_Writer = new StreamWriter(m_CurrentFilePath, append: false) { AutoFlush = false };
                m_WritesSinceFlush = 0;
                m_LastPoseTickMsPerPlayer.Clear();
                IsRecording = true;
                Debug.Log($"[SessionRecorder] Recording started: {m_CurrentFilePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SessionRecorder] StartRecording failed: {ex.Message}");
                m_Writer = null;
                IsRecording = false;
                return false;
            }
        }

        public void StopRecording()
        {
            if (!IsRecording)
            {
                Debug.LogWarning("[SessionRecorder] StopRecording called while not recording - ignored.");
                return;
            }

            try
            {
                m_Writer?.Flush();
                m_Writer?.Close();
                Debug.Log($"[SessionRecorder] Recording stopped: {m_CurrentFilePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SessionRecorder] StopRecording failed to close file cleanly: {ex.Message}");
            }
            finally
            {
                m_Writer = null;
                IsRecording = false;
            }
        }

        // playerId: the recorded player's stable Meta/Oculus user id (PhotonPlayerRig.oculusPlayerId).
        // networkTimestampMs: MultiplayerManager.GetNetworkedTimestampMilliseconds() at the moment of
        // sampling - the shared Fusion tick clock, not this device's local CurrentSketchTime.
        public void RecordPose(
            ulong playerId,
            int? networkTimestampMs,
            TrTransform head,
            TrTransform left,
            TrTransform right,
            TrTransform tool)
        {
            if (!IsRecording) return;

            int tickMs = networkTimestampMs ?? -1;
            if (tickMs >= 0 &&
                m_LastPoseTickMsPerPlayer.TryGetValue(playerId, out int lastTickMs) &&
                tickMs - lastTickMs < kMinPoseIntervalMs)
            {
                return;
            }
            m_LastPoseTickMsPerPlayer[playerId] = tickMs;

            var record = new PoseRecord
            {
                type = "pose",
                tickMs = tickMs,
                player = playerId,
                headPos = head.translation,
                headRot = head.rotation,
                leftPos = left.translation,
                leftRot = left.rotation,
                rightPos = right.translation,
                rightRot = right.rotation,
                toolPos = tool.translation,
                toolRot = tool.rotation,
            };
            WriteLine(JsonUtility.ToJson(record));
        }

        // Called once per completed stroke (covers both the single-RPC "Full" path and the
        // Begin/Continue/Complete streaming path - PhotonRPC.cs funnels both into one place,
        // CreateBrushStroke(), which is where this is called from). controlPoints must be the
        // stroke's full point array, each still carrying its origin-device local
        // CurrentSketchTime-based m_TimestampMs - the translation to the shared tick clock
        // happens in here, not at the call site.
        public void RecordStrokeComplete(
            int? networkTimestampMs,
            Guid commandGuid,
            int seed,
            Guid brushGuid,
            Color color,
            float brushSize,
            float brushScale,
            PointerManager.ControlPoint[] controlPoints)
        {
            if (!IsRecording) return;
            if (controlPoints == null || controlPoints.Length == 0) return;

            int tickMs = networkTimestampMs ?? -1;

            // Single-anchor offset correction: the stroke's own local (CurrentSketchTime-based)
            // clock and the shared Fusion tick clock both advance at real elapsed-time rate on
            // the origin device, so one offset - derived from the last point vs this command's
            // tick anchor - is enough to translate every point in the stroke. See flight
            // recorder design notes for why this is preferred over a per-point network stamp.
            int offset = 0;
            if (tickMs >= 0)
            {
                uint strokeLocalEndTimestampMs = controlPoints[controlPoints.Length - 1].m_TimestampMs;
                offset = tickMs - (int)strokeLocalEndTimestampMs;
            }

            var points = new RecordedControlPoint[controlPoints.Length];
            for (int i = 0; i < controlPoints.Length; i++)
            {
                points[i] = new RecordedControlPoint
                {
                    pos = controlPoints[i].m_Pos,
                    orient = controlPoints[i].m_Orient,
                    pressure = controlPoints[i].m_Pressure,
                    tickMs = tickMs >= 0 ? (int)controlPoints[i].m_TimestampMs + offset : -1,
                };
            }

            var record = new StrokeRecord
            {
                type = "strokeComplete",
                tickMs = tickMs,
                commandGuid = commandGuid.ToString(),
                seed = seed,
                brushGuid = brushGuid.ToString(),
                color = color,
                brushSize = brushSize,
                brushScale = brushScale,
                points = points,
            };
            WriteLine(JsonUtility.ToJson(record), immediateFlush: true);
        }

        public void RecordStrokeDelete(int? networkTimestampMs, Guid commandGuid, int seed)
        {
            if (!IsRecording) return;

            var record = new StrokeRecord
            {
                type = "strokeDelete",
                tickMs = networkTimestampMs ?? -1,
                commandGuid = commandGuid.ToString(),
                seed = seed,
                points = Array.Empty<RecordedControlPoint>(),
            };
            WriteLine(JsonUtility.ToJson(record), immediateFlush: true);
        }

        // immediateFlush: strokes (rare, valuable, one per completed/deleted stroke) always
        // flush right away - the cost is negligible at that frequency. Pose (up to ~30Hz per
        // player) stays batched behind the periodic/lifecycle flush below, since flushing
        // every line at that rate risks frame hitches on Quest. Worst case on a hard crash is
        // losing a couple of buffered pose samples, never a stroke and never a torn line.
        private void WriteLine(string json, bool immediateFlush = false)
        {
            try
            {
                m_Writer?.WriteLine(json);
                if (immediateFlush)
                {
                    m_Writer?.Flush();
                    m_WritesSinceFlush = 0;
                    return;
                }
                m_WritesSinceFlush++;
                if (m_WritesSinceFlush >= kFlushEveryNWrites)
                {
                    m_Writer?.Flush();
                    m_WritesSinceFlush = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SessionRecorder] Failed to write record, stopping recording: {ex.Message}");
                StopRecording();
            }
        }

        // Safety net for app pause/quit (Application lifecycle callbacks fire on a
        // MonoBehaviour elsewhere and forward here - SessionRecorder itself isn't one).
        // Flushes without stopping, since on Android/Quest OnApplicationPause fires on
        // routine backgrounding (headset removal, system overlay) that often resumes
        // normally, not just on a real exit - see the earlier Guardian-boundary-disconnect
        // investigation this session for why pause != session end.
        public void FlushIfRecording()
        {
            if (!IsRecording) return;
            try
            {
                m_Writer?.Flush();
                m_WritesSinceFlush = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SessionRecorder] FlushIfRecording failed: {ex.Message}");
            }
        }
    }
}
