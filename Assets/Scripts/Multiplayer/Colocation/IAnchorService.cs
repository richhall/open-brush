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

using System.Threading.Tasks;

namespace OpenBrush.Multiplayer.Colocation
{
    // Abstracts the Meta spatial-anchor operations ColocationManager needs, so the state
    // machine can be unit tested with a fake implementation and no physical headset -
    // per COLOCATION_MVP_PLAN.md section 5.1 ("Abstract Meta anchor operations ... behind
    // small interfaces so protocol tests do not require a headset").
    //
    // OculusAnchorService is the real implementation, wrapping the existing
    // Assets/OculusMR/SpatialAnchorManager.cs.
    public interface IAnchorService
    {
        string AnchorUuid { get; }

        Task<bool> CreateAndSaveAnchor();

        Task<bool> ShareAnchorWith(ulong metaUserId);

        Task<bool> LoadAndBindRemoteAnchor(string anchorUuid);

        bool LocalizeSceneToAnchor();
    }
}
