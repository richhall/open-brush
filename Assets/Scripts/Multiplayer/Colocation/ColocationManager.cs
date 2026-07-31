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
using System.Threading;
using System.Threading.Tasks;

namespace OpenBrush.Multiplayer.Colocation
{
    // Coordinates the colocation state machine (COLOCATION_MVP_PLAN.md section 1.1) via the
    // two abstracted services, so this class itself has no direct dependency on
    // OVRSpatialAnchor or Photon and can be unit tested with fake IAnchorService /
    // ISignallingService implementations - no headset required (plan section 5.1).
    //
    // Extends Andy's original single-host/single-joiner protocol to N joiners: one explicit
    // host (created via Host()), any number of joiners (each via their own Join() call on
    // their own device), addressed individually rather than assuming exactly one joiner.
    // No host migration, matching the plan's explicit MVP choice - if the host leaves,
    // existing colocated joiners keep their local state, but no new joins are possible until
    // someone calls Host() again.
    public class ColocationManager
    {
        private readonly IAnchorService m_AnchorService;
        private readonly ISignallingService m_Signalling;

        // Serializes ShareAnchorWith() calls so concurrent join requests from multiple joiners
        // can't overlap - Andy's plan explicitly flags "anchor sharing can be started
        // repeatedly or overlap" as a current release blocker in the 2-person implementation;
        // this fixes it as part of doing N-way properly, since overlap becomes far more likely
        // once more than one joiner can arrive at once.
        private readonly SemaphoreSlim m_ShareLock = new(1, 1);

        private readonly HashSet<ulong> m_ColocatedJoiners = new();

        public ColocationState State { get; private set; } = ColocationState.Idle;
        public ColocationRole Role { get; private set; } = ColocationRole.None;
        public string LastFailureReason { get; private set; }
        public IReadOnlyCollection<ulong> ColocatedJoiners => m_ColocatedJoiners;

        public event Action<ColocationState> StateChanged;

        public ColocationManager(IAnchorService anchorService, ISignallingService signalling)
        {
            m_AnchorService = anchorService;
            m_Signalling = signalling;

            m_Signalling.JoinRequestReceived += OnJoinRequestReceived;
            m_Signalling.AnchorUuidReceived += OnAnchorUuidReceived;
            m_Signalling.LocalizationResultReceived += OnLocalizationResultReceived;
        }

        public async Task<bool> Host(ulong myMetaUserId)
        {
            if (State != ColocationState.Idle)
            {
                ColocationLog.Warning($"Host() called while in state {State}, ignoring.");
                return false;
            }

            Role = ColocationRole.Host;
            m_ColocatedJoiners.Clear();

            SetState(ColocationState.CreatingAnchor);
            bool created = await m_AnchorService.CreateAndSaveAnchor();
            if (!created)
            {
                Fail("Failed to create/save anchor.");
                return false;
            }

            SetState(ColocationState.Localizing);
            bool localized = m_AnchorService.LocalizeSceneToAnchor();
            if (!localized)
            {
                Fail("Failed to localize scene to own anchor.");
                return false;
            }

            SetState(ColocationState.Active);
            m_Signalling.AnnounceHostReady();
            ColocationLog.Account($"Host active (Meta user {myMetaUserId}), anchor ready, waiting for joiners.");
            return true;
        }

        // Sends the join request and returns once that request has been sent - not once
        // localization has actually completed. Callers should watch StateChanged (or poll
        // State) for the eventual transition to Active or Failed, since the rest of the
        // handshake happens asynchronously via signalling callbacks.
        public Task<bool> Join(ulong myMetaUserId)
        {
            if (State != ColocationState.Idle)
            {
                ColocationLog.Warning($"Join() called while in state {State}, ignoring.");
                return Task.FromResult(false);
            }

            Role = ColocationRole.Joiner;

            SetState(ColocationState.WaitingForPeer);
            m_Signalling.RequestToJoin(myMetaUserId);
            ColocationLog.Account($"Requested to join as Meta user {myMetaUserId}.");
            return Task.FromResult(true);
        }

        public void Leave()
        {
            SetState(ColocationState.Leaving);
            Role = ColocationRole.None;
            m_ColocatedJoiners.Clear();
            SetState(ColocationState.Idle);
            ColocationLog.Info("Left colocation session.");
        }

        // --- Host-side signalling callbacks ---

        private async void OnJoinRequestReceived(ulong joinerMetaUserId)
        {
            if (Role != ColocationRole.Host || State != ColocationState.Active)
            {
                ColocationLog.Warning($"Ignoring join request from a joiner - not an active host (role={Role}, state={State}).");
                return;
            }

            ColocationLog.Account($"Join request received, sharing anchor.");

            await m_ShareLock.WaitAsync();
            bool shared;
            try
            {
                shared = await m_AnchorService.ShareAnchorWith(joinerMetaUserId);
            }
            finally
            {
                m_ShareLock.Release();
            }

            if (!shared)
            {
                ColocationLog.Error("Failed to share anchor with a joiner.");
                return;
            }

            m_Signalling.SendAnchorUuid(joinerMetaUserId, m_AnchorService.AnchorUuid);
        }

        private void OnLocalizationResultReceived(ulong joinerMetaUserId, bool success, string failureReason)
        {
            if (Role != ColocationRole.Host) return;

            if (success)
            {
                m_ColocatedJoiners.Add(joinerMetaUserId);
                // +1 to include the host themselves in the reported headcount.
                ColocationLog.Info($"A joiner localized successfully. Colocated count: {m_ColocatedJoiners.Count + 1}.");
            }
            else
            {
                ColocationLog.Error($"A joiner failed to localize: {failureReason}");
            }
        }

        // --- Joiner-side signalling callbacks ---

        private async void OnAnchorUuidReceived(string anchorUuid)
        {
            if (Role != ColocationRole.Joiner || State != ColocationState.WaitingForPeer)
            {
                ColocationLog.Warning($"Received anchor UUID in unexpected state (role={Role}, state={State}), ignoring.");
                return;
            }

            SetState(ColocationState.WaitingForAnchor);
            bool loaded = await m_AnchorService.LoadAndBindRemoteAnchor(anchorUuid);

            if (!loaded)
            {
                m_Signalling.SendLocalizationResult(false, "Failed to load/bind/localize remote anchor.");
                Fail("Failed to load/bind/localize remote anchor.");
                return;
            }

            SetState(ColocationState.Active);
            m_Signalling.SendLocalizationResult(true, null);
            ColocationLog.Info("Joiner active, localized to host's anchor.");
        }

        private void SetState(ColocationState newState)
        {
            if (State == newState) return;
            ColocationLog.Info($"State: {State} -> {newState}");
            State = newState;
            StateChanged?.Invoke(newState);
        }

        private void Fail(string reason)
        {
            LastFailureReason = reason;
            ColocationLog.Error(reason);
            SetState(ColocationState.Failed);
        }
    }
}
