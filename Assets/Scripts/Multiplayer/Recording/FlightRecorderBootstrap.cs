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
#if MP_PHOTON
using OpenBrush.Multiplayer;
#endif // MP_PHOTON

namespace OpenBrush.Multiplayer.Recording
{
    // Persistent host for the flight recorder's per-frame/lifecycle hooks. Deliberately NOT
    // PhotonRPC (which previously hosted these) - PhotonRPC is added as a component on the
    // NetworkRunner's GameObject on connect and destroyed with it on disconnect
    // (PhotonManager.Disconnect() calls GameObject.Destroy(m_Runner.gameObject)), so replay
    // ticking (and, previously, stroke-command processing) would silently stop the moment you
    // left a multiplayer room. This self-bootstraps via RuntimeInitializeOnLoadMethod so it
    // needs no scene/Editor wiring and survives scene loads and connect/disconnect cycles.
    public class FlightRecorderBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            var go = new GameObject("FlightRecorderBootstrap");
            go.AddComponent<FlightRecorderBootstrap>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
#if MP_PHOTON
            // Drains PhotonRPC's pending-command queue independent of whether a live PhotonRPC
            // instance exists - needed so strokes replay creates via PhotonRPC.CreateBrushStroke()
            // actually get committed to the sketch after you've left multiplayer (the queue used
            // to only drain from PhotonRPC.Update(), which stops running on disconnect).
            PhotonRPC.ProcessPendingCommands();
            SessionPlaybackController.m_Instance.Tick();
#endif // MP_PHOTON
        }

        // See SessionRecorder.FlushIfRecording() for why this flushes rather than stops on
        // pause, and why pause (not quit) is the signal that actually matters on Android/Quest.
        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                SessionRecorder.m_Instance.FlushIfRecording();
            }
        }

        private void OnApplicationQuit()
        {
            SessionRecorder.m_Instance.FlushIfRecording();
        }
    }
}
