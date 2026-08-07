// Copyright 2023 The Open Brush Authors
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


#if MP_PHOTON

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Fusion;
using TiltBrush;
using static TiltBrush.SketchControlsScript;
using System.Threading.Tasks;
using Photon.Voice.Unity;
using OpenBrush.Multiplayer.Recording;

namespace OpenBrush.Multiplayer
{
    public class PhotonRPC : SimulationBehaviour
    {
        private static Dictionary<Guid, Stroke> m_inProgressStrokes;
        private static List<PendingCommand> m_pendingCommands;
        private static Dictionary<Guid, TaskCompletionSource<bool>> m_acknowledgments;

        // Live stroke streaming (fluid remote rendering) - keyed by the drawing stroke's own
        // seed, not a stroke/command guid, since neither exists yet while a stroke is still being
        // drawn. See RPC_LiveStrokeBegin/Continue/End below and MultiplayerManager's sender side.
        private static Dictionary<int, LivePreview> m_LivePreviewPointers;

        public void Awake()
        {
            m_inProgressStrokes = new();
            m_pendingCommands = new();
            m_acknowledgments = new();
            m_LivePreviewPointers = new();
        }

        public void Update()
        {
            TryProcessCommands();
            SweepStaleLivePreviews();
        }

        // Entry point for callers with no live PhotonRPC instance to hang off (e.g. flight
        // recorder replay, which can run after leaving multiplayer - see FlightRecorderBootstrap).
        // TryProcessCommands()/CheckifChildStillPending()/InvokePreCommands() only ever touch the
        // static m_pendingCommands list, never instance state, so they're safe to drive this way.
        // Calling this from both here and PhotonRPC.Update() while actually connected is harmless -
        // the second call just finds an already-empty queue.
        public static void ProcessPendingCommands()
        {
            m_pendingCommands ??= new();
            m_inProgressStrokes ??= new();
            TryProcessCommands();
        }

        private static bool CheckifChildStillPending(PendingCommand pending)
        {
            if (pending.TotalExpectedChildren == pending.Command.ChildrenCount)
            {
                bool moreChildrenToAssign = false;

                foreach (var childCommand in pending.Command.Children)
                {
                    // has a child present in the pending queue, check them too
                    var childPending = m_pendingCommands.FirstOrDefault(x => x.Guid == childCommand.Guid);

                    if (!childPending.Guid.Equals(default))
                    {
                        var childIsStillPending = CheckifChildStillPending(childPending);

                        if (!childIsStillPending)
                        {
                            m_pendingCommands.Remove(childPending);
                        }

                        moreChildrenToAssign |= childIsStillPending;
                    }
                }

                return moreChildrenToAssign;
            }

            else
            {
                return true;
            }
        }

        private static void InvokePreCommands(PendingCommand pendingCommand)
        {
            pendingCommand.PreCommandAction.Invoke();

            foreach (var childCommand in pendingCommand.ChildCommands)
            {
                InvokePreCommands(childCommand);
            }
        }

        private static void TryProcessCommands()
        {
            if (m_pendingCommands.Count == 0)
            {
                return;
            }

            var command = m_pendingCommands[0];

            bool stillPending = CheckifChildStillPending(command);

            if (stillPending)
            {
                return;
            }

            // All children present, begin execution

            m_pendingCommands.RemoveAt(0);

            InvokePreCommands(command);

            //SketchMemoryScript.m_Instance.PerformAndRecordCommand(command.Command, invoke: false);
            SketchMemoryScript.m_Instance.PerformAndRecordNetworkCommand(command.Command);

            TryProcessCommands();
        }

        private static void AddPendingCommand(Action preAction, Guid commandGuid, Guid parentGuid, BaseCommand command, int childCount)
        {

            PendingCommand pendingCommand = new PendingCommand(commandGuid, command, preAction, childCount);

            if (!parentGuid.Equals(default))
            {
                var pendingParent = m_pendingCommands.FirstOrDefault(x => x.Guid == parentGuid);
                pendingParent.ChildCommands.Add(pendingCommand);
            }

            m_pendingCommands.Add(pendingCommand);
        }

