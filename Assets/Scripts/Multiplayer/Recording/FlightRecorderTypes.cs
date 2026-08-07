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
using UnityEngine;

namespace OpenBrush.Multiplayer.Recording
{
    // Shared NDJSON record shapes, written by SessionRecorder and read back by
    // SessionPlaybackController - kept in one place so the two stay in sync.

    // First-pass parse target: every record has a "type" field, read this before
    // deciding which of the full structs below to parse the same line into.
    [Serializable]
    internal struct RecordTypePeek
    {
        public string type;
    }

    [Serializable]
    internal struct PoseRecord
    {
        public string type;
        public int tickMs;
        public ulong player;
        public Vector3 headPos;
        public Quaternion headRot;
        public Vector3 leftPos;
        public Quaternion leftRot;
        public Vector3 rightPos;
        public Quaternion rightRot;
        public Vector3 toolPos;
        public Quaternion toolRot;
    }

    [Serializable]
    internal struct RecordedControlPoint
    {
        public Vector3 pos;
        public Quaternion orient;
        public float pressure;
        public int tickMs;
    }

    [Serializable]
    internal struct StrokeRecord
    {
        public string type;
        public int tickMs;
        public string commandGuid;
        public int seed;
        public string brushGuid;
        public Color color;
        public float brushSize;
        public float brushScale;
        public RecordedControlPoint[] points;
    }
}
