# In-app post-production compositing preview for Open Brush

## Context

Alison Goodyear's dance performance piece composites Open Brush stroke replays over
real BlackMagic footage of three VR dancers, using depth so real dancers occlude CG
strokes correctly. That compositing currently happens entirely outside Open Brush, in
`D:\Alison-Goodyear-Postproduction\scripts\make_composite_video.py`, driven by
`render_sequence.ps1` repeatedly hitting the Open Brush Remote API over HTTP. The
process works (a 98-second, 5,867-frame sequence at 4096x2160 with metric depth has
been rendered end to end) but has no interactive feedback loop: temporal misalignment
(a per-dancer delay of a few frames) and spatial misalignment (dancers' strokes don't
line up with their hands in the video) can currently only be diagnosed by running a
multi-hour render and looking at the result.

The `feature/mp-stroke-streaming` branch
(`D:\MSI-Github\open-brush-mp-stroke-streaming`) already added the calibrated camera,
depth/normals capture, and timeline scrubbing this depends on (see
`CALIBRATED_CAMERA_CAPTURE.md` there). This plan is the next step described in that
document's "Next steps" section: move a live, scrubbable, depth-aware compositing
preview into Open Brush itself, so alignment can be checked and corrected
interactively, and so the corrected result can be rendered from inside the app rather
than only externally in Python.

The originating design brief for this work is `OpenBrushPostProductionAPIBrief.md`,
alongside this file.

Two things shape the whole plan:
- **Each Windows Il2cpp build takes ~30 minutes.** Nearly everything here is testable
  in Unity Editor Play Mode against the existing Remote API, so the plan is staged to
  spend builds only at real checkpoints, not per change.
- **The existing Python pipeline is not being retired.** It stays in place as the
  team's QA reference. What's being built here is a second, in-app path to the same
  kind of output — sharing the same compositing formula so the preview is provably
  what a render will produce, not an approximation of it.

## Recommended approach

Ten stages, each independently testable. Stages 1-9 are Editor-only (no build needed).
A single Il2cpp build is worth taking after stage 9, to confirm parity with the
standalone player before a real production render is attempted.

### Stage 1 — Fix per-layer flattening on `.tilt` playback