        private static bool CheckifCommandGuidIsInStack(Guid commandGuid)
        {

            if (SketchMemoryScript.m_Instance.IsCommandInStack(commandGuid))
            {
                //Debug.Log($"Command with Guid {commandGuid} already in stack.");
                return true;
            }
            return false;
        }

        private static bool CheckifStrokeGuidIsInMemory(Guid strokeGuid)
        {

            if (SketchMemoryScript.m_Instance.IsStrokeInMemory(strokeGuid))
            {
                //Debug.Log($"Stroke with Guid {strokeGuid} already in memory.");
                return true;
            }
            return false;
        }

        private static BaseCommand FindParentCommand(Guid parentGuid)
        {
            PendingCommand pendingParent = m_pendingCommands.FirstOrDefault(x => x.Guid == parentGuid);

            if (!parentGuid.Equals(default))
            {
                return pendingParent.Command;
            }
            return null;
        }

        public static void Send_BaseCommand(NetworkRunner runner, Guid commandGuid, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            if (targetPlayer == default)
            {
                RPC_BaseCommand(runner, commandGuid, parentGuid, childCount);
            }
            else
            {
                RPC_BaseCommand(runner, commandGuid, parentGuid, childCount, targetPlayer);
            }
        }

        private static void BaseCommand(Guid commandGuid, Guid parentGuid = default, int childCount = 0)
        {
            if (CheckifCommandGuidIsInStack(commandGuid)) return;

            Debug.Log($"Base command child count: {childCount}");
            var parentCommand = FindParentCommand(parentGuid);
            var command = new BaseCommand(parent: parentCommand);

            AddPendingCommand(() => { }, commandGuid, parentGuid, command, childCount);
        }

        public static void Send_BrushStrokeFull(NetworkRunner runner, NetworkedStroke strokeData, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            if (targetPlayer == default)
            {
                RPC_BrushStrokeFull(runner, strokeData, commandGuid, timestamp, parentGuid, childCount);
            }
            else
            {
                RPC_BrushStrokeFull(runner, strokeData, commandGuid, timestamp, parentGuid, childCount, targetPlayer);
            }
        }

