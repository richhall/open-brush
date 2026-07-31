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

#if MP_PHOTON

using System;
using System.Linq;
using Fusion;
using OpenBrush.Multiplayer.Colocation;

namespace OpenBrush.Multiplayer
{
    // Real ISignallingService implementation, using the RPC_Colocation* methods added to
    // PhotonRPC.cs (targeted only for the anchor UUID, per Andy's plan's own flagged concern -
    // see the comment there for why the others are safe as broadcasts).
    //
    // Fusion RPC receivers must be static (see PhotonRPC.cs's own convention), but
    // ColocationManager depends on an instance-based ISignallingService with real events - so
    // this keeps a static m_Instance, mirroring the MultiplayerManager.m_Instance /
    // OculusMRController.m_Instance singleton convention already used throughout this
    // codebase, purely so PhotonRPC's static receivers have a live instance to raise events on.
    public class PhotonColocationSignalling : ISignallingService
    {
        public static PhotonColocationSignalling m_Instance;

        private readonly NetworkRunner m_Runner;

        public event Action HostReadyReceived;
        public event Action<ulong> JoinRequestReceived;
        public event Action<string> AnchorUuidReceived;
        public event Action<ulong, bool, string> LocalizationResultReceived;

        public PhotonColocationSignalling(NetworkRunner runner)
        {
            m_Runner = runner;
            m_Instance = this;
        }

        // TEMP-VERBOSE (2026-07-31): every send/receive below logs on both success and
        // failure via ColocationLog, specifically so send-vs-receive-vs-dropped can be told
        // apart on-device via adb logcat - there was previously no way to tell whether a
        // signalling call actually left the device, arrived, or was silently swallowed by a
        // null PhotonColocationSignalling.m_Instance on the receiving end. All of this goes
        // through ColocationLog (not raw Debug.Log) so the planned UI refactor - routing
        // colocation status onto the same in-VR controller-back log the other Fusion messages
        // already use - only has to change ColocationLog itself, not every call site here.

        public void AnnounceHostReady()
        {
            PhotonRPC.RPC_ColocationHostReady(m_Runner);
            ColocationLog.Info("AnnounceHostReady sent.");
        }

        public void RequestToJoin(ulong myMetaUserId)
        {
            PhotonRPC.RPC_ColocationRequestToJoin(m_Runner, myMetaUserId);
            ColocationLog.Account($"RequestToJoin sent (Meta user {myMetaUserId}).");
        }

        public void SendAnchorUuid(ulong targetMetaUserId, string anchorUuid)
        {
            PlayerRef? targetPlayer = FindPlayerRefForMetaUserId(targetMetaUserId);
            if (targetPlayer == null)
            {
                ColocationLog.Error($"Could not resolve a Photon player for Meta user {targetMetaUserId} - anchor UUID not sent.");
                return;
            }
            PhotonRPC.RPC_ColocationAnchorUuid(m_Runner, anchorUuid, targetPlayer.Value);
            ColocationLog.Info($"SendAnchorUuid sent to Photon player {targetPlayer.Value}.");
        }

        public void SendLocalizationResult(bool success, string failureReason)
        {
            PhotonRPC.RPC_ColocationLocalizationResult(m_Runner, success, failureReason ?? string.Empty);
            ColocationLog.Info($"SendLocalizationResult sent (success={success}).");
        }

        // --- Called by PhotonRPC's static RPC_Colocation* receivers ---

        public void RaiseHostReadyReceived()
        {
            ColocationLog.Info("HostReadyReceived arrived.");
            HostReadyReceived?.Invoke();
        }

        public void RaiseJoinRequestReceived(ulong metaUserId)
        {
            ColocationLog.Account($"JoinRequestReceived arrived (Meta user {metaUserId}).");
            JoinRequestReceived?.Invoke(metaUserId);
        }

        public void RaiseAnchorUuidReceived(string anchorUuid)
        {
            ColocationLog.Info("AnchorUuidReceived arrived.");
            AnchorUuidReceived?.Invoke(anchorUuid);
        }

        public void RaiseLocalizationResultReceived(PlayerRef sender, bool success, string failureReason)
        {
            ulong senderMetaUserId = FindMetaUserIdForPlayerRef(sender);
            ColocationLog.Info($"LocalizationResultReceived arrived (Meta user {senderMetaUserId}, success={success}).");
            LocalizationResultReceived?.Invoke(senderMetaUserId, success, failureReason);
        }

        // Meta user IDs travel to us via the same per-frame rig data channel
        // MultiplayerManager.cs already reads to detect new players for anchor sharing -
        // there's no separate PlayerRef<->Meta-user-ID directory, so both directions are
        // resolved by scanning currently known remote players.
        private static PlayerRef? FindPlayerRefForMetaUserId(ulong metaUserId)
        {
            var manager = MultiplayerManager.m_Instance;
            if (manager?.m_RemotePlayers == null) return null;

            foreach (var remotePlayer in manager.m_RemotePlayers.List)
            {
                if (remotePlayer.TransientData == null || !remotePlayer.TransientData.IsSpawned) continue;
                if (remotePlayer.TransientData.ReceiveData().ExtraData.OculusPlayerId == metaUserId)
                {
                    return PlayerRef.FromEncoded(remotePlayer.PlayerId);
                }
            }
            return null;
        }

        private static ulong FindMetaUserIdForPlayerRef(PlayerRef playerRef)
        {
            var manager = MultiplayerManager.m_Instance;
            if (manager?.m_RemotePlayers == null) return 0;

            var remotePlayer = manager.m_RemotePlayers.List
                .FirstOrDefault(p => p.PlayerId == playerRef.RawEncoded);
            if (remotePlayer?.TransientData == null || !remotePlayer.TransientData.IsSpawned) return 0;

            return remotePlayer.TransientData.ReceiveData().ExtraData.OculusPlayerId;
        }
    }
}

#endif // MP_PHOTON
