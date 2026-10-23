# Fast scrub/play preview for the post-production compositor — design draft

Status: **partly built** (written 2026-10-20 by Sonnet 5.5, refined by Opus). Built so
far: profiling (2.6), preview plates and RAM cache (4.1), live CG (4.2), lens
undistortion for the preview (4.6). Each section has a dated "Built" note. It supersedes the
"CG capture is a snapshot on demand" approach in `POSTPROD_PREVIEW_PLAN.md` stage 6.
Read that file first for the history; this one assumes it.

## 1. Purpose

The user's workflow is: **scrub and play through a recording quickly, by eye, to
find temporal and spatial mismatches between the real video and the Open Brush
(`.tilt`) stroke replay, then adjust the time and spatial offsets until they
line up.** There is no other calibration data for the existing recorded footage, so
visual inspection is the only available method. (Future shoots should get a proper
in-room calibration; that is out of scope here.)

That workflow needs:

- **Fast scrub**: dragging a slider shows the composited frame for that moment with
  no perceptible lag, like scrubbing in After Effects.
- **Responsive Pause/stop**: Pause takes effect immediately, not after a backlog.
- **Quick play**: playback that is watchable (real time, or at least smooth enough
  to judge motion alignment), not a slideshow.
- **Immediate offset feedback**: changing a time or spatial offset updates the
  preview straight away.
- **Full-quality on demand**: when scrubbing stops (or by choice), a full-resolution
  composite.

### 1.1 Target workflow (stated 2026-10-21)

1. A **calibrated shoot** in the dance studio.
2. **Pre-processing** (outside Open Brush) produces: the camera calibration
   (intrinsics, lens distortion, pose), full-res colour and depth plates, and low-res
   preview plates.
3. Everything else happens **inside Open Brush**: temporal and spatial alignment,
   preview, and final render/export.

The person working in the room delivers the video and preprocessed plates; the artist,
editor or director then completes post-production by themselves, without running the
technical pipeline.

**Clip manifest (proposed).** Pre-processing writes one manifest into the clip folder:
camera intrinsics, lens distortion, extrinsics/pose, source frame rate and resolution,
frame ranges, and the plate folders with their encodings. Open Brush loads the whole
setup with one command (e.g. `postprod.clip.load=<clipDir>`); the control page needs
only the folder. Nothing is typed or copied by hand (a hand-copied calibration already
introduced an fy mismatch, see 4.6).

**Gap list: things still specific to this shoot (2026-10-21).**

| Shoot-specific item | Where |
|---|---|
| Plate generator paths and the 1280x676 size | `make_preview_plates.py` constants |
| Preview folder name `preview_1280x676` | `PostProductionPlateCache.cs` |
| 60fps source frame rate; "C006" in names | `PostProductionSettings` (`C006NativeFps`, `SketchTimeToC006Frame`) |
| Lens, intrinsics, pose, clip dir, sketch name typed or pre-filled with this shoot's values | `postproduction.html` defaults |
| Calibration copied by hand | workflow (fy mismatch, 4.6) |

Already general: the undistorted working frame/overscan (computed from whatever
calibration is set), plate frame ranges and depth stride (read from manifests),
true-source-frame-number naming, and per-sketch persistence of all settings.

