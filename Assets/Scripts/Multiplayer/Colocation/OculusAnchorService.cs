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

#if OCULUS_SUPPORTED

using System.Collections.Generic;
using System.Threading.Tasks;
using TiltBrush;

namespace OpenBrush.Multiplayer.Colocation
{
    // Real IAnchorService implementation, delegating to the existing
    // Assets/OculusMR/SpatialAnchorManager.cs (not reimplementing anchor logic - that class
    // already has working create/save/load/bind/share operations, this just adapts its
    // shape to the interface ColocationManager depends on).
    //
    // Takes the SpatialAnchorManager instance explicitly via constructor rather than reaching
    // for OculusMRController.m_Instance internally, so this stays easy to construct in tests
    // with a real (or fake) SpatialAnchorManager without needing the whole scene set up.
    public class OculusAnchorService : IAnchorService
    {
        private readonly SpatialAnchorManager m_AnchorManager;

        public OculusAnchorService(SpatialAnchorManager anchorManager)
        {
            m_AnchorManager = anchorManager;
        }

        public string AnchorUuid => m_AnchorManager.AnchorUuid;

        public async Task<bool> CreateAndSaveAnchor()
        {
            return await m_AnchorManager.CreateSpatialAnchor();
        }

        public async Task<bool> ShareAnchorWith(ulong metaUserId)
        {
            return await m_AnchorManager.ShareAnchors(new List<ulong> { metaUserId });
        }

        public async Task<bool> LoadAndBindRemoteAnchor(string anchorUuid)
        {
            // SyncToRemoteAnchor also localizes the scene to the anchor once bound, so joiners
            // don't need a separate LocalizeSceneToAnchor() call - only the host does (see
            // ColocationManager, which localizes to its own freshly-created anchor directly).
            //
            // Must specify Cloud explicitly - the default is Local, which only ever finds
            // anchors created on this same device. The host shared this anchor via
            // ShareAnchors() (cloud-based Shared Spatial Anchors), so a joiner on a different
            // physical device can only find it in Cloud storage. Confirmed against Andy
            // Baker's own archived OculusMRController.RemoteSyncToAnchor, which passed Cloud
            // explicitly for exactly this reason - dropping it here caused joiners to hang
            // indefinitely in WaitingForAnchor (2026-07-28, confirmed on-device).
            return await m_AnchorManager.SyncToRemoteAnchor(anchorUuid, OVRSpace.StorageLocation.Cloud);
        }

        public bool LocalizeSceneToAnchor()
        {
            return m_AnchorManager.SceneLocalizeToAnchor();
        }
    }
}

#endif // OCULUS_SUPPORTED