Root cause found: `ScenePlaybackByTimeLayered` (used by `sketch.scrub`/
`sketch.currenttime`) captures one canvas in its constructor and stamps it onto every
stroke, discarding the correctly-parsed per-stroke `m_IntendedCanvas`
([SketchWriter.cs:539](Assets/Scripts/Save/SketchWriter.cs#L539)).
The sibling class already does this right:
[ScenePlaybackByDistance.cs:146](Assets/Scripts/Playback/ScenePlaybackByDistance.cs#L146)
uses `stroke.m_IntendedCanvas ?? targetCanvas`. Port that fallback into
[ScenePlaybackByTime.cs:278-280](Assets/Scripts/Playback/ScenePlaybackByTime.cs#L278).

This unblocks everything downstream that depends on per-dancer layers surviving load
(show/hide, per-layer temporal offset, per-layer spatial offset).

**Test**: load a multi-dancer `.tilt` in Editor, `layer.show`/`layer.hide` each layer
via the Remote API mid-scrub, confirm strokes stay separated.

### Stage 2 — Generalized temporal offset (global + per-layer)

Replace the render script's hardcoded constants
(`FIRST_VIDEO_FRAME`, `DEPTH_START_ORIG`, stride) with a live, API-settable mapping
from sketch time to plate frame index, plus a per-layer delta on top of a global
value (negative allowed, per the brief — the tilt may be ahead of the video).

New endpoints, following the existing `[ApiEndpoint(...)]` pattern in
`Assets/Scripts/API/ApiMethods.GlobalCommands.cs` (see `sketch.currenttime` at
[:185-202](Assets/Scripts/API/ApiMethods.GlobalCommands.cs#L185)
for the convention to match):
- `postprod.offset.time.global=<ms>`
- `postprod.offset.time.layer=<index>,<ms>`

**Test**: Editor, set offsets via the API, confirm the computed plate/depth frame index
shifts accordingly (log or `query.`-prefixed readback).

**Status (2026-09-08): global auto-mapping built and wired into the live preview.**
`PostProductionSettings.SketchTimeToC006Frame(sketchTimeMs)` implements
`videoTimeMs = sketchTimeMs + GlobalTimeOffsetMs; frame = round(videoTimeMs/1000*60)`
(sign convention verified against the empirical anchor recorded above: sketch
148,240ms == C006 frame 2950 at offset -99073). `PostProductionPreviewController`
calls this every frame (`UpdateAutoPlate`, hooked into the existing per-frame
`Update()`) and automatically calls `SetPlate` with the resolved color+depth paths
whenever the mapped frame changes - enabled via a new endpoint,
`postprod.preview.autoplate=<clipDir>` (empty disables). Frame resolution/stride-
snapping logic was factored into a new shared `DepthFrameResolver` class rather than
duplicated from `PostProductionMediaLoader.SeekToC006Frame` (kept as two independent
copies deliberately, not merged, to avoid coupling the two otherwise-unrelated
subsystems - see the "still two separate code paths" note above). **Per-layer time
offset is still NOT visually wired** - there is one shared plate regardless of layer,
so `GetEffectiveTimeOffsetMs(layer)` only matters once each layer's own CG geometry
can be drawn at its own corrected time, which needs the same per-layer draw path
stage 3 is waiting on. CG capture deliberately stays a manual
`postprod.preview.refreshcg` call, not auto-refreshed - see stage 6 for the ~1.4s
snapshot cost reasoning. **Inverse mapping added 2026-09-08** (user request):
`PostProductionSettings.C006FrameToSketchTimeMs` solves the same formula for sketch
time given a video frame, exposed via `postprod.preview.videotime`/
`postprod.preview.videoframe` - lets the sketch clock be driven FROM a chosen
real-footage moment instead of only the other direction.

### Stage 3 — Per-layer spatial offset

Apply a `TrTransform` offset to each layer's canvas GameObject (from
`SceneScript.GetOrCreateLayer(int)`,
[SceneScript.cs:323](Assets/Scripts/SceneScript.cs#L323)) —
moving one canvas moves all its strokes together. Per the brief: position (x/z) and
y-rotation are the expected day-to-day controls; full position/rotation/scale should
still be accepted (euler and quaternion input, matching the existing `Quaternion`
parser added at
[ApiEndpointAttribute.cs:120-131](Assets/Scripts/API/ApiEndpointAttribute.cs#L120)),
global value plus a per-layer delta, same shape as stage 2.

**Rendering must not mutate the real canvas transform either** — an earlier version
of this plan proposed live-mutating `canvas.transform` for on-screen feedback and
caching the pre-offset pose separately so save wouldn't capture it. That's fragile:
it means the save path has to know postprod mode is active and substitute a stale
cached value, which would silently discard any other legitimate reason the transform
changed during that session (a layer manually repositioned, multiplayer sync, another
tool/workflow entirely unrelated to this feature). Not an acceptable trade.

Instead: the compositing camera (stage 6) renders each layer's strokes itself, with
an explicit override matrix (`authoredPose * offset`), via `Graphics.DrawMesh`/
`CommandBuffer.DrawMesh` per `Batch`
([BatchPool.cs](Assets/Scripts/Batching/BatchPool.cs) —
strokes are organized as `Batch` objects under a `BatchManager` per canvas, standard
`MeshRenderer`s parented under the canvas transform today), rather than relying on
Unity's default per-camera scene traversal. `canvas.transform` itself — and
`App.Scene.Pose` for the global case — are **never** touched for postprod purposes,
under any circumstances. Consequence: no interception of
`SceneScript.LayerCanvasesSerialized()` or any other save-path code is needed at all;
it stays completely unaware postprod mode exists, because nothing it reads has
changed. Every other workflow that reads or writes the real transform is unaffected.
Cost: this needs a custom draw path for the compositing camera specifically (iterate
each visible layer's batches, draw with the override matrix) rather than letting
Unity's default camera culling handle it — more work than mutating the transform
directly, but it's the only version that doesn't create a new hazard alongside the
one being fixed.

**Do not write this into the existing `LayerMetadata.Transform`.** That field is
`layer.LocalPose` — the canvas's real, authored position, the same field the artist's
own layer-move gestures write to
([SceneScript.cs:445-458](Assets/Scripts/SceneScript.cs#L445),
applied back on load at
[SaveLoadScript.cs:750-768](Assets/Scripts/Save/SaveLoadScript.cs#L750)).
Baking a still-being-tuned calibration correction into it would be indistinguishable
from moving the layer for real, with no way to tell "artist placement" from "our
correction" apart, and no clean way to reset the correction without disturbing the
authored position. Instead, add a new, separate field — a `PostProdOffset`
`TrTransform` alongside `Transform` on `LayerMetadata` — and compose it with the
existing `LocalPose` at preview/render time (`effective pose = LocalPose *
PostProdOffset`) rather than overwriting `LocalPose` itself. See stage 8 for where
this is persisted.

**Status (2026-09-08): now persisted (stage 8), still not rendered.** Global/per-layer
spatial offsets survive save/reload (`SketchMetadata.PostProduction.SpatialOffset` /
`LayerMetadata.PostProdOffset`) and the web page (stage 7) can set them, but they
remain a pure data layer visually - still nothing renders the override matrix,
unchanged from the note below.

**Test**: like stage 2, this is a data-layer-only step with no visual output yet —
nothing renders the override matrix until stage 6's compositing camera exists. Editor,
set global and per-layer spatial offsets via the API, confirm via the
`query.postprod.offset.spatial.*` readback that they compose correctly (global alone
passes through untouched layers unchanged; a layer's own offset composes with the
current global value, and recomposes live if the global value changes afterward). The
visual check — nudge a layer's offset and see only that dancer's strokes move — belongs
at stage 6, once there's an actual render path to see it through.

### Stage 4 — Media ingestion

Two different problems, two different formats — confirmed by inspecting the actual
source file: `Original/A002_06110040_C006.mov` is Apple ProRes 422 (not Blackmagic
RAW), 4096x2160, 10-bit 4:2:2, 60fps (`ffprobe`-verified). Unity's `VideoPlayer` has no
ProRes decode path on Windows (Media Foundation doesn't support it, and the Blackmagic
RAW SDK targets capture cards / native integration, not `VideoPlayer` — this file
isn't even BRAW anyway).

- **Preview plate**: transcode a H.264/H.265 proxy once (external, ffmpeg), play it
  through Unity's `VideoPlayer`, following the existing pattern in
  [ReferencePanelVideoTab.cs](Assets/Scripts/ReferencePanelVideoTab.cs)
  (`widget.VideoController.VideoTexture` assigned onto a material,
  [:169-183](Assets/Scripts/ReferencePanelVideoTab.cs#L169)) and backed by
  [ReferenceVideo.cs](Assets/Scripts/ReferenceVideo.cs).
- **Final-render plate**: full-quality frame image sequence, extracted once from the
  ProRes master via ffmpeg (external step, alongside the existing VDA depth
  precompute) — no video codec dependency inside Open Brush for final quality at all.
- **Real-footage depth (both modes)**: VDA's `production_depths_metric.npy` baked to
  per-frame EXR sidecars (single-channel `Y`, float32, ZIP — the exact variant
  `ScreenshotManager.EncodeDepthCapture` already writes,
  [ScreenshotManager.cs:852-873](Assets/Scripts/ScreenshotManager.cs#L852)).
  No runtime EXR reader currently exists in the repo (write-only today) — add a small
  reader scoped to this exact known variant, not general OpenEXR support.
- **CG colour/depth**: stay live. CG colour renders directly from the calibrated
  camera every frame; CG depth stays in the depth-normals `RenderTexture`
  (`RenderEncodedDepthNormalsToTexture`,
  [ScreenshotManager.cs:509-550](Assets/Scripts/ScreenshotManager.cs#L509))
  and is decoded straight off the GPU — no file round-trip for either preview or the
  in-app final render.
- **Folder layout — decided and set up 2026-09-04.** Real storage lives on D:, one
  subfolder per clip so future shoots don't collide:
  `D:\Alison-Goodyear-Postproduction\OpenBrushMedia\<clip>\` (e.g. `C006`). Open Brush
  sees it as a new sibling category under its existing `Media Library` convention
  (alongside `Videos`, `Images`, `Models` — see `App.MediaLibraryPath()`,
  [App.cs:2293](Assets/Scripts/App.cs#L2293)):
  `C:\Users\rich\OneDrive\Documents\Open Brush\Media Library\PostProd\<clip>\`, an
  NTFS directory junction (`New-Item -ItemType Junction`, no admin rights needed,
  transparent to Unity's own file APIs) pointing at the D: folder — solves the "C:
  doesn't have room" problem from the brief. Junction created and verified
  (write-through-junction, read-via-D: round-trip) for `C006`; the per-clip subfolder
  structure *inside* `C006` (proxy video / plate frame sequence / depth EXR sequence)
  is not yet decided — that's part of actually generating each of those three media
  types, not this folder-setup step.

**Test**: Editor, load a known proxy video + baked depth EXR sequence, confirm frame
`N` displays the right plate and the EXR reader round-trips a known depth value.

**Status (2026-09-04): only two of the three media types have been generated.**
`C006/` currently has `proxy_c006.mp4` (H.264 preview proxy) and `depth_frames/`
(baked real-footage depth EXRs) — both needed for, and sufficient for, the live
in-app *preview* (stages 5-6), which is deliberately proxy-resolution. The
**full-quality frame image sequence extracted from the ProRes master**, called for
above for the *final-render* plate, has not been generated yet. Not a blocker for
anything done so far, but must happen before stage 9 (in-app final render) — that
stage needs to composite at full 4096x2160 against real frames, not an upscaled
proxy. Flagged here so it isn't rediscovered as a surprise later.

### Stage 5 — The compositor (shared by preview and final render)

One piece of code, not two. Implements exactly the formula already proven in
`make_composite_video.py:100-105`:

```
visible = cgDepth > 0 && cgDepth < plateDepth   // strict, no epsilon, no median filter
alpha   = max(cg.rgb) * visible                  // brightness-derived, gated by visible
out     = cg * alpha + plate * (1 - alpha)
```

Written once in HLSL, structured as a `Graphics.Blit`-style pass following the
existing stencil-mask compositing chain in
[RenderWrapper.cs:473-519](Assets/Scripts/Rendering/RenderWrapper.cs#L473)
(Built-in Render Pipeline — confirmed via `GraphicsSettings.asset:47`, no SRP asset
assigned). Takes four inputs (CG colour, CG depth, plate colour, plate depth) and is
driven identically by both the live preview (screen resolution, once per `Update()`)
and the eventual batch render (full resolution, looped over every frame) — see stage
9. A "naive" mode (plate behind live CG, no depth test) is the same shader with the
depth test short-circuited, for quick alignment checks without needing depth loaded.

**Test**: Editor, feed a known frame's four inputs, compare the composite's occlusion
boundary against the equivalent frame from the existing Python output as a regression
check.

### Stage 6 — Display: reuse the camera that already paints the window

No new in-app UI, per the brief — controls come from a web page, results appear in
Open Brush's own window.

- `App.VrSdk.GetVrCamera()`
  ([VrSdk.cs:311](Assets/Scripts/VrSdk.cs#L311))
  is the camera already painting the desktop window in NoHeadsetMode — confirmed
  because that's exactly the GameObject `MonoCameraControlScript` is attached to at
  [VrSdk.cs:192](Assets/Scripts/VrSdk.cs#L192).
  This is the same camera the existing `render_sequence.ps1` workflow already runs
  behind today.
- A new `postprod.preview=1` toggle applies the calibrated intrinsics to this camera
  (reusing `BuildProjectionFromIntrinsics`,
  [ScreenshotManager.cs:1087-1099](Assets/Scripts/ScreenshotManager.cs#L1087),
  the same mechanism `camera.intrinsics` already uses for the capture rig) and enables
  the stage-5 compositing blit on it. `postprod.preview=0` restores normal viewing.
- **This is the only place temporal/spatial offsets are ever visible.** Since stage 3
  applies offsets only via this camera's own override-matrix draw path and never
  touches the real canvas/scene transform, normal editing and any other viewport
  (including looking around in a headset with `postprod.preview` off) always shows
  the sketch's true, unmodified geometry. Toggling preview on is how you look through
  the one camera that reflects the current tuning — off, you're back to the ordinary
  view, unaffected.
- Explicitly **not** the Spectator Camera (`DropCamWidget`) — that's a placeable
  camera *prop* in the 3D scene toggled by `spectator.on/off`
  ([ApiMethods.GlobalCommands.cs:376-386](Assets/Scripts/API/ApiMethods.GlobalCommands.cs#L376)),
  not a "what does the window show" switch — ruled out during design, worth recording
  so it isn't reinvestigated later.

**Test**: Editor, toggle `postprod.preview`, confirm the Game view swaps between
normal scene and live composite. This is also where stage 3's spatial offsets get
their first real visual check (deferred from stage 3 since the override-matrix draw
path lives here): nudge a layer's spatial offset via the API and confirm only that
dancer's strokes move, with the real canvas transform still unaffected everywhere
else.

### Stage 7 — Web control page

`Assets/Resources/ScriptExamples/postproduction.html`, following the exact convention
of the existing `spectator.html` and `remotecontrol.html` (Bootstrap CSS from the same
CDN, synchronous `sendCommand()` doing `GET /api/v1?...`,
[ApiManager.cs:342-352](Assets/Scripts/API/ApiManager.cs#L342)
auto-serves anything dropped in that folder — no new C# needed for the page itself).
Controls: scrub slider + explicit frame/time entry, layer list with show/hide and
temporal/spatial offset fields, naive/depth-aware toggle, preview on/off.

**Test**: Editor, drive the whole preview from the page in a browser.

**Status (2026-09-08): built.** `Assets/Resources/ScriptExamples/postproduction.html`,
served at `http://localhost:40074/examplescripts/postproduction.html` once Play mode
is running (auto-served by the existing `PopulateExampleScripts`/
`BASE_EXAMPLE_SCRIPTS_URL` mechanism - no new C# needed, matching the plan). Covers:
load sketch (`load.realtime` + `sketch.scrub=1`), a scrub slider bound to
`sketch.currenttime`, play/pause (`app.timescale`), the calibrated camera setup in
one button, preview on/off, naive/depth-aware toggle, CG refresh, the new
`postprod.preview.autoplate` enable/disable, global time offset, and per-layer
show/hide + time offset for this project's known layers 0-3. A live status panel
polls the plate/preview/autoplate/cgcapture/composite/offset query endpoints every
second and renders them as plain text - this is what replaces asking Claude to check
status after every action. **Not yet tested against a running Play session** (built
right after a Unity restart) - open the URL above once Play mode is running and
work through the sections in order (1-6) to verify.

### Stage 8 — Persistence

Store inside the `.tilt` itself, in `metadata.json` (`SketchMetadata`,
[SketchMetadata.cs](Assets/Scripts/Save/SketchMetadata.cs)),
not a separate sidecar file — this keeps the offsets bound to the sketch, travelling
with it on copy/rename, and follows the format's own established extension pattern
(20+ additive fields across its version history, every one
`[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]` so older files are
unaffected). `.tilt` is a zip with a small fixed set of named subfiles
([TiltFile.cs:37-41](Assets/Scripts/Save/TiltFile.cs#L37)) —
this approach stays inside the existing `metadata.json` subfile, no changes to
`TiltFile.cs` needed.

- **Per-layer spatial offset**: a new `PostProdOffset` `TrTransform` field on
  `LayerMetadata`, kept separate from the existing authored `Transform` (see stage 3
  for why) — composed with it at preview/render time, never written into it.
- **Per-layer temporal offset**: a new `TimeOffsetMs` field on `LayerMetadata`,
  alongside `PostProdOffset`.
- **Global spatial + temporal offsets**: a new `PostProductionMetadata` block
  directly on `SketchMetadata`, same non-destructive pattern one level up. The global
  spatial value is **not** written into the existing `SceneTransformInRoomSpace`
  field ([SketchMetadata.cs:782](Assets/Scripts/Save/SketchMetadata.cs#L782)) —
  that's `App.Scene.Pose`, set from the ChArUco colocation during the actual capture
  session and applied on load
  ([SaveLoadScript.cs:736-737](Assets/Scripts/Save/SaveLoadScript.cs#L736)),
  so overwriting it would repeat the exact same destructive-conflation problem as
  `LayerMetadata.Transform`, one level up. Instead: `PostProductionMetadata.SpatialOffset`
  (`TrTransform`), composed with `App.Scene.Pose` at preview/render time
  (`effective scene pose = App.Scene.Pose * SpatialOffset`). `PostProductionMetadata.TimeOffsetMs`
  has no existing field to conflict with, since nothing in the format currently
  represents a time offset. Other postprod state (naive/depth-aware mode default,
  media source paths) lives in the same block. All new fields follow the existing
  `NullValueHandling.Ignore` convention used for every other addition to this file.

**Test**: Editor, set offsets, save, reload the sketch, confirm offsets restore.

**Status (2026-09-08): built.** `LayerMetadata.PostProdOffset`/`TimeOffsetMs` and
`SketchMetadata.PostProductionMetadata` (`SpatialOffset`, `TimeOffsetMs`,
`CropStartFrame`, `CropEndFrame`, `AutoPlateClipDir`) added exactly as designed above,
using `ShouldSerializeX()` callbacks (matching the existing
`ShouldSerializeSceneTransformInRoomSpace` idiom in this file) so an ordinary sketch
that never touches this feature gets no `PostProduction` block at all rather than a
spurious empty one. Save side: `SceneScript.LayerCanvasesSerialized()`
(per-layer) and `SketchSnapshot.GetSketchMetadata()`/`GetPostProductionMetadata()`
(global). Load side: `SaveLoadScript.cs`'s existing layer-creation loop (per-layer)
and the block already restoring `App.Scene.Pose` (global) - both reset to defaults
for a sketch with no `PostProduction` block, not left holding whatever a previously
loaded sketch set. **Crop start/end frames added beyond the original stage 8 design**
(user request, 2026-09-08) - `PostProductionSettings.CropStartFrame`/`CropEndFrame`
(nullable ints, C006 native frame numbers), settable via `postprod.crop.start`/
`postprod.crop.end`/`postprod.crop.clear`, persisted the same way. **Not yet
consumed by anything** - stage 9 (final render) doesn't exist yet to honor them, and
the live preview doesn't clamp/flag out-of-crop scrubbing either; purely a data layer
for now, same bootstrapping pattern stages 2/3 started with. **Camera setup also
added to persistence beyond the original design** (user request, 2026-09-08) -
`PostProductionMetadata.CameraIntrinsics`/`CameraClip`/`CameraName`/`CameraPose`,
sourced from new raw getters on `ScreenshotManager` (`CalibratedFx` etc.,
`UseCalibratedProjection`/`UseCalibratedClip`) and `PostProductionPreviewController`
(`TargetCameraName`, `HasTargetPose`, `TargetPosition`/`TargetRotation`) - stored with
no unit conversion, so they round-trip exactly with `camera.intrinsics`/
`camera.clip`/`postprod.preview.camera`/`postprod.preview.pose`. New matching query
endpoints (`query.camera.intrinsics`, `query.camera.clip`,
`query.postprod.preview.camera`, `query.postprod.preview.pose`) return the same
comma-separated format the SET commands take, specifically so the stage-7 page's
`syncSettingsFromServer()` can copy a query result straight into an input field with
no parsing. **Not yet tested** - built but no save/reload round-trip has been run
against a live Play session yet.

**REAL DATA-LOSS INCIDENT, found and fixed 2026-09-08 - read before touching
save.overwrite/save.new via the API on this branch again.** Using the stage-7 page's
original Save button (`save.overwrite`) while `sketch.scrub` was active silently
truncated `Untitled_17.tilt`: `num_strokes` dropped 50 -> 14, control points
collapsed across all three dancer layers, ~76s of painting lost from the end of the
file - confirmed via `parse_tilt_sketch.py` run before and after. User had a backup
and restored it (confirmed full 50 strokes again). Root cause:
`SketchSnapshot.WriteSnapshotToFile` re-derives strokes from
`SketchMemoryScript.AllStrokes()` at save time, which apparently returns a truncated
set while the scrub playback timeline is mid-position - a pre-existing Open Brush
behavior, not something introduced this session, but this session's Save button was
the first thing to actually combine `sketch.scrub` + a real save via the API.
**Fix**: a new metadata-only save path - `PostProductionMetadataSaver.cs` +
`postprod.metadata.save=<filename>` endpoint. Reads the existing `.tilt`'s
`data.sketch` (and thumbnail/hires) as raw bytes and writes them back verbatim,
completely bypassing `AllStrokes()`/`SketchWriter.WriteMemory` - stroke data cannot
be touched by this path no matter what the scrub state is. Only `metadata.json`'s
`PostProduction` block and per-layer `PostProdOffset`/`TimeOffsetMs` actually change.
The stage-7 page's Save button now calls this instead. **`save.overwrite`/`save.new`
via the API should not be used while `sketch.scrub` is active until the underlying
`AllStrokes()` truncation bug is found and fixed in Open Brush's own save code** -
the new endpoint sidesteps the symptom for the postprod workflow specifically, it
doesn't fix the root cause for saves in general.

### Stage 9 — In-app final render, dual output mode

The same stage-5 compositor, driven by a batch loop instead of `Update()`, at full
output resolution. Output sink is pluggable — this already exists in the codebase, not
new infrastructure:
- **Direct video encode** — `FfmpegPipe`
  ([FfmpegPipe.cs](Assets/Scripts/Rendering/FfmpegPipe.cs)),
  the same bundled first-party ffmpeg
  (`Support/ThirdParty/ffmpeg/`) already used for camera-path video recording.
- **Frame-image sequence** — `StillFrameSequenceExporter`
  ([StillFrameSequenceExporter.cs](Assets/Scripts/Rendering/StillFrameSequenceExporter.cs)),
  already the codebase's existing fallback for exactly this "quick path avoids a step,
  pros get frames for grading" choice, selected today via
  `App.UserConfig.Video.ForceFrameSequenceRender`
  ([VideoRecorderUtils.cs:117-144](Assets/Scripts/Rendering/VideoRecorderUtils.cs#L117)).

Add a third sink alongside these two (the composited frame, not a plain screenshot),
following the same selection convention.

**Test**: Editor, render a short (~5s) in-app sequence both ways; spot-check frames
against the equivalent range from the existing Python-rendered output for the same
sketch (won't be bit-identical — different language, different plate/depth precision
path — but should agree by formula).

### Stage 10 — Build checkpoint

One Il2cpp Windows build, after stage 9, to confirm the whole thing (including
`VideoPlayer` playback, the new EXR reader, and `FfmpegPipe`) behaves the same in the
standalone player as in-Editor before attempting a real production render through it.
Delete the whole build output folder first (`D:\MSI-Github\Builds\Oculus_Release_OpenBrush_Il2cpp\`),
not just `Plugins`, per prior build experience on this branch.

## Verification summary

- Stages 1-9: Unity Editor Play Mode against the Remote API (curl/PowerShell or the
  stage-7 web page), no build required.
- Regression check for the compositor specifically (stage 5): compare against a known
  frame from the existing `make_composite_video.py` output.
- Stage 10: one full build, sanity-checked against the same Editor test pass.
- The existing `make_composite_video.py` / `render_sequence.ps1` pipeline stays
  untouched throughout, as the team's independent QA reference.

## Known issues to revisit

- **Video-seek frame precision, found during stage 4 (2026-09-04), not yet
  resolved.** `PostProductionMediaLoader` must leave the proxy `VideoPlayer` playing
  (never `Pause()`) for `.time` seeks to land reliably at all — confirmed necessary
  the hard way (see `project_openbrush_postprod_preview` memory for the full
  debugging trail). But leaving it playing means it keeps drifting forward after
  each seek (measured: requesting frame 5950 and waiting 0.5s of continued playback
  left it at frame 5956, +6 frames over). The real compositor needs the plate frozen
  exactly on the frame matching the CG side's paused/scrubbed state, so a fixed wait
  isn't good enough. Needs: `Pause()` *after* the seek lands (untested whether that
  reintroduces the original bug) plus a frame-precise "has it actually landed"
  detection, rather than the current fixed 30-frame wait. Must be solved before
  stage 5/6's compositor can use this loader for real — revisit before wiring
  `PostProductionMediaLoader` into the live preview.

- **CG capture for the live preview uses the existing snapshot-to-file mechanism
  (`ScreenshotManager.TakeSnapshot`), triggered on demand (e.g. when the scrub
  position changes), rather than a true per-frame GPU capture - decided during
  stage 6 (2026-09-04).** Reuses proven, already-correct code (background-sentinel
  detection, culling mask handling) instead of reimplementing the shader-replacement
  depth pass from scratch, and matches the actual use case (alignment-checking
  while scrubbing, not smooth playback) - a snapshot costs ~1.4s per the plan's own
  measured performance numbers, fine for "update when scrub changes," wasteful for
  "every frame regardless." **Revisit at the end of this dev process**: if the
  preview needs to feel more responsive than on-demand snapshots allow (e.g. for
  smooth scrubbing rather than step-and-settle), reconsider a true live capture
  path then, once the rest of the feature is proven and it's clear whether this is
  actually a real limitation in practice.

- **Camera intrinsics use `fy = fx` rather than the true calibrated `fy`,
  deliberately, for now.** The proven camera recipe (position, quaternion,
  `camera.intrinsics`) sets `fy` equal to `fx` to match Blender's square-pixel
  limitation, inherited from the earlier Blender-comparison validation (IoU 0.80,
  stroke pixel counts within 0.6%). The true calibrated value is
  `fy=3165.0707` (vs. `fx=3163.5317`) - about 0.5px difference at frame edges.
  Deliberately kept matched to fx for consistency with that validated comparison
  while this dev process is ongoing. **Revisit at the end of this dev process**:
  switch to the true calibrated `fy` for genuine production compositing, once
  nothing downstream still depends on Blender-comparability.

- **Full source clip is ingested for now, not just the performance range - noted
  2026-09-04.** `A002_06110040_C006.mov`'s first ~45 seconds are pre-performance
  (slate/clapperboard, setup) - the dance doesn't start until much later (sketch ms
  ~213467, C006 frame ~2959). The existing depth bake and the stage-4 full-res color
  frame extraction both currently cover the *entire* range the depth manifest already
  spans (original frames 2950-8830, stride 2, 2941 frames) rather than being cropped
  to just the performance - deliberate for now, so temporal-offset tuning (stage 2)
  has real frames to work against right from the start of that range while the
  correct offset is still being found, rather than needing to re-ingest every time
  the assumed start point changes. **(b) done 2026-09-08** - see stage 8's status:
  `postprod.crop.start`/`postprod.crop.end` are now first-class, API-settable,
  persisted values (`PostProductionSettings.CropStartFrame`/`CropEndFrame`), not yet
  consumed by any render path since stage 9 doesn't exist yet. **(a) still open**:
  once the performance's start/end points are settled, consider precropping source
  video/depth ingestion to just that range to save space and ingestion time - a
  storage/ingestion optimization, independent of (b)'s render-time control.

- **Frame naming convention reworked - raised 2026-09-04, renamed 2026-09-08.**
  `depth_frames/frame_NNNNN_depth.exr` used to number files by *bake-sequence
  index*, not actual source video frame - `frame_00000_depth.exr` was really
  original C006 frame 2950, decodable only via `manifest.json`'s `origStartFrame`/
  `stride`. Backwards - a filename should be self-describing. **Done**: renamed all
  2941 EXRs so `frame_NNNNN` means actual C006 video frame NNNNN directly
  (`frame_02950_depth.exr` .. `frame_08830_depth.exr`, stride 2, files simply absent
  for frames without baked depth - a sparse directory, not a hidden reindex).
  `manifest.json` updated to `firstFrame`/`lastFrame`/`stride` as descriptive
  metadata only, no longer required to decode a filename.
  `PostProductionMediaLoader.SeekToC006Frame` (`Assets/Scripts/API/
  PostProductionMediaLoader.cs`) updated to match: snaps a requested C006 frame to
  the nearest one actually on disk within `[firstFrame, lastFrame]` at `stride`,
  then builds the path directly from that true frame number (previously computed a
  separate `depthIndex` and built `frame_{depthIndex:D5}_depth.exr`, which would now
  point at nonexistent/wrong files). Deliberately kept depth *sparse* (rename only,
  no VDA recompute) for now - see cost tradeoff logged 2026-09-04 above; the rename
  doubles as a check that the loader is stride-aware rather than assuming a dense
  0..N sequence. **Still open, not done**: any future full-res color frame
  extraction should use this same true-frame-number convention
  (`frame_02950_color.png`) so depth stays a genuine sidecar to the video; and this
  hasn't yet been tested end-to-end in Unity Play Mode against the real API.

- **Out-of-range frame requests and missing-media display - designed and
  implemented 2026-09-08.** Raised by asking what happens if `postprod.plate.seek`
  is given a frame genuinely outside the baked depth range (e.g. frame 60, when
  depth only covers [2950, 8830]) - currently `SeekToC006Frame`'s
  `Mathf.Clamp` would silently snap it to frame 2950 and read that file as if it
  were valid, with only a `c006Frame=60 depthFrame=2950` mismatch buried in the
  status string as the only clue. **Snapping to the nearest available frame is
  correct behavior for the stride case** (e.g. requesting 2951 when only even
  frames exist) but wrong for a genuinely out-of-range request - these need to be
  distinguished, not handled identically.
  - **Status text (the machine-readable "truth", via `query.postprod.plate.status`
    -> `PostProductionMediaLoader.LastStatus`)**: must explicitly say when a
    request is out of range (e.g. "c006Frame=60 depth UNAVAILABLE, valid range
    [2950, 8830]"), not just report a snapped frame number that could be
    misread as normal stride-snapping. **Done**: `SeekToC006Frame` now checks
    `frame` against `[firstFrame, lastFrame]` before snapping - out-of-range sets
    `LastStatus` to an explicit "depth UNAVAILABLE - out of range" message and a
    new `PostProductionMediaLoader.DepthAvailable` flag to false; in-range still
    snaps to the nearest frame on disk exactly as before (stride case unaffected).
    Also wrapped the EXR read itself in a try/catch so a missing/corrupt file
    within the valid range reports clearly instead of throwing.
  - **What the composited preview actually displays (the visual side, separate
    from the status text) - decided design, implemented in
    `PostProductionCompositeImageEffect.cs`.** Color and depth are
    independent resources and can be missing independently:
    - Depth available -> normal depth-aware composite (current/planned stage 5/6
      behavior, unaffected).
    - Depth unavailable but color available (the realistic case, since color
      comes from the dense untrimmed 60fps proxy) -> fall back to a **naive-over**
      composite (CG always drawn in front, no occlusion) **with a visible flag
      baked into the frame** (e.g. a solid magenta border/corner tag - cheap,
      code-only, no font rendering needed) so a viewer scrubbing the live preview,
      or looking at an exported frame later, can tell at a glance that occlusion
      wasn't applied there, without needing to cross-check the API. This reuses a
      precedent already established in the Blender proof-of-concept, which always
      generated naive-over alongside the depth-aware composite for comparison -
      not a new idea, just reapplying it as an automatic fallback.
    - Color unavailable -> a clear **"media offline" placeholder** (flat magenta,
      Unity's own missing-texture convention), never a frozen/stale previous
      frame shown silently.
    - **Core principle: never silently substitute.** No clamp-and-pretend, no
      holding the last good frame without saying so - every degraded case must be
      visually distinguishable from a correct composite.
  - **Implemented**: `EnsurePlateTextures` now tracks `m_HasPlateColor` and
    `m_HasPlateDepth` independently (previously one combined `m_HasPlate` flag)
    and reports each failure distinctly in `LastStatus`. `OnRenderImage` branches
    on all three tiers: CG-not-ready-or-plate-never-configured keeps the original
    plain-passthrough behavior (stages 1-2 unaffected); plate configured but color
    file missing/unreadable shows a flat-magenta "media offline" card
    (`FillSolidColor`, a 1x1 texture blitted full-screen); color fine but depth
    missing composites in naive mode (`PostProdCompositor.Composite(...,
    naiveMode: true)`, passing a cached dummy 1x1 depth texture - the shader's
    naive branch samples `_PlateDepthTex` but never reads the value, confirmed by
    reading `PostProdComposite.shader` directly) and draws a 12px magenta border
    directly onto the render target (`DrawMissingDepthBorder`, via Unity's built-in
    `Hidden/Internal-Colored` GL-drawing shader - no custom shader asset or font
    rendering needed) so it's never mistaken for a real depth-aware result.
  - **Note: `PostProductionMediaLoader` (stage 4's interactive frame-seek/status
    tool) and `PostProductionCompositeImageEffect` (stage 5/6's actual live
    compositor) are still two separate, unconnected code paths** - the compositor
    takes explicit color/depth file paths via `postprod.preview.plate`
    independently of the loader's C006-frame-number seeking. Both were fixed
    consistently with the same design, but nothing here wires a `SeekToC006Frame`
    call into automatically updating the compositor's plate paths - that
    connection (mapping sketch time / C006 frame to the compositor's plate) is
    still the open stage 5/6 concern noted elsewhere in this doc. Not yet tested
    end-to-end in Unity Play Mode against the real API.
