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

namespace OpenBrush.Multiplayer.Colocation
{
    // [Colocation]-prefixed logging, following the bracketed-prefix convention already used
    // throughout the codebase (e.g. [PhotonManager], [ICOSALOAD], [OB-Shortcuts]).
    //
    // Log state transitions and one-time operation results only - never per-frame - per
    // COLOCATION_MVP_PLAN.md section 0.1. At that frequency this is always-on, no toggle needed.
    //
    // Account() is the one exception: raw Meta account IDs are only useful in development and
    // must not leak into production logging (plan's own privacy note), so those calls are gated
    // behind Unity's Development Build flag rather than logged unconditionally.
    public static class ColocationLog
    {
        private const string Prefix = "[Colocation]";

        public static void Info(string message) => Debug.Log($"{Prefix} {message}");
        public static void Warning(string message) => Debug.LogWarning($"{Prefix} {message}");
        public static void Error(string message) => Debug.LogError($"{Prefix} {message}");

        // For Meta account IDs / other data that shouldn't appear in production logs.
        public static void Account(string message)
        {
            if (Debug.isDebugBuild)
            {
                Debug.Log($"{Prefix} {message}");
            }
        }
    }
}
