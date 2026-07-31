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

namespace OpenBrush.Multiplayer.Colocation
{
    // Abstracts the multiplayer signalling ColocationManager needs to run Andy Baker's
    // host/joiner protocol (COLOCATION_MVP_PLAN.md section 3.1), so the state machine can be
    // unit tested without Photon or a headset. Addressed by Meta user ID rather than any
    // Photon-specific type (e.g. PlayerRef), keeping this interface implementation-agnostic.
    //
    // PhotonColocationSignalling is the real implementation, using targeted (non-broadcast)
    // Fusion RPCs - see Assets/Scripts/Multiplayer/Photon/PhotonRPC.cs's
    // [RpcTarget] PlayerRef pattern, which this deliberately follows rather than the plan's
    // original broadcast-based UUID RPC (flagged there as a release blocker).
    //
    // Protocol (extended from Andy's 2-person version to N joiners - one host, any number of
    // joiners, each addressed individually rather than via a single fixed joiner):
    //   1. Host: AnnounceHostReady() once anchor is created and cloud-saved.
    //   2. Joiner: RequestToJoin(myMetaUserId) once their own Meta user ID is known.
    //   3. Host: (on JoinRequestReceived) shares the anchor to that specific joiner via
    //      IAnchorService, then SendAnchorUuid(thatJoinerId, uuid).
    //   4. Joiner: (on AnchorUuidReceived) loads/binds/localizes via IAnchorService, then
    //      SendLocalizationResult(success, failureReason) - replies to whichever host sent the
    //      UUID; the concrete implementation resolves this from the received RPC's sender
    //      rather than the coordinator needing to track a host ID itself.
    public interface ISignallingService
    {
        void AnnounceHostReady();
        void RequestToJoin(ulong myMetaUserId);
        void SendAnchorUuid(ulong targetMetaUserId, string anchorUuid);
        void SendLocalizationResult(bool success, string failureReason);

        event Action HostReadyReceived;
        event Action<ulong> JoinRequestReceived;
        event Action<string> AnchorUuidReceived;
        event Action<ulong, bool, string> LocalizationResultReceived;
    }
}
