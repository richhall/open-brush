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
using OpenBrush.Multiplayer;
using OpenBrush.Multiplayer.Colocation;

namespace TiltBrush
{
    // Owns the single ColocationManager for the app process, wiring the real Photon
    // signalling and Oculus anchor services together. Single entry point for both the
    // future custom UI (COLOCATION_MVP_PLAN.md Phase 3, not yet built) and the
    // colocation.host/colocation.join API commands in
    // Assets/Scripts/API/ApiMethods.Multiplayer.cs.
    //
    // Mirrors Andy Baker's own archived ColocationBootstrap (attic/meta-colocation branch) as
    // closely as possible: a scene-resident MonoBehaviour that lazily instantiates the
    // OculusMR prefab itself, entirely inside the OCULUS_SUPPORTED guard. That matters beyond
    // style - Assets/OculusMR/OculusMR.prefab also carries Meta's own OVRSceneManager
    // component, which has unconditional Awake()/Start()/OnEnable() methods calling straight
    // into OVRPlugin. Those aren't gated by Open Brush's own OCULUS_SUPPORTED define (Meta's
    // package code has no idea it exists), and #if guards only control which code compiles -
    // they never stop a GameObject placed directly in a scene from being instantiated. So the
    // prefab must only ever be instantiated from inside this guard, never placed statically in
    // Main.unity, or every other headset build would spin up OVRSceneManager and hit it too.
    public class ColocationBootstrap : MonoBehaviour
    {
        private static ColocationBootstrap m_Instance;

        [SerializeField] private GameObject m_OculusMrPrefab;

        public static ColocationManager Instance { get; private set; }

        public static bool IsSupported
        {
            get
            {
#if OCULUS_SUPPORTED && MP_PHOTON
                return m_Instance != null && OculusMRController.m_Instance != null;
#else
                return false;
#endif
            }
        }

        private void Awake()
        {
            m_Instance = this;

#if OCULUS_SUPPORTED
            if (OculusMRController.m_Instance == null)
            {
                if (m_OculusMrPrefab == null)
                {
                    ColocationLog.Error("Oculus MR prefab is not assigned to the ColocationBootstrap.");
                    return;
                }
                Instantiate(m_OculusMrPrefab);
            }
#endif
        }

        public static bool Host(ulong myMetaUserId)
        {
#if OCULUS_SUPPORTED && MP_PHOTON
            if (!EnsureManager()) return false;
            _ = Instance.Host(myMetaUserId);
            return true;
#else
            Debug.LogWarning("[Colocation] Host requested on a build without Android colocation support.");
            return false;
#endif
        }

        public static bool Join(ulong myMetaUserId)
        {
#if OCULUS_SUPPORTED && MP_PHOTON
            if (!EnsureManager()) return false;
            _ = Instance.Join(myMetaUserId);
            return true;
#else
            Debug.LogWarning("[Colocation] Join requested on a build without Android colocation support.");
            return false;
#endif
        }

        public static void Leave()
        {
            Instance?.Leave();
        }

#if OCULUS_SUPPORTED && MP_PHOTON
        private static bool EnsureManager()
        {
            if (Instance != null) return true;

            if (!IsSupported)
            {
                ColocationLog.Error("Colocation bootstrap is not ready - OculusMRController is not present yet.");
                return false;
            }

            var runner = MultiplayerManager.m_Instance != null ? MultiplayerManager.m_Instance.Runner : null;
            if (runner == null)
            {
                ColocationLog.Error("No active Photon NetworkRunner - join a multiplayer room before starting colocation.");
                return false;
            }

            var anchorService = new OculusAnchorService(OculusMRController.m_Instance.m_SpatialAnchorManager);
            var signalling = new PhotonColocationSignalling(runner);
            Instance = new ColocationManager(anchorService, signalling);
            return true;
        }
#endif
    }
}
