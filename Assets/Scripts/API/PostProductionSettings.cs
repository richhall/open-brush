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

using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush
{
    // Global + per-layer temporal and spatial offsets for the in-app post-production
    // compositing preview (see POSTPROD_PREVIEW_PLAN.md, stages 2-3). Purely in-memory
    // for now - stage 8 adds persistence into SketchMetadata.
    //
    // Spatial offsets are deliberately never applied to the real canvas/scene transform
    // (see stage 3's plan writeup for why) - they exist only to be composed with the
    // real pose by the compositing camera's own draw path, built in stage 6. Until then
    // this is a pure data layer with no visual effect, same as the temporal offsets.
    public static class PostProductionSettings
    {
        public static int GlobalTimeOffsetMs { get; set; } = 0;

        // Incremented each time a sketch's post-production settings have been restored on
        // load, so a client can wait for a load to finish before reading them back
        // (query.postprod.loadcount).
        public static volatile int LoadCount;
        // Name (no .tilt) of the sketch last loaded, however it was loaded, so the control
        // page can follow loads it didn't make itself (query.postprod.sketch.name).
        public static volatile string LoadedSketchName = "";

        private static readonly Dictionary<int, int> s_LayerTimeOffsetMs = new Dictionary<int, int>();

        public static void SetLayerTimeOffsetMs(int layer, int ms)
        {
            s_LayerTimeOffsetMs[layer] = ms;
        }

        public static int GetLayerTimeOffsetMs(int layer)
        {
            return s_LayerTimeOffsetMs.TryGetValue(layer, out var ms) ? ms : 0;
        }

        // The offset to apply when mapping this layer's sketch time to a real-footage
        // plate/depth frame: the global offset plus this layer's own delta on top of it.
        public static int GetEffectiveTimeOffsetMs(int layer)
        {
            return GlobalTimeOffsetMs + GetLayerTimeOffsetMs(layer);
        }

        // Maps a sketch time to the matching native-frame-rate C006 video frame number,
        // using the GLOBAL time offset only - there is one shared real-footage plate
        // regardless of layer, so per-layer offsets (which affect where THAT layer's own
        // geometry is drawn, once the stage 3/6 per-layer draw path exists) don't apply to
        // choosing which plate frame to show. Sign convention matches
        // postprod.offset.time.global's own doc string ("negative if the tilt is ahead of
        // the video"): videoTimeMs = sketchTimeMs + offsetMs, so a tilt that reads ahead
        // needs a negative offset to pull the mapped video time back down. Verified against
        // the empirical anchor recorded 2026-09-08 (sketch 148,240ms == C006 frame 2950,
        // offset -99073).
        // Source video frame rate, from the clip manifest (postprod.clip.load). 60 is the
        // fallback for sketches saved before clip manifests existed (C006 is 60fps).
        public const double DefaultSourceFps = 60.0;
        public static double SourceFps { get; set; } = DefaultSourceFps;

        public static int SketchTimeToC006Frame(double sketchTimeMs)
        {
            double videoTimeMs = sketchTimeMs + GlobalTimeOffsetMs;
            return Mathf.RoundToInt((float)(videoTimeMs / 1000.0 * SourceFps));
        }

        // Inverse of SketchTimeToC006Frame - lets the user drive the sketch clock FROM
        // a video frame/time instead of the other way round (e.g. "I want to see what
        // was painted at video frame 3888", rather than always scrubbing the tilt and
        // reading off the resulting frame). Same formula solved for sketchTimeMs:
        // sketchTimeMs = videoTimeMs - offsetMs.
        public static double C006FrameToSketchTimeMs(int c006Frame)
        {
            double videoTimeMs = c006Frame / SourceFps * 1000.0;
            return videoTimeMs - GlobalTimeOffsetMs;
        }

        // Video crop start/end, in C006 native frame numbers - which portion of the
        // plate is actually used for the final render (see POSTPROD_PREVIEW_PLAN.md's
        // "known issues to revisit"). Null means "use the full available range" -
        // deliberately not defaulted to the depth bake's own first/last frame, since
        // that range was chosen for ingestion convenience, not necessarily the exact
        // performance bounds.
        public static int? CropStartFrame { get; set; }
        public static int? CropEndFrame { get; set; }

        public static TrTransform GlobalSpatialOffset { get; set; } = TrTransform.identity;

        // The global offset as last loaded from or saved to the sketch, so experimental
        // nudges can be reverted (postprod.offset.spatial.revert).
        public static TrTransform SavedGlobalSpatialOffset { get; set; } = TrTransform.identity;

        private static readonly Dictionary<int, TrTransform> s_LayerSpatialOffset =
            new Dictionary<int, TrTransform>();

        public static void SetLayerSpatialOffset(int layer, TrTransform offset)
        {
            s_LayerSpatialOffset[layer] = offset;
        }

        public static TrTransform GetLayerSpatialOffset(int layer)
        {
            return s_LayerSpatialOffset.TryGetValue(layer, out var t) ? t : TrTransform.identity;
        }

        public static void ClearLayerOffsets()
        {
            s_LayerTimeOffsetMs.Clear();
            s_LayerSpatialOffset.Clear();
            s_SavedLayerSpatialOffset.Clear();
        }

        // Per-layer offsets as last loaded from or saved to the sketch, for revert.
        private static readonly Dictionary<int, TrTransform> s_SavedLayerSpatialOffset =
            new Dictionary<int, TrTransform>();

        public static void SetSavedLayerSpatialOffset(int layer, TrTransform offset)
        {
            s_SavedLayerSpatialOffset[layer] = offset;
        }

        public static TrTransform GetSavedLayerSpatialOffset(int layer)
        {
            return s_SavedLayerSpatialOffset.TryGetValue(layer, out var t) ? t : TrTransform.identity;
        }

        // The offset to apply when rendering this layer in the post-production composite:
        // the global spatial offset composed with this layer's own delta on top of it.
        // Mirrors GetEffectiveTimeOffsetMs's shape, but composed (TrTransform multiply)
        // rather than summed, since these are poses, not scalars.
        public static TrTransform GetEffectiveSpatialOffset(int layer)
        {
            return GlobalSpatialOffset * GetLayerSpatialOffset(layer);
        }
    }
}