        private static void BrushStrokeFull(NetworkedStroke strokeData, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {

            if (CheckifCommandGuidIsInStack(commandGuid)) return;

            var decode = NetworkedStroke.ToStroke(strokeData);

            CreateBrushStroke(decode, commandGuid, timestamp, parentGuid, childCount);
        }

        public static void Send_BrushStrokeBegin(NetworkRunner runner, Guid id, NetworkedStroke strokeData, int finalLength, [RpcTarget] PlayerRef targetPlayer = default)
        {
            if (targetPlayer == default)
            {
                RPC_BrushStrokeBegin(runner, id, strokeData, finalLength);
            }
            else
            {
                RPC_BrushStrokeBegin(runner, id, strokeData, finalLength, targetPlayer);
            }
        }

        private static void BrushStrokeBegin(Guid id, NetworkedStroke strokeData, int finalLength)
        {
            var decode = NetworkedStroke.ToStroke(strokeData);

            decode.m_Type = Stroke.Type.NotCreated;
            decode.m_IntendedCanvas = App.Scene.MainCanvas;

            Array.Resize(ref decode.m_ControlPoints, finalLength);
            Array.Resize(ref decode.m_ControlPointsToDrop, finalLength);

            if (m_inProgressStrokes.ContainsKey(id))
            {
                Debug.LogError("Shouldn't be here!");
                return;
            }

            m_inProgressStrokes[id] = decode;
        }

        public static void Send_BrushStrokeContinue(NetworkRunner runner, Guid id, int offset, NetworkedControlPoint[] controlPoints, bool[] dropPoints, [RpcTarget] PlayerRef targetPlayer = default)
        {
            if (targetPlayer == default)
            {
                RPC_BrushStrokeContinue(runner, id, offset, controlPoints, dropPoints);
            }
            else
            {
                RPC_BrushStrokeContinue(runner, id, offset, controlPoints, dropPoints, targetPlayer);
            }
        }

        private static void BrushStrokeContinue(Guid id, int offset, NetworkedControlPoint[] controlPoints, bool[] dropPoints)
        {
            if (!m_inProgressStrokes.ContainsKey(id))
            {
                Debug.LogError("shouldn't be here!");
                return;
            }

            var stroke = m_inProgressStrokes[id];

            for (int i = 0; i < controlPoints.Length; ++i)
            {
                stroke.m_ControlPoints[offset + i] = NetworkedControlPoint.ToControlPoint(controlPoints[i]);
                stroke.m_ControlPointsToDrop[offset + i] = dropPoints[i];
            }
        }

        public static void Send_BrushStrokeComplete(NetworkRunner runner, Guid id, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            if (targetPlayer == default)
            {
                RPC_BrushStrokeComplete(runner, id, commandGuid, timestamp, parentGuid, childCount);
            }
            else
            {
                RPC_BrushStrokeComplete(runner, id, commandGuid, timestamp, parentGuid, childCount, targetPlayer);
            }
        }

        private static void BrushStrokeComplete(Guid id, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {

            if (CheckifCommandGuidIsInStack(commandGuid)) return;

            if (!m_inProgressStrokes.ContainsKey(id))
            {
                Debug.LogError("shouldn't be here!");
                return;
            }

            var stroke = m_inProgressStrokes[id];

            CreateBrushStroke(stroke, commandGuid, timestamp, parentGuid, childCount);

            m_inProgressStrokes.Remove(id);
        }

        // Public (not just for internal RPC use) so SessionPlaybackController can drive replay
        // through the exact same path a live remote stroke takes, rather than duplicating this
        // logic - see flight recorder design notes.
        public static void CreateBrushStroke(Stroke stroke, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {

            // Flight recorder capture - covers both the single-RPC Full path and the
            // Begin/Continue/Complete streaming path, since both funnel through here with a
            // fully-populated stroke. No-op unless SessionRecorder.IsRecording.
            SessionRecorder.m_Instance.RecordStrokeComplete(
                timestamp, commandGuid, stroke.m_Seed, stroke.m_BrushGuid,
                stroke.m_Color, stroke.m_BrushSize, stroke.m_BrushScale, stroke.m_ControlPoints);

            Action preAction = () =>
            {
                // Tear down any live-drawn preview for this stroke right before the authoritative
                // geometry is built, so there's no overlap between the two.
                CleanUpLivePreview(stroke.m_Seed);

                stroke.m_Type = Stroke.Type.NotCreated;
                stroke.m_IntendedCanvas = App.Scene.MainCanvas;
                stroke.Recreate(null, App.Scene.MainCanvas);
                SketchMemoryScript.m_Instance.MemoryListAdd(stroke);
            };

            var parentCommand = FindParentCommand(parentGuid);

            var command = new BrushStrokeCommand(stroke, commandGuid, timestamp, parent: parentCommand);

            AddPendingCommand(preAction, commandGuid, parentGuid, command, childCount);
        }

        public static void Send_DeleteStroke(NetworkRunner runner, int seed, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            if (targetPlayer == default)
            {
                RPC_DeleteStroke(runner, seed, commandGuid, timestamp, parentGuid, childCount);
            }
            else
            {
                RPC_DeleteStroke(runner, seed, commandGuid, timestamp, parentGuid, childCount, targetPlayer);
            }
        }

        // Public so SessionPlaybackController can replay deletions through the same path a
        // live remote delete takes.
        public static void DeleteStroke(int seed, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {
            if (CheckifCommandGuidIsInStack(commandGuid)) return;

            // TODO : implment GUID for strokesdata.
            // The range of int is large (-2,147,483,648 to 2,147,483,647), but collisions are still possible.
            var foundStroke = SketchMemoryScript.m_Instance.GetMemoryList.Where(x => x.m_Seed == seed).First();

            if (foundStroke != null)
            {
                SessionRecorder.m_Instance.RecordStrokeDelete(timestamp, commandGuid, seed);

                var parentCommand = FindParentCommand(parentGuid);
                var command = new DeleteStrokeCommand(foundStroke, commandGuid, timestamp, parent: parentCommand);

                AddPendingCommand(() => { }, commandGuid, parentGuid, command, childCount);
            }
            else
            {
                Debug.LogError($"couldn't find stroke with seed: {seed}");
            }
        }

        #region Live stroke streaming (fluid remote rendering)
        // Purely-visual, best-effort preview channel, entirely separate from the authoritative
        // Begin/Continue/Complete/Full pipeline above, which is left completely unchanged - the
        // real stroke data and undo/redo registration still only ever come from there. This just
        // grows a temporary line on a dedicated remote pointer while the sender is actively
        // drawing, then gets torn down the moment the authoritative stroke arrives (matched by
        // seed - see SketchMemoryScript.MemorizeBrushStroke for why every local stroke now gets
        // a real one). If the authoritative stroke never arrives for some reason, a per-frame
        // sweep in Update() force-cleans anything stale so nothing is orphaned permanently.

        private class LivePreview
        {
            public PointerScript Pointer;
            public Stroke PlaceholderStroke;
            public float LastUpdateRealtime;
        }

        // Seconds with no Continue/authoritative-arrival before a preview is force-cleaned -
        // covers the case where End or the final stroke data never shows up (dropped RPC, error).
        private const float kLivePreviewStaleSeconds = 8f;

        public static void Send_LiveStrokeBegin(NetworkRunner runner, int seed, Guid brushGuid, Color color,
            float brushSize, float brushScale, Vector3 pos, Quaternion orient, float pressure)
        {
            RPC_LiveStrokeBegin(runner, seed, brushGuid, color, brushSize, brushScale, pos, orient, pressure);
        }

        public static void Send_LiveStrokeContinue(NetworkRunner runner, int seed, NetworkedControlPoint[] newPoints)
        {
            RPC_LiveStrokeContinue(runner, seed, newPoints);
        }

        public static void Send_LiveStrokeEnd(NetworkRunner runner, int seed)
        {
            RPC_LiveStrokeEnd(runner, seed);
        }

        private static void LiveStrokeBegin(int seed, Guid brushGuid, Color color, float brushSize, float brushScale,
            Vector3 pos, Quaternion orient, float pressure)
        {
            if (m_LivePreviewPointers.ContainsKey(seed))
            {
                // Already tracking this one - a duplicate/late Begin arrived, ignore it.
                return;
            }

            var placeholder = new Stroke
            {
                m_Type = Stroke.Type.NotCreated,
                m_IntendedCanvas = App.Scene.MainCanvas,
                m_BrushGuid = brushGuid,
                m_Color = color,
                m_BrushSize = brushSize,
                m_BrushScale = brushScale,
                m_Seed = seed,
                m_ControlPoints = new[]
                {
                    new PointerManager.ControlPoint { m_Pos = pos, m_Orient = orient, m_Pressure = pressure }
                },
                m_ControlPointsToDrop = new[] { false },
            };

            var pointer = PointerManager.m_Instance.CreateRemotePointer();
            if (pointer.BeginLineFromMemory(placeholder, App.Scene.MainCanvas) == null)
            {
                // Unknown brush guid etc - BeginLineFromMemory already logs. Nothing to preview.
                PointerManager.m_Instance.RemoveRemotePointer(pointer);
                return;
            }

            // BeginLineFromMemory only positions the pointer at point 0's transform - it does not
            // lay down any geometry for it. Every other caller of BeginLineFromMemory in this
            // codebase (Stroke.Recreate -> RecreateLineFromMemory -> UpdateLineFromStroke, and the
            // native timestamp-playback system) always follows it with an UpdateLineFromControlPoint
            // call starting from point 0, not point 1. Skipping this left the brush's internal
            // "last position" seeded but with no committed first vertex, which showed up as a
            // spurious triangle connecting back to the start point until the authoritative stroke
            // replaced the preview at completion.
            pointer.UpdateLineFromControlPoint(placeholder.m_ControlPoints[0]);
            pointer.UpdateLineVisuals();

            m_LivePreviewPointers[seed] = new LivePreview
            {
                Pointer = pointer,
                PlaceholderStroke = placeholder,
                LastUpdateRealtime = Time.realtimeSinceStartup,
            };
        }

        private static void LiveStrokeContinue(int seed, NetworkedControlPoint[] newPoints)
        {
            if (!m_LivePreviewPointers.TryGetValue(seed, out var preview)) return;
            if (newPoints == null || newPoints.Length == 0) return;

            foreach (var netCp in newPoints)
            {
                preview.Pointer.UpdateLineFromControlPoint(NetworkedControlPoint.ToControlPoint(netCp));
            }
            preview.Pointer.UpdateLineVisuals();
            preview.LastUpdateRealtime = Time.realtimeSinceStartup;

            // The pointer's own transform is set once in BeginLineFromMemory and never moved again,
            // but PointerManager.Update() calls UpdatePointer() -> UpdateLineFromObject() on every
            // remote pointer every frame regardless of our explicit Continue calls above. That
            // derives its target position from this stale transform, which used to yank
            // m_LastSpawnXf back to the stroke's start position between Continue batches (the cause
            // of the preview fanning back to the start point while drawing). Keep the pointer's
            // transform in sync with the latest point so that per-frame call is a no-op - matches
            // what local drawing does every frame anyway.
            var lastCp = newPoints[newPoints.Length - 1];
            var xf_CS = TrTransform.TRS(lastCp.m_Pos, lastCp.m_Orient, preview.PlaceholderStroke.m_BrushScale);
            var xf_RS = App.Scene.MainCanvas.Pose * xf_CS;
            preview.Pointer.transform.SetPositionAndRotation(xf_RS.translation, xf_RS.rotation);
        }

        private static void LiveStrokeEnd(int seed)
        {
            if (!m_LivePreviewPointers.TryGetValue(seed, out var preview)) return;
            // Nothing to draw right now - just bookkeeping so the stale sweep doesn't fire
            // immediately if the authoritative stroke takes a little longer to arrive than usual
            // (e.g. a large stroke needing several chunked Continue RPCs).
            preview.LastUpdateRealtime = Time.realtimeSinceStartup;
        }

        // Called from CreateBrushStroke() the moment the authoritative version of a stroke is
        // about to be (re)created, so the temporary preview never overlaps with the real thing.
        private static void CleanUpLivePreview(int seed)
        {
            if (!m_LivePreviewPointers.TryGetValue(seed, out var preview)) return;
            preview.Pointer.EndLineFromMemory(preview.PlaceholderStroke, discard: true);
            PointerManager.m_Instance.RemoveRemotePointer(preview.Pointer);
            m_LivePreviewPointers.Remove(seed);
        }

        private static void SweepStaleLivePreviews()
        {
            if (m_LivePreviewPointers.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<int> stale = null;
            foreach (var kvp in m_LivePreviewPointers)
            {
                if (now - kvp.Value.LastUpdateRealtime > kLivePreviewStaleSeconds)
                {
                    (stale ??= new List<int>()).Add(kvp.Key);
                }
            }
            if (stale == null) return;
            foreach (var seed in stale)
            {
                Debug.LogWarning($"[Multiplayer] Live stroke preview for seed {seed} never got an authoritative stroke - force-cleaned.");
                CleanUpLivePreview(seed);
            }
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_LiveStrokeBegin(NetworkRunner runner, int seed, Guid brushGuid, Color color,
            float brushSize, float brushScale, Vector3 pos, Quaternion orient, float pressure)
        {
            LiveStrokeBegin(seed, brushGuid, color, brushSize, brushScale, pos, orient, pressure);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_LiveStrokeContinue(NetworkRunner runner, int seed, NetworkedControlPoint[] newPoints)
        {
            LiveStrokeContinue(seed, newPoints);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_LiveStrokeEnd(NetworkRunner runner, int seed)
        {
            LiveStrokeEnd(seed);
        }

        #endregion

        public static void Send_SwitchEnvironment(NetworkRunner runner, Guid environmentGuid, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {

            if (targetPlayer == default)
            {
                RPC_SwitchEnvironment(runner, environmentGuid, commandGuid, timestamp, parentGuid, childCount);
            }
            else
            {
                RPC_SwitchEnvironment(runner, environmentGuid, commandGuid, timestamp, parentGuid, childCount, targetPlayer);
            }
        }

        private static void SwitchEnvironment(Guid environmentGuid, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {
            if (CheckifCommandGuidIsInStack(commandGuid)) return;

            TiltBrush.Environment environment = EnvironmentCatalog.m_Instance.GetEnvironment(environmentGuid);

            if (environment != null)
            {

                var parentCommand = FindParentCommand(parentGuid);
                var command = new SwitchEnvironmentCommand(environment, commandGuid, timestamp, parent: parentCommand);

                AddPendingCommand(() => { }, commandGuid, parentGuid, command, childCount);
            }
            else
            {
                Debug.LogError($"Environment with Guid {environmentGuid} not found.");
            }
        }

        public static async Task<bool> WaitForAcknowledgment(Guid commandGuid, int timeoutMilliseconds = 1000)
        {
            var tcs = new TaskCompletionSource<bool>();
            m_acknowledgments[commandGuid] = tcs;

            var timeoutTask = Task.Delay(timeoutMilliseconds);
            var acknowledgmentTask = tcs.Task;
            var completedTask = await Task.WhenAny(acknowledgmentTask, timeoutTask);

            if (completedTask == acknowledgmentTask)
            {
                m_acknowledgments.Remove(commandGuid);
                return await acknowledgmentTask;
            }
            else
            {
                m_acknowledgments.Remove(commandGuid);
                return false;
            }
        }

        private static void TransferRoomOwnership(NetworkPlayerSettings[] playerSettings,NetworkRoomSettings roomData)
        {
            if (!MultiplayerManager.m_Instance) return;

            // Convert NetworkPlayerSettings[] to RemotePlayerSettings[]
            RemotePlayerSettings[] remoteSettings = new RemotePlayerSettings[playerSettings.Length];
            for (int i = 0; i < playerSettings.Length; i++)
            {
                remoteSettings[i] = new RemotePlayerSettings(
                    playerSettings[i].m_PlayerId,
                    playerSettings[i].m_IsMutedForAll,
                    playerSettings[i].m_IsViewOnly
                );
            }

            RoomCreateData currentRoomData = new RoomCreateData();

            currentRoomData.maxPlayers = roomData.m_MaxPlayers;
            currentRoomData.silentRoom = roomData.m_IsSilentRoom;
            currentRoomData.viewOnlyRoom = roomData.m_IsViewOnlyRoom;
            
            MultiplayerManager.m_Instance.RoomOwnershipReceived(remoteSettings, currentRoomData);
        }

        private static void SetViewOnly(bool isEnabled)
        {
            MultiplayerManager.m_Instance.IsViewOnly = isEnabled;
        }

        #region RPCS
        [Rpc(InvokeLocal = false)]
        public static void RPC_SyncToSharedAnchor(NetworkRunner runner, string uuid)
        {
#if OCULUS_SUPPORTED
            OculusMRController.m_Instance.RemoteSyncToAnchor(uuid);
#endif // OCULUS_SUPPORTED
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_PerformCommand(NetworkRunner runner, string commandName, string guid, string[] data)
        {
            Debug.Log($"Command recieved: {commandName}");

            if (commandName.Equals("TiltBrush.BrushStrokeCommand"))
            {
                var asString = string.Join(string.Empty, data);
                Debug.Log(asString);
                var decode = JsonUtility.FromJson<Stroke>(asString);

                // Temp
                decode.m_BrushGuid = new System.Guid(guid);

                // Can we set up these more sensibly?
                decode.m_Type = Stroke.Type.NotCreated;
                decode.m_IntendedCanvas = App.Scene.MainCanvas;

                // Setup data that couldn't be transferred
                decode.Recreate(null, App.Scene.MainCanvas);
                SketchMemoryScript.m_Instance.MemoryListAdd(decode);

                SketchMemoryScript.m_Instance.PerformAndRecordCommand(new BrushStrokeCommand(decode), invoke: false);
            }
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_Undo(NetworkRunner runner, string commandName)
        {
            if (SketchMemoryScript.m_Instance.CanUndo())
            {
                SketchMemoryScript.m_Instance.StepBack(false);
            }
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_Redo(NetworkRunner runner, string commandName)
        {
            if (SketchMemoryScript.m_Instance.CanRedo())
            {
                SketchMemoryScript.m_Instance.StepForward(false);
            }
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BaseCommand(NetworkRunner runner, Guid commandGuid, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            BaseCommand(commandGuid, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BaseCommand(NetworkRunner runner, Guid commandGuid, Guid parentGuid = default, int childCount = 0)
        {
            BaseCommand(commandGuid, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeFull(NetworkRunner runner, NetworkedStroke strokeData, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            BrushStrokeFull(strokeData, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeFull(NetworkRunner runner, NetworkedStroke strokeData, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {
            BrushStrokeFull(strokeData, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeBegin(NetworkRunner runner, Guid id, NetworkedStroke strokeData, int finalLength, [RpcTarget] PlayerRef targetPlayer = default)
        {
            BrushStrokeBegin(id, strokeData, finalLength);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeBegin(NetworkRunner runner, Guid id, NetworkedStroke strokeData, int finalLength)
        {
            BrushStrokeBegin(id, strokeData, finalLength);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeContinue(NetworkRunner runner, Guid id, int offset, NetworkedControlPoint[] controlPoints, bool[] dropPoints, [RpcTarget] PlayerRef targetPlayer = default)
        {
            BrushStrokeContinue(id, offset, controlPoints, dropPoints);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeContinue(NetworkRunner runner, Guid id, int offset, NetworkedControlPoint[] controlPoints, bool[] dropPoints)
        {
            BrushStrokeContinue(id, offset, controlPoints, dropPoints);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeComplete(NetworkRunner runner, Guid id, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            BrushStrokeComplete(id, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_BrushStrokeComplete(NetworkRunner runner, Guid id, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {
            BrushStrokeComplete(id, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_DeleteStroke(NetworkRunner runner, int seed, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            DeleteStroke(seed, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_DeleteStroke(NetworkRunner runner, int seed, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {
            DeleteStroke(seed, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_SwitchEnvironment(NetworkRunner runner, Guid environmentGuid, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0, [RpcTarget] PlayerRef targetPlayer = default)
        {
            SwitchEnvironment(environmentGuid, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        private static void RPC_SwitchEnvironment(NetworkRunner runner, Guid environmentGuid, Guid commandGuid, int timestamp, Guid parentGuid = default, int childCount = 0)
        {
            SwitchEnvironment(environmentGuid, commandGuid, timestamp, parentGuid, childCount);
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_CheckCommand(NetworkRunner runner, Guid commandGuid, PlayerRef initiatorPlayer, [RpcTarget] PlayerRef targetPlayer)
        {
            bool isCommandInStack = CheckifCommandGuidIsInStack(commandGuid);
            RPC_Confirm(runner, commandGuid, isCommandInStack, initiatorPlayer);
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_CheckStroke(NetworkRunner runner, Guid strokeGuid, PlayerRef initiatorPlayer, [RpcTarget] PlayerRef targetPlayer)
        {
            bool isCommandInStack = CheckifStrokeGuidIsInMemory(strokeGuid);
            RPC_Confirm(runner, strokeGuid, isCommandInStack, initiatorPlayer);
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_Confirm(NetworkRunner runner, Guid commandGuid, bool isCommandInStack, [RpcTarget] PlayerRef targetPlayer)
        {
            if (m_acknowledgments.TryGetValue(commandGuid, out var tcs))
            {
                tcs.SetResult(isCommandInStack);
                m_acknowledgments.Remove(commandGuid);
            }
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_TransferRoomOwnership
            (NetworkRunner runner,
             [RpcTarget] PlayerRef targetPlayer,
             NetworkPlayerSettings[] playerSettings,
             NetworkRoomSettings roomData)
        {
            TransferRoomOwnership(playerSettings,roomData);
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_SetUserViewOnlyMode(NetworkRunner runner, bool value, [RpcTarget] PlayerRef targetPlayer)
        {
            SetViewOnly(value);
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_DisconnectRemoteUser(NetworkRunner runner,[RpcTarget] PlayerRef targetPlayer)
        {
            MultiplayerManager.m_Instance.Disconnect();
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_MutePlayer(NetworkRunner runner, bool mute, int playerId)
        {
            if (MultiplayerAudioSourcesManager.m_Instance != null)
            {
                MultiplayerAudioSourcesManager.m_Instance.SetMuteForPlayer(playerId, mute);
            }
        }

        // --- Colocation signalling (OpenBrush.Multiplayer.Colocation.ISignallingService) ---
        // Broadcast rather than targeted where the receiving ColocationManager already
        // self-filters by role/state (host-ready, join-request, localization-result) - only
        // the anchor UUID needs a real target, since leaking it to the wrong peer is the
        // specific issue COLOCATION_MVP_PLAN.md flags with Andy's original broadcast RPC.
        //
        // (2026-07-28: these were temporarily disabled via #if false to test a suspected
        // Fusion RPC-weaver regression against a basic-multiplayer-instability report.
        // Reinstated after the same instability was still reproduced with these disabled,
        // ruling that hypothesis out.)

        // Each receiver logs explicitly when PhotonColocationSignalling.m_Instance is null,
        // rather than letting "?." silently swallow the RPC - that's exactly the failure mode
        // this logging pass was added to rule in/out (see PhotonColocationSignalling.cs).
        [Rpc(InvokeLocal = false)]
        public static void RPC_ColocationHostReady(NetworkRunner runner)
        {
            if (PhotonColocationSignalling.m_Instance == null)
            {
                Debug.LogError("[Colocation] RPC_ColocationHostReady arrived but PhotonColocationSignalling.m_Instance is null - dropped.");
                return;
            }
            PhotonColocationSignalling.m_Instance.RaiseHostReadyReceived();
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_ColocationRequestToJoin(NetworkRunner runner, ulong metaUserId)
        {
            if (PhotonColocationSignalling.m_Instance == null)
            {
                Debug.LogError($"[Colocation] RPC_ColocationRequestToJoin arrived (Meta user {metaUserId}) but PhotonColocationSignalling.m_Instance is null - dropped.");
                return;
            }
            PhotonColocationSignalling.m_Instance.RaiseJoinRequestReceived(metaUserId);
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_ColocationAnchorUuid(NetworkRunner runner, string anchorUuid, [RpcTarget] PlayerRef targetPlayer)
        {
            if (PhotonColocationSignalling.m_Instance == null)
            {
                Debug.LogError("[Colocation] RPC_ColocationAnchorUuid arrived but PhotonColocationSignalling.m_Instance is null - dropped.");
                return;
            }
            PhotonColocationSignalling.m_Instance.RaiseAnchorUuidReceived(anchorUuid);
        }

        [Rpc(InvokeLocal = false)]
        public static void RPC_ColocationLocalizationResult(NetworkRunner runner, bool success, string failureReason, RpcInfo info = default)
        {
            if (PhotonColocationSignalling.m_Instance == null)
            {
                Debug.LogError($"[Colocation] RPC_ColocationLocalizationResult arrived (success={success}) but PhotonColocationSignalling.m_Instance is null - dropped.");
                return;
            }
            PhotonColocationSignalling.m_Instance.RaiseLocalizationResultReceived(info.Source, success, failureReason);
        }

        #endregion
    }
}

#endif // FUSION_WEAVER