**Built and tested 2026-10-23** (`Untitled_17` migrated: live CG before vs after mean diff
0.72/255, stroke IoU 0.967, the residual from the true fy; reload re-reads the manifest;
backup in `OpenBrushReplays\backups\`): `clip.json`, written by
`D:\Alison-Goodyear-Postproduction\scripts\make_clip_manifest.py` from `intrinsics.npz`,
`extrinsics.json` and ffprobe. `postprod.clip.load=<clipDir>` (`PostProductionClip.cs`)
applies intrinsics (true fy 3165.07, no longer fy = fx), lens, near/far, camera pose, source
fps (`PostProductionSettings.SourceFps`) and the preview folder name. The sketch stores only
`PostProduction.ClipDir`; camera and lens are re-read from the manifest on load (older
sketches keep loading their saved values). The control page starts blank and shows the
clip's values read-only. **Camera pose now uses the ChArUco board corner as origin**, from
the calibration directly (rotation matches the old hand-derived pose to 0.0001 deg, height
exactly). The old placement becomes the sketch's global spatial offset: for `Untitled_17`,
(6.1592, 0, -46.2888) units with no rotation, which keeps the composite identical. Still
shoot-specific: the "C006" in method names, the colour/depth folder names (a convention),
and the display camera name (`CenterEyeAnchor`, saved per sketch).

**Visible layers, built and tested 2026-10-23 (hide, save, reload, restore):** `PostProduction.VisibleLayers`
(null = all), saved by `postprod.metadata.save` from the live layer visibility and applied
after the authored `Layers[].Visible` on load. Read back with `query.postprod.layers.visible`.

## 2. Background and context

### 2.1 The project

Alison Goodyear dance piece. Real BlackMagic footage of three VR dancers is
composited with Open Brush stroke replays; real depth lets the real dancers occlude
the CG strokes. The existing offline pipeline is
`D:\Alison-Goodyear-Postproduction\scripts\make_composite_video.py` (Python), kept as
the QA reference. This repo (`D:\MSI-Github\open-brush-mp-stroke-streaming`, branch
`feature/mp-stroke-streaming`) is building an in-app version so alignment can be
checked interactively. Related docs in the repo root: `POSTPROD_PREVIEW_PLAN.md`
(10-stage plan and a long "known issues" section), `CALIBRATED_CAMERA_CAPTURE.md`,
`OpenBrushPostProductionAPIBrief.md`.

### 2.2 The data (all concrete values)

- **Video**: `Original\A002_06110040_C006.mov`, ProRes 422, 4096x2160, 60fps.
- **Sketch**: `Untitled_17.tilt`, 50 strokes, 4 layers. Layer 0 is a stray 3-point
  mark; layers 1-3 are the three dancers. Stroke timestamps span 64,796ms to
  311,240ms of sketch time.
- **Time mapping**: `videoTimeMs = sketchTimeMs + GlobalTimeOffsetMs`,
  `c006Frame = round(videoTimeMs / 1000 * 60)`. The current global offset is
  **-99,990 ms** (found by eye; a one-anchor calculation had given -99,073, a 917ms
  / 55-frame difference that was never reconciled). Negative means the tilt runs
  ahead of the video. Per-layer time offsets exist as data but nothing applies them.
- **Spatial offsets**: global and per-layer (`TrTransform`) exist as data only;
  nothing renders them (see 5.5).
- **Calibrated camera**: intrinsics `3163.5317,3163.5317,2289.4466,1102.9255,4096,2160`
  (OpenCV convention, off-centre principal point), clip `1,1000` Open Brush units,
  pose `-0.4743,14.2147,-11.5124, 0.070337,0.003137,0.001319,0.997518`, camera
  `CenterEyeAnchor`.
- **Existing plate media** (`D:\Alison-Goodyear-Postproduction\OpenBrushMedia\C006\`):
  - `depth_frames/frame_NNNNN_depth.exr`: real-footage depth from VDA, metric,
    float32 single-channel, **1280x676**, true C006 frame numbers, **stride 2** (even
    frames only), frames 2950-8830 (2,941 files). `manifest.json` has
    `firstFrame/lastFrame/stride`.
  - `color_frames/frame_NNNNN_color.png`: **full-res 4096x2160** PNG, even frames
    2100-9012 (3,457 files). 516 of these have no depth (outside 2950-8830).
  - `proxy_c006.mp4`: an H.264 preview proxy (built in stage 4, currently unused).
- Odd frames have neither colour nor depth. Today a requested odd frame snaps to the
  nearest even one (`DepthFrameResolver`).

### 2.3 What exists in code (all in `Assets/Scripts/API/` unless noted)

- `PostProductionPreviewController`: singleton MonoBehaviour. Applies the calibrated
  projection and pose to the target camera, owns plate/CG state, auto-plate sync
  (`UpdateAutoPlate`), CG snapshot (`RefreshCgCapture`).
- `PostProductionCompositeImageEffect`: `OnRenderImage` on the camera. Modes: normal
  composite, naive (no depth), CG-only (blits the live camera feed, ignoring the
  plate), "media offline" magenta.
- `PostProdCompositor` + shader: the depth-aware blend, proven against the Python
  reference: `visible = cgDepth > 0 && cgDepth < plateDepth` (strict, no epsilon, no
  median filter); `alpha = max(cg.rgb) * visible`; `out = cg*alpha + plate*(1-alpha)`.
- `ExrDepthReader`, `DepthFrameResolver`, `PostProductionSettings` (offsets, crop,
  time mapping and its inverse), `PostProductionMediaLoader` (stage-4 proxy
  `VideoPlayer` seek tool, not wired into the live preview).
- `PostProductionMetadataSaver` + `PostProductionMetadata` in `SketchMetadata.cs`:
  persists offsets, crop, camera setup and auto-plate dir into the `.tilt`
  `metadata.json` without touching stroke data.
- `Assets/Resources/ScriptExamples/postproduction.html`: the control page, served at
  `http://localhost:40074/examplescripts/postproduction.html` while Play mode runs.
- `ScreenshotManager` (`Assets/Scripts/`): `TakeSnapshot`, `RenderEncodedDepthNormalsToTexture`
  (depth rendered to an encoded RGBA RenderTexture), `ApplyCalibratedProjectionToCamera`.

### 2.4 How the preview works today (and why it is the problem)

CG strokes are captured with `ScreenshotManager.TakeSnapshot` (reused because it
already handled background removal, culling masks, and matched the Blender
reference camera). Each capture:

- renders colour at 4096x2160, reads it back GPU->CPU, PNG-encodes it;
- renders depth into an encoded RGBA texture, reads it back, builds a histogram over
  all ~8.8M pixels, then runs three CPU loops over every pixel to produce a 16-bit
  PNG, a float EXR (ZIP) and an 8-bit normalised PNG, plus a JSON file;
- writes five files synchronously, to `C:\Users\rich\OneDrive\Documents\Open Brush\Snapshots`
  (OneDrive-redirected, so possibly being synced on every write);
- then the compositor decodes the PNG and EXR from disk again for display.

Everything runs on Unity's main thread. **Measured**: about 1.4s per capture, and
while captures run sketch time advances at roughly a quarter of real speed. The
stage breakdown above comes from reading the code, **not from profiling**; the
first design task is to profile one capture to confirm where the time goes.
Only the colour PNG and the float EXR are actually used by the preview.

The plate side also decodes a 4096x2160 PNG whenever the mapped frame changes.

Consequences seen in use:

- Auto-plate follows scrubbing instantly, but the CG only updates on an explicit
  "Refresh CG capture", so plate and strokes disagree after any scrub.
- Added workarounds on the control page made it worse: debounced auto-refresh after
  a scrub, and a Play loop that queues `refreshcg` every ~100ms. The Remote API
  command queue is FIFO and each capture blocks ~1.4s, so a backlog builds and
  **Pause waits behind all of it** ("play continues for ages after I hit pause").
- That is a design mismatch, not a bug to patch: the snapshot path was chosen as a
  deliberate shortcut for single-frame alignment checks (plan stage 6 "known issues")
  and cannot support scrubbing or playback.

### 2.5 Why not a side-by-side colour+depth video

The original idea was one video file with low-res colour and depth side by side:
one decode, colour and depth locked together, tiny files. **We would like to have
used this, but our attempt at frame-exact seeking in Unity's `VideoPlayer` failed**
(`POSTPROD_PREVIEW_PLAN.md` "Video-seek frame precision", stage 4):

- `.time` seeks only appeared to land if the player was left **playing**.
- Left playing, it was +6 frames past the target after 0.5s. That is simply playback
  advancing, not a seek fault.

Revised assessment (2026-10-20): this is **unproven, not proven broken**. The loader
used a fixed 30-frame wait instead of `seekCompleted`/`frameReady` events, the proxy
is long-GOP H.264 (seeks decode from the nearest keyframe), and a paused player may not
refresh its texture after a seek without `StepForward()` or a brief play. An all-intra
encode with event-driven seeking might well work.

**Decision: use the RAM-cached image-sequence approach (section 4.1) instead**, for
reasons that hold even if video seeking could be made reliable:

| | Side-by-side video (all-intra) | JPEG colour + lossless depth, RAM cache |
|---|---|---|
| Forward play | Best: hardware decode to GPU | Fine: ~5-10ms decode + upload, prefetched |
| Scrubbing back and forth over one region (the calibration workflow) | Every position is a new async seek and re-decode | Instant once cached |
| Frame exactness | Needs event-driven seeking proven | Exact by construction (one file per frame) |
| Depth precision | 8-bit lossy (~12cm steps over 0-30m, compression noise at edges) unless 10-bit decode works | Lossless 16-bit |
| Disk | Smallest | Larger, still modest (section 4.1) |

Video remains a fallback for long forward playback only if profiling shows the cache
can't keep up. Neither plate option is the current bottleneck; the CG capture is.

### 2.6 Profiling results (measured 2026-10-20, Editor Play mode)

Measured with temporary instrumentation (marked TEMP; `query.postprod.debug.timing`,
plus extra stage lines in `_snapshot_debug.log`), 4 runs, consistent across runs:

| Step | Time |
|---|---|
| Depth render (GPU) | 0-3ms |
| Colour render (GPU) | 3-6ms |
| Depth readback + CPU decode loops + 3 encodes | ~760-800ms |
| Colour PNG encode + write | ~210ms |
| File writes | 1-3ms |
| **Snapshot total** | **~980-1,030ms** (286 earlier logged captures: mean 1,190ms) |
| Compositor reloading the CG PNG + EXR from disk | ~290-345ms |
| Compositor reloading a full-res plate (4K PNG + EXR) | ~500-560ms |
| Sketch seek (the one frame applying it) | 0.5-96ms; worst cases rebuild ~36-48 strokes |

Conclusions:

- Rendering is effectively free; almost all cost is GPU->CPU readback and CPU
  encode/decode of large images. (GPU work may be partly attributed to the readback
  step, which doesn't change the conclusion.) This strongly supports the GPU-only live
  CG path (4.2).
- A plate-only change costs ~0.5s of main-thread stall, which is why auto-plate scrubbing
  also lagged. Supports the low-res plate cache (4.1).
- **Seeks are cheap enough for fast scrubbing.** The incremental stroke playback applies
  a seek in one frame, at worst ~100ms for a full rebuild on this sketch, typically a few
  ms. The design's biggest risk (seek cost, open question 4) is ruled out for this sketch.

## 3. Goals and non-goals

Goals:

1. Scrub latency low enough to feel continuous (target: a new frame composited per
   rendered Unity frame while dragging).
2. Pause/stop acts within one frame, regardless of what else is queued.
3. Play at or near real time for judging motion alignment.
4. Changing global/per-layer time offsets or spatial offsets updates the preview
   immediately.
5. On settle (or on demand) a full-resolution composite using the existing high-quality path.
6. Frame-accurate: the plate frame shown must be exactly the one the mapping selects.

Non-goals (for this design):

- The in-app final render (plan stage 9). The full-res on-demand path is a preview,
  not the deliverable.
- In-room calibration for future shoots.
- Replacing the Python pipeline, which stays as the QA reference.
- Fixing Open Brush's own save truncation bug (see section 8).

## 4. Proposed architecture

Two paths, chosen by interaction state:

- **Fast path (scrubbing/playing)**: everything on the GPU at preview resolution, no
  disk I/O per frame beyond reading small pre-made plate files.
- **Quality path (settled, or on demand)**: the existing full-resolution snapshot +
  full-res PNG/EXR plate, unchanged.

```
                 sketch time (slider / play clock)
                          |
          SketchTimeToC006Frame (global offset)      per-layer time offset (open Q, 4.7)
                          |
        +-----------------+------------------+
        |                                    |
  Preview plate cache                 Live CG render (per layer)
  (colour + depth, low-res,           camera -> colour RT + depth RT
   RAM LRU of textures)               preview res, no readback, no files
        |                                    |
        +-----------------+------------------+
                          |
              compositor shader (existing formula)
                          |
                    screen (camera)
```

### 4.1 Preview plate: low-res image sequence + RAM cache (chosen approach)

Must be **created**; it does not exist yet. We know enough to generate it now.

**On-disk format (decided):**

- **Resolution**: **1280x676 for both colour and depth**, pixel-aligned. 676 is the
  VDA depth's native height; colour scaled from 4096x2160 gives 675, so colour is
  scaled to 1280x676 (a 0.15% vertical stretch, the same full-frame stretch the
  Python reference applies when it resizes depth to 4096x2160). Depth is copied at
  native resolution with no resampling.
- **Colour**: `frame_NNNNN_color.jpg`, JPEG quality 90, 4:4:4 (no chroma subsampling).
  Source: the ProRes master via ffmpeg (not the existing full-res PNGs, to avoid a
  second generation of compression).
- **Depth**: `frame_NNNNN_depth.png`, **16-bit greyscale PNG, millimetres** (uint16,
  value = round(metres * 1000)), **0 = invalid / no depth**, matching the EXR
  convention. Sampled real depth range is 1.6-12.4m, well inside the 65.535m limit;
  the generator must still assert the max and report any clipping. Absolute
  millimetres, never per-frame normalisation (normalisation would make values
  incomparable across frames and with CG depth). Source: the existing
  `depth_frames/*.exr`.
- **Not undistorted**: plates stay in raw camera image space, like the existing
  full-res frames; undistortion happens in the compositor (section 4.6).
- **Frame coverage**: colour for **every frame** 2950-8830 (5,881 frames, 16ms steps
  for judging time offsets); depth at its existing stride 2 (2,941 frames), with odd
  frames using the nearest even depth. Colour outside the depth range can be added
  later if needed; those frames can only composite naively anyway.
- **Size**: roughly 150-250KB per colour JPEG and ~0.5-1MB per depth PNG (to be
  measured), so a few GB total. On D:.

**Storage location and naming:**

```
D:\Alison-Goodyear-Postproduction\OpenBrushMedia\C006\
    color_frames\              (existing: full-res PNG, even frames 2100-9012)
    depth_frames\              (existing: 1280x676 float EXR, even frames 2950-8830)
    proxy_c006.mp4             (existing, unused)
    preview_1280x676\          (new)
        manifest.json
        color\frame_NNNNN_color.jpg
        depth\frame_NNNNN_depth.png
```

- One folder per preview set, named by resolution, so a different preview size can
  sit alongside later without collisions. Colour and depth live together because they
  are a matched pair (same resolution, same source frame range, same settings).
- `frame_NNNNN` is the **true C006 source frame number**, the same convention as the
  existing sequences, so no lookup is needed to interpret a filename.
- `manifest.json` records: `width`, `height`, colour `firstFrame/lastFrame/stride`
  (2950/8830/1), depth `firstFrame/lastFrame/stride` (2950/8830/2), depth encoding
  (`uint16 millimetres, 0 = invalid`), colour encoding (JPEG q90 4:4:4), `undistorted:
  false`, source paths, and the generator script and date.
- Open Brush already sees `OpenBrushMedia\C006` through the NTFS junction
  `C:\Users\rich\OneDrive\Documents\Open Brush\Media Library\PostProd\C006`, so the new
  folder needs no extra setup. The existing auto-plate `clipDir` convention
  (`<clipDir>\color_frames`, `<clipDir>\depth_frames`) stays for the full-res path; the
  preview path reads `<clipDir>\preview_1280x676\manifest.json`.
- Generator: a one-off script in `D:\Alison-Goodyear-Postproduction\scripts\`
  (e.g. `make_preview_plates.py`), run with `.venv_depth` (has OpenEXR, cv2, numpy;
  the bare system Python does not have OpenEXR). It should verify every depth frame
  has a colour frame and report counts.

**Built 2026-10-20** (`PostProductionPlateCache.cs`, `postprod.preview.fastplates`).
Deviation from the format above: the in-app cache reads **depth from the existing
`depth_frames/*.exr`** (already 1280x676), decoded on the worker thread with the pure-C#
`ExrDepthReader`, rather than the 16-bit PNGs, because it isn't certain Unity 2022.3's
`LoadImage` reads 16-bit greyscale PNGs, and EXR decode can run off the main thread
anyway. Colour uses the new preview JPEGs. The 16-bit PNG depth set (1.7GB) is currently
unused by Unity. A frame not yet cached holds the previous one with an amber border.
Fix 2026-10-21: during playback the cache fell ~4s behind within a few seconds (the
loader restarted its search on every playhead change and decoded one EXR at a time).
Now forward moves under 30 frames don't restart a pass, and depth EXRs decode on up to
4 thread-pool threads; average decode time is reported in the status.
Fix 2026-10-23 (measured with TEMP DIAG status lines): at 1x, 4 decoders gave ~26 depth
frames/s against 30 needed (110ms per EXR), so frames ready ahead ran down from 300 to 0
over ~35s; then it never recovered, because each pass restarted at the playhead and spent
a third of its effort behind it. Now: decoders default to a quarter of the processor
threads (2-8; 8 here), settable with `postprod.preview.fastplates.decoders` **(must stay
configurable if this ships: slower machines need fewer, or a lower playback speed)**;
during forward play only frames ahead are loaded, starting 20 frames ahead when the
playhead has overtaken the cache, so it catches up instead of freezing. That stopped the
permanent freeze but not the shortfall: 8 decoders still completed only ~27/s, with 2-3
busy, because the single loader thread read colour JPEGs one at a time, and each cold file
open on the USB SSD (Samsung T9) costs ~12-15ms regardless of size (measured outside
Unity: 12.6ms per 61KB JPEG). Colour reads now run in parallel (8 at once) and the
File.Exists checks are gone. Verified: 0 holding samples in 181, frames ready ahead held
near 300 colour / ~86 depth at 1x, loader idle 86% of the time.
Remaining stutter (2026-10-23) was garbage collection, not the plate: 100-170ms hitches
about every second, in step with GCs (~1 per 30 frames, heap swinging 1.1-1.43GB), while
plate upload (<1ms) and stroke playback (<1.3ms) were negligible. Each depth decode
allocated ~16MB (file buffer, pixel array, a flipped copy, and new inflate/reorder
buffers for each of 43 blocks), ~480MB/s at 1x. Fixed with
`ExrDepthReader.ReadSingleChannelFloatPooled` (same decode code path, rows written
bottom-first, per-thread reused buffers) and a pool of depth pixel arrays in the cache; a
bit-exact comparison against the original reader runs once on each enable. Not yet verified.

**Cache design:**

- Keep **compressed bytes** in RAM, not decoded textures: a decoded 1280x676 RGBA
  frame is ~3.5MB (plus depth), so caching thousands decoded is too much, while the
  compressed files for the whole working range fit easily.
- Hold a small **decoded texture ring** around the playhead (e.g. ±30 frames), with
  read-ahead forwards when playing and both ways when scrubbing.
- Read file bytes on a worker thread; decode/upload on the main thread (Unity texture
  APIs are main-thread only), budgeted per frame so a big jump never stalls a frame.
- Never show a wrong frame: if the exact frame isn't decoded yet, show the nearest
  ready frame **visibly flagged** (or hold the previous frame flagged), consistent
  with the existing "never silently substitute" rule.
- Cache keyed by true frame number.

### 4.2 Live CG render (replaces the snapshot)

The camera already renders the CG live: **CG Only mode already shows exactly this feed
in real time**. What is missing is capturing it, with depth, into textures the
compositor can sample, at no extra readback cost.

- **Built 2026-10-20** (`PostProductionLiveCg.cs`): depth verified pixel-identical to
  the snapshot path; renders at the **display's pixel size** (a fixed 1280x676
  upscaled with point sampling stair-stepped stroke edges), 4x MSAA on colour,
  optional bloom (off by default; the snapshot colour pass has bloom).
- Render the calibrated camera into a **colour RenderTexture** and a **depth
  RenderTexture** at preview resolution each frame (or each time sketch time/offsets/
  layers change).
- Depth: ideally a float RT (R32F) written directly in metres by a replacement
  shader or depth pass, avoiding the encode/decode round trip. `ScreenshotManager.RenderEncodedDepthNormalsToTexture`
  exists but encodes into RGBA and is meant for readback; reuse its replacement-shader
  setup but sample on the GPU, not CPU.
- The compositor shader samples CG colour, CG depth, plate colour, plate depth and
  applies the existing formula. No files, no CPU loops.
- Must reproduce the same CG as the snapshot path: same culling mask, background
  handling (`removeBackground` sentinel logic in `TakeSnapshot` is non-trivial and
  was a prior source of bugs), same projection. Parity with the snapshot output is a
  required test (section 10).

### 4.3 Scrub, play and pause

- The playhead is a single piece of preview state: desired sketch time (or video
  frame). Scrubbing writes it; the preview reads it once per Unity frame.
- **Latest-wins, not queued.** Dragging must never enqueue N commands. Either a
  single "desired state" slot the preview polls per frame, or coalescing in the API
  layer. This is the direct fix for the Pause backlog: Pause sets a flag that the
  next frame observes, regardless of other traffic.
- Play: advance the sketch clock in Unity itself (not by the web page polling),
  driven by `Time.deltaTime`, so playback speed does not depend on HTTP round trips.
- The sketch playback seek (`sketch.currenttime`) is **incremental** ("stepping
  forward one frame only renders the strokes that began in that interval"). Its cost
  for large jumps, and backwards jumps, is **unmeasured** and could itself be the next
  bottleneck for fast scrubbing.
- Web page: slider should stream position while dragging (latest-wins), and Pause
  must not depend on an HTTP queue being empty.

### 4.4 Quality path

Unchanged mechanism: when the playhead has been still for a short delay, or on a
button, run the existing full-res `TakeSnapshot` + full-res plate (PNG/EXR) and show
it. A toggle can keep the fast path always on. The snapshot output files should
move off the OneDrive-synced `Snapshots` folder (or at least be written to a
non-synced path) and the unused depth outputs (16-bit PNG, normalised PNG, JSON)
should be skipped for this use.

### 4.5 Per-layer spatial offsets

**Built 2026-10-21, simpler than the draw path below.** `PostProductionLayerOffsetScope`
(`using (new PostProductionLayerOffsetScope()) { camera.Render(); }`) sets each offset
layer's canvas to authored local pose x offset for the duration of the live CG renders
only, and restores it exactly on Dispose, in the same call on the main thread, so no frame
passes and nothing else can see or save the temporary pose. Unity then renders exactly as
normal (every brush, transparency sorting, the depth pass), with all layers in one pass so
dancers still occlude each other. It sets the transform via `Coords.AsLocal` directly,
bypassing `CanvasScript.LocalPose` (which emits `PoseChanged` and clears
`transform.hasChanged`, used by development builds to detect unsanctioned changes), and
restores `hasChanged`. The global offset still moves the camera. Nudges take a target
(global or a layer); a layer's pivot is the floor under that layer's stroke centre.
`postprod.offset.spatial.nudge/revert/reset=<target>,...`, readout per target. Page: a
target selector on the nudge panel and a layers table (visible, solo, time offset with
nudges, spatial readout, Align = make it the nudge target).

Original plan, kept for reference:

Global and per-layer spatial offsets (`PostProductionSettings`) are stored and
persisted but **not rendered**. The plan (stage 3) deliberately forbids mutating
`canvas.transform` or `App.Scene.Pose` (they are the artist's authored placement and
are saved into the sketch). So offsets must be applied by the compositing camera's own
draw: render each layer's `Batch` meshes with an override matrix
(`authoredPose * offset`) via `Graphics.DrawMesh`/`CommandBuffer`, rather than letting
Unity's default traversal draw them. That is the same per-layer render the live CG
pass in 4.2 needs anyway, so the live CG pass should be designed around per-layer
rendering from the start (and visibility per layer: see 8).

**Global alignment nudge, built 2026-10-21** (`PostProductionAlignment.cs`). Since the
ChArUco camera pose, floor and scale are trusted, the editing controls are only: move the
sketch forward/back (camera view direction flattened onto the floor) and left/right
(camera's horizontal right axis), and turn it about the room's vertical axis through a
floor pivot under the centre of all strokes (all strokes, from the full memory list, so
the pivot doesn't move when scrubbing or hiding layers). Each nudge composes onto the
stored global offset, which stays a full transform. The live CG shows it by moving its
cameras by the inverse offset (exact from a fixed camera; the sketch is never touched).
API: `postprod.offset.spatial.nudge=forwardM,rightM,yawDeg`, `.revert` (to last loaded
or saved), `.reset`, readout via `query.postprod.offset.spatial.readout`. Page: buttons,
step sizes, arrow keys + Q/E. Per-layer offsets are still not drawn.

**Shuttle and live scrub, built 2026-10-21.** `postprod.preview.shuttle=<rate>` (1 = real
time, negative = reverse, 0 = stop) advances the sketch clock inside Open Brush each frame
(`PostProductionPreviewController.Update`, with `Time.timeScale` held at 0), so playback
never depends on HTTP round trips; the page's old Play loop is gone and Play/Pause use
the shuttle. Page: reverse/stop/forward at 0.1x-1x, keys J/K/L. Optional live scrub
while dragging sends only the latest slider position with at most one request in flight.
Nudge steps are now typeable, with 0.5cm/1/5/20cm and 0.1/0.5/2 degree presets.
Fix 2026-10-21: the shuttle and Pause first froze Unity's game clock (`Time.timeScale =
0`), which also froze the environment lighting fade-in after a load and left lit strokes
grey. They now hold or drive only the sketch clock (cancelling its per-frame advance and
adopting outside seeks), with `[DefaultExecutionOrder(-10000)]` so stroke playback sees
the corrected time; the game clock runs normally.

### 4.6 Lens distortion (found 2026-10-20, affects both paths)

The CG is rendered with a pinhole projection from the calibrated intrinsics. The real
footage is **not** pinhole: `calibration_output\intrinsics.npz` has
`dist_coeffs = [-0.0241, -0.0144, 0.00273, 0.00589, 0.0174]`. Measured with OpenCV
(2026-10-21): up to **89px of displacement** at 4K (an earlier estimate of ~25px only
counted the main radial term), largest towards the left and top because of the
off-centre principal point and the tangential terms. Enough to distort by-eye spatial
alignment and bake lens error into the spatial offsets.

Current state:

- `color_frames` (extracted by plain ffmpeg) and `depth_frames` (VDA run on raw
  frames; `bake_depth_exr_for_openbrush.py` does no undistortion) are both in **raw,
  distorted** image space. They agree with each other, but **the in-app compositor
  currently composites pinhole CG over a distorted plate.**
- The Python reference undistorts the colour plate (`cv2.remap`) but **not** the depth
  it compares against, so even the reference has a colour/depth mismatch of up to the
  same amount at the edges.

**Agreed design (2026-10-21): Open Brush works fully undistorted.**

- **Undistorted working frame.** The CG is already a pinhole render. The plate colour
  and depth are undistorted in the compositor, so alignment is judged in a geometrically
  true space. No plate files are regenerated: plates stay in raw camera space.
- **Overscan.** This lens has barrel distortion; straightening it pushes the frame edges
  outward, so a same-size undistorted frame (as the Python reference uses) loses edge
  content. The working frame is enlarged, with the same focal length and a shifted
  principal point, until every original pixel fits. Computed with
  `cv2.undistortPoints` on the frame border, P = the CG camera matrix:
  **4182x2219** (+2.1% x, +2.7% y; padding L80.7 R5.0 T38.1 B19.9 px), principal point
  **(2370.45, 1141.93)**. The CG renders at the same overscanned frame.
- **Export, two options.** (a) Undistorted: the overscanned composite as it is, or
  cropped. (b) Matching the original lens: re-apply the distortion. Preferred for (b):
  distort the **CG** colour and depth back into raw lens space and composite over the
  original, untouched plate, so the real footage is never resampled; the occlusion
  test then runs in raw lens space. The overscan guarantees CG coverage for every edge
  pixel. Export is not built yet (it is part of the final render, plan stage 9).
- **Depth is resampled nearest-texel, never blended**: interpolating across a depth
  edge invents in-between depths that change occlusion.
- **Calibration mismatch to note:** the calibration's fy is 3165.07 while
  `camera.intrinsics` uses 3163.53 for both fx and fy (0.05%). The working frame uses
  the CG camera's values; the source mapping uses the true calibration. Not changed.

**Built 2026-10-21 (preview half):** `PostProductionLens.cs` holds the calibration
(`postprod.lens=fx,fy,cx,cy,k1,k2,p1,p2,k3`, persisted in `PostProduction.LensCalibration`)
and computes the overscanned working frame in C# (iterative undistortion, matching
`cv2.undistortPoints`). With `postprod.preview.lens.undistort=1`, the live CG renders the
working frame and the compositor shader maps each output pixel through the forward
distortion model to the raw plate (no lookup texture needed: the forward model is closed
form). Outside the real frame the plate is black and does not occlude the CG.
Undistortion follows the live CG path only (the snapshot path renders the raw frame).
To verify: compare against `cv2.initUndistortRectifyMap` + `cv2.remap` for one frame at
the overscan size.

### 4.7 Per-layer time offsets

**Built 2026-10-21** (`PostProductionLayerTimeline.cs`). Stroke playback asks an
`IStrokePlaybackTimeline` for every time it uses (stroke start, end, each control point).
`ScenePlaybackByTimeLayered` now wraps whichever timeline is in use (sketch-time or
real-time) in one that adds each stroke's layer offset to all three, so each dancer is
drawn shifted in time; one clock, one playback, nothing on disk changes, and no wrapper
at all when every layer offset is 0. Convention: video time = sketch time + global +
layer; the plate follows the global offset only. Offsets are captured when the playback
is built, so `postprod.offset.time.layer` rebuilds it via
`SketchMemoryScript.RebuildPlaybackKeepingTime` (rewind the old playback to 0, which
clears part-drawn strokes, as `Redraw`'s timeline-edit branch does; start a new one in the
current mode so a real-time timeline stays real-time; restore the time). Page: per-dancer
ms field (live), frames readout, -1 frame / -1ms / +1ms / +1 frame.

Original analysis, kept for reference:

Per-layer time offsets mean different layers should show different sketch moments.
Sketch playback is **one global clock** (`App.Instance.CurrentSketchTime`) that adds
or removes strokes incrementally. Showing layer A at t and layer B at t+delta is not
something the current playback supports. Options for Opus to evaluate:

- Per-layer visibility by stroke timestamp (each stroke has control-point timestamps;
  show strokes whose time <= that layer's own time), possibly by rendering from fully
  loaded strokes and masking, instead of the incremental playback.
- Multiple playback timelines, one per layer.
- Defer: ship global time offset + per-layer spatial first (the user found the global
  offset by eye already), and treat per-layer time as phase 2.

## 5. Interaction between pieces (summary of data flow)

1. Playhead (sketch time) -> `SketchTimeToC006Frame` -> resolved plate frame.
2. Plate cache returns textures for that frame (or a placeholder while loading;
   never silently substitute a wrong frame; the existing "media offline" magenta
   convention applies).
3. Live CG pass renders the sketch at the playhead, per layer, with spatial offsets.
4. Compositor combines them; shown on the camera.
5. Settled -> quality path replaces the display with the full-res composite.

## 6. Control surface

Keep the web page (`postproduction.html`) as the control UI: the user already works
there. It must change from "send command, wait" to "update shared playhead state".
Open question: HTTP polling vs a WebSocket (Open Brush already has a websocket test
page, `websockets_test.html`) for streaming slider values. The page should also show
the plate frame, video time, and whether the display is the fast or quality path.

## 7. Calibration aids (optional, to be prioritised by the user)

Since alignment is judged by eye, cheap aids could help: a wipe/split slider between
plate and CG, outline-only CG, an overlay of the frame number and both clocks, and
stepping by single frames. None of these are designed yet; they are listed so Opus can
ask the user which matter.

## 8. Constraints, hazards and conventions

- **Builds take ~30 min** (Windows Il2cpp). Everything here should be testable in
  Editor Play mode against the Remote API on `localhost:40074`; spend a build only at
  the end.
- **Always ask the user to press Play** before testing via the API; do not infer from
  port or log state.
- **HTTP 200 does not mean an API command worked**; check the real side effect or a
  status query. Several endpoints have `query.postprod.*.status` readbacks for this.
- **Do not use `save.overwrite` / `save.new` via the API while `sketch.scrub` is
  active.** On 2026-09-08 this silently truncated `Untitled_17.tilt` from 50 strokes
  to 14 (the full-save path re-derives strokes from `SketchMemoryScript.AllStrokes()`,
  which returns a truncated set mid-scrub). The user restored a backup. Use
  `postprod.metadata.save=<name>`, which copies `data.sketch` byte for byte. The root
  cause in Open Brush's own save code is unfixed.
- **Layer visibility is not persisted.** The user wants a dedicated
  `PostProduction.VisibleLayers` field (not the authored `Layers[].Visible`) so a
  render can specify e.g. "layer 2 only". Not built.
- **Disk**: C: is nearly full (a previous Editor.log grew to fill it). Put all new
  media on D:. The Open Brush Documents folder is OneDrive-redirected:
  `C:\Users\rich\OneDrive\Documents\Open Brush\` (Sketches, Snapshots, etc.).
- **Do not mutate real canvas/scene transforms** for offsets (see 4.5).
- **Sketchbook panels cover the view after an API load (open, 2026-10-20).** The API
  load path (`LoadNamedFile` in `ApiMethods.GlobalCommands.cs`) calls
  `PanelManager.ToggleSketchbookPanels(true)`, which switches the panels into
  Sketchbook mode over the view. `panel.close=sketchbook` does not undo it (it only
  hides floating widget panels). A tried endpoint that toggled the mode back closed the
  panels, but they reopened shortly after; it was reverted. Workaround: the user opens
  **New Empty Sketch** before an API-driven test. Needs a proper fix later (e.g. an
  option on the API load not to open the Sketchbook).
- **Seeking near the end of the sketch jumps to the end (open, 2026-10-20).** With
  playback paused, `sketch.currenttime=300000` was read back as 311,240ms (the last
  stroke's end) on the next frame, reproducibly. Cause not investigated; matters for
  scrubbing the end of a recording.
- Prior failures worth remembering: a lazily-created singleton meant sketch time read
  as 0 until another command touched it (now created eagerly in `ApiManager.Awake`);
  Open Brush sets `CurrentSketchTime = float.MaxValue` as an internal "fully loaded"
  sentinel that must be clamped before display; a bare `new JsonSerializer()` cannot
  parse `metadata.json` (needs `CustomJsonContractResolver`).
- User preferences recorded in project memory: keep scope tight, prefer the smallest
  change that satisfies the request, do not bundle "while I'm at it" work, and move
  fast. A 2026-09-06 session overran badly (5.5h vs ~1h expected) partly from scope
  creep.

## 9. Open questions for refinement

1. Profile one snapshot: **done** (section 2.6).
2. Plate format: **decided** (section 4.1): JPEG colour + 16-bit millimetre PNG depth,
   1280x676, RAM cache. Side-by-side all-intra video kept only as a playback fallback.
3. Temporal resolution: **decided**: colour every frame, depth stride 2. Revisit only
   if odd frames with held depth cause visible occlusion stepping during play.
3a. Lens distortion (section 4.6): confirm the remap-in-shader approach, and decide
   whether the Python reference should also be fixed to remap depth.
4. Cost of `sketch.currenttime` seeks: **answered** (section 2.6): cheap, 0.5-96ms.
5. Live depth: float RT via replacement shader vs reusing the encoded path; how to
   match `TakeSnapshot`'s background/culling behaviour exactly.
6. Per-layer time offset strategy (4.7) and whether it is in scope for the first
   version.
7. Control transport: HTTP latest-wins slot vs WebSocket.
8. Preview resolution: fixed (e.g. 1280x675) vs a user-selectable draft quality like
   After Effects.
9. Which calibration aids (section 7) does the user actually want?
10. The 917ms discrepancy between the calculated (-99,073) and by-eye (-99,990)
    global offsets: still unreconciled; the new tool is how it gets resolved.

## 10. Staging and test plan (suggested; Opus should revise)

1. Profile a snapshot and seeks: **done** (section 2.6). Preview plates generated
   (`preview_1280x676`, 5,881 colour + 2,941 depth frames, 2026-10-20).
2. Generate the preview plate set (`preview_1280x676`, section 4.1) with
   `make_preview_plates.py`; build the texture cache; verify frame-accurate display
   against the full-res plate for a few known frames (e.g. 3888, 3900, 3960).
2a. Add the undistortion remap to the compositor (section 4.6) and check it against
   `cv2.remap` for one frame.
3. Live CG render to RenderTextures at preview res, behind a toggle, alongside the
   snapshot path. **Parity test**: same sketch time and camera, compare against the
   snapshot output (pixel diff on colour; visibility-mask diff on depth).
4. Compositor reading the live textures; scrub and play driven inside Unity;
   latest-wins control; measure frame time and Pause latency.
5. Settle-to-quality-path.
6. Per-layer spatial offsets via the per-layer draw path; then decide on per-layer time.
7. `PostProduction.VisibleLayers` persistence.

Acceptance: dragging the slider across the 3900-4000 range updates every frame with
no visible lag; Pause stops within one frame while playing; changing the global time
offset visibly shifts the CG against the video with no refresh click; settling shows
the full-res composite that matches the previous snapshot path.

## 11. State of the code at time of writing (uncommitted, mostly untested)

- `PostProductionPreviewController.cs`: Preview Off now restores the camera pose
  recorded when Preview turned on (previously only the projection reset, leaving the
  camera stuck at the calibrated pose). Not compiled or run. Also a `CgCaptureCount`
  counter was added with **no** matching query endpoint (the `ApiManager.cs` edit was
  not made); harmless, remove or use as part of the redesign.
- `postproduction.html`: slider sends the seek on mouse-up; an auto-refresh CG
  checkbox with debounced capture after scrub/layer changes; a Play loop that
  repeatedly requests captures. **The Play loop causes the Pause backlog and should be
  removed** when the fast path lands. Not run in a browser.
- Everything else listed in sections 2.3 and in `POSTPROD_PREVIEW_PLAN.md` stage 2, 7
  and 8 status notes was built earlier and partly verified (the save/load round trip of
  offsets and the metadata-only save were checked against the file directly).
