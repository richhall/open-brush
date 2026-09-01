# Calibrated camera capture — what this branch adds

This branch is `feature/mp-stroke-streaming` on
[`richhall/open-brush`](https://github.com/richhall/open-brush) — i.e. Andy's branch with
a single commit on top.

**To see only what we added**, compare against the upstream branch rather than `main`:

https://github.com/icosa-foundation/open-brush/compare/feature/mp-stroke-streaming...richhall:open-brush:feature/mp-stroke-streaming

(A comparison against `main` would show all 89 commits of `mp-stroke-streaming` mixed in
with ours, which is not what you want.)

## Why

We are compositing Open Brush stroke replays over real video of the same performance:
three dancers painting in VR, filmed on a calibrated BlackMagic camera against
greenscreen, with the sketch colocated to a ChArUco board in the room.

To make that work, Open Brush has to render frames from a virtual camera that matches
the real one — same intrinsics, same pose, same moment in the sketch's timeline — and
produce depth alongside colour so real dancers can occlude CG strokes correctly.

Everything here exists to serve that. It has been used end to end: a 98-second, 5,867
frame sequence at 4096×2160 with metric depth, composited against the real footage.

## What was added

### 1. Calibrated camera (`ScreenshotManager.cs`, `ApiMethods.cs`)

`camera.intrinsics=fx,fy,cx,cy,width,height` sets a real OpenCV camera matrix, applied
by building `Camera.projectionMatrix` directly rather than approximating with
`fieldOfView`.

This matters because a single vertical FOV cannot express a non-centred principal point
(cx, cy) or non-square pixels (fx ≠ fy), and real calibrated lenses have both. Unity's
physical-camera `lensShift` could cover the principal point but not fx ≠ fy, and uses its
own normalisation convention. The projection matrix expresses all four exactly.

`camera.clip=near,far` sets clip planes in Open Brush units (1 m = 10 units), which also
sets the range recorded in the depth sidecar.

Verified against a Blender scene carrying the same calibration: vertical FOV, horizontal
FOV and lens shift all match to 5–6 decimal places, and rendered stroke geometry
overlays the Blender reference at IoU 0.78 with stroke pixel counts within 2.7%.

### 2. Timeline scrubbing (`SketchMemoryScript.cs`, `ApiMethods.GlobalCommands.cs`)

`sketch.scrub=1` keeps the playback timeline alive so `sketch.currenttime=<ms>` can seek
to any moment, forwards or backwards, repeatedly.

**This is arguably a bug fix rather than a feature.** `ContinueDrawingFromMemory()` sets
`m_ScenePlayback = null` once the initial play completes. Only `IScenePlayback.Update()`
acts on `App.Instance.CurrentSketchTime`, so with no playback object a seek silently does
nothing — the scene just stays as loaded. The existing code comment ("in timeline edit
mode we keep playback alive to allow scrubbing") describes the intended behaviour, but
nothing reaches it: `forEdit` is never passed `true` by any caller on this branch.

The change is one guarded line, off by default:

```csharp
[NonSerialized] public bool m_KeepPlaybackAliveForScrubbing;
...
if (!m_KeepPlaybackAliveForScrubbing) { m_ScenePlayback = null; }
```

`sketch.scrub` also repairs state if enabled after a sketch has finished loading, by
unrendering and rebuilding the timeline — so it works whichever order it is called in.

Seeks are incremental: stepping forward one frame renders only the strokes that began in
that interval, rather than rebuilding the scene.

**Units gotcha:** `sketch.currenttime` is measured in milliseconds **from session start**,
not from the sketch's own stroke timestamps. In RealTime mode `BeginDrawingFromMemory`
sets `CurrentSketchTime = 0` and builds a `RealTimeStrokePlaybackTimeline`, so the zero
point is `StrokeTimeSessions[0].StartSketchTimeMs`. Sending a raw stroke timestamp lands
you elsewhere in the sketch and looks like the seek is broken.

### 3. Quaternion camera input (`ApiMethods.cs`, `ApiEndpointAttribute.cs`)

`app.snapshot.quat=x,y,z,qx,qy,qz,qw,filename,w,h` and `user.look.at.quat`, plus
`Quaternion` support in the endpoint parameter parser (four comma-separated floats,
following the existing `Vector3` pattern).

Euler input goes through `Quaternion.Euler` at the API boundary, which introduces
composition-order ambiguity when the rotation comes from an external calibration.
Supplying the quaternion directly avoids it.

Note `user.look.at` (Euler or quaternion) is subject to `SceneScript.Pose`'s tilt
protection, which silently re-levels the scene and strips pitch and roll unless
`disableTiltProtection` is set. `app.snapshot`/`app.snapshot.quat` are not affected —
they position the capture rig directly.

### 4. Camera introspection (`ApiManager.cs`, `ScreenshotManager.cs`)

`query.camera.projection` returns both halves of the camera:

- **projection** — fx, fy, cx, cy, vertical/horizontal FOV, clip planes in units and
  metres, the 4×4 matrix, and lens shift expressed in Blender's convention so it can be
  compared directly against a Blender camera
- **lastCaptureExtrinsics** — the capture camera's world position and rotation for the
  most recent snapshot, **alongside the requested pose**

Reporting requested-versus-actual side by side is deliberate: it makes "is the camera
actually where I asked" answerable in one call. That is what identified the bug below.

### 5. Warning comments only: do not activate the MultiCamTool in `app.snapshot`

**This is not a fix to anything in your branch — it is a warning against a mistake we
made ourselves.** The net code change here is zero; only comments were added.

While developing this we added `EnableSpecificTool(BaseTool.ToolType.MultiCamTool)` to
`app.snapshot`, believing the tool had to be active for capture to work. It broke every
capture after the first, in a way that took a long time to diagnose because it looked
like a camera-positioning problem.

The mechanism is worth knowing. While MultiCamTool is the active tool,
`MultiCamTool.UpdateCameraManagerTransforms()` runs every frame from `UpdateTool()` and
drives the capture camera to its own attach point — which follows the user's pointer, and
in the `!EnableMulticamPreview` path is pinned to
`App.VrSdk.GetVrCamera().transform.position`. So the **first** capture after activation
used the requested pose, and every one after that silently pointed at the user's
viewpoint, rendering blank while the strokes stayed visible on screen.

Activation is not needed: `ScreenshotManager.TakeSnapshot` finds the tool via
`GetToolOfType` (which works whether or not it is active) and activates the capture rig
itself. Two comments now say so, so nobody reinstates it for the same plausible-sounding
reason we did.

### 6. `app.timescale=<float>`

Sets `Time.timeScale`. Used to freeze real-time drift so a seek holds exactly between
being set and being captured. The same technique the MultiCam ODS capture path already
uses internally.

## Deliberately left as-is

### The bespoke file logging

`ScreenshotManager.DebugLogToFile` writes to `Snapshots/_snapshot_debug.log` rather than
using `Debug.Log`. There are 31 call sites. This is **not** the right long-term shape and
we know it — the same file already uses `Debug.LogAssertion` and `OutputWindowScript`.

It is here for two real reasons:

1. `Player.log` is unusable during long capture runs. `VrStylusHandler` polls VR input
   actions every `LateUpdate()` with no HMD check, so in NoHeadsetMode it logs an error
   plus stack trace **every frame** — measured at ~40 MB/min, reaching 4.9 GB in one
   session and filling the system drive. See "Bugs found" below.
2. The render tooling depends on it. `query.queue` reports `m_OutgoingCommandQueue`, not
   the request queue, so it always reads 0 and cannot be used to detect capture
   completion. Our render script instead watches this log for the end-of-capture marker.

Converting it to `Debug.Log` is straightforward, but would mean shipping untested changes
to the one path that took longest to validate, and re-verifying costs a full multi-hour
render. It is left working and documented rather than tidied and unproven.

`app.getpose` is likewise a temporary debug endpoint. It returns a string, but because it
is not `query.`-prefixed the HTTP response is always empty — its output only reaches the
debug log. It should be renamed `query.scene.pose` or dropped.

### Local dev workarounds (not for upstream)

- **`BuildTiltBrush.cs`** — overrides the Android identifier to a package registered
  against a personal Meta App ID, because the official Open Brush App ID has no
  entitlement on this developer's account.
- **`PhotonManager.cs`** — forces `ConnectionProtocol.WebSocketSecure` (port 443) instead
  of raw UDP. This was needed to run the session on **21 August 2026**, on a venue network
  that blocked outbound UDP to Photon's nameserver and game servers. Recommended
  implementation would be to use UDP and fall back to WebSockets if UDP fails — venue and
  institutional networks that block outbound UDP are common enough that this is worth
  handling properly rather than as a local override.
- **`Config.cs`** — null-conditional guards on the `Secrets` accessors so the app runs
  without a secrets asset present.

The Meta App ID in `OculusPlatformSettings.asset` has deliberately **not** been committed.
`Assets/Secrets.asset` is gitignored and contains no committed credentials.

## Bugs found along the way (not fixed here)

1. **Tilt files do not load preserving layers.** `layer.show` / `layer.hide` /
   `layer.toggle` work correctly — they call `canvas.gameObject.SetActive()`. But loading
   a `.tilt` puts every stroke on canvas 0, so per-layer visibility cannot isolate
   anything. Verified by hiding layers 1–3 (no visual change) then layer 0 (everything
   disappeared). The per-stroke layer index **is** present in the file — we read it
   across 47 sketches — so the data survives and the loader discards the structure. This
   blocks rendering one artist at a time from a multiplayer sketch.

2. **`VrStylusHandler` logs every frame without an HMD check.** Every other VR-only
   component gates on `VrSdk.IsHmdInitialized()`; this one does not, so NoHeadsetMode
   produces an error and stack trace per frame. Real CPU cost, constant fan noise, and
   ~40 MB/min of `Player.log` — enough to fill a system drive during a long render.

3. **The watermark is applied after the depth/normals passes are restored.**
   `TakeSnapshot` sets `CameraConfig.Watermark = false` for depth and normals, but its
   `finally` restores it before the colour pass runs. So colour frames carry a watermark
   the sidecars do not. Harmless if the compositing matte comes from depth, surprising
   otherwise.

4. **`query.queue` reports the wrong queue** — `m_OutgoingCommandQueue` rather than
   pending requests, so it is not usable to detect when an API command has completed.

## Also worth knowing

- **Layer indices are not portable between headsets.** The same multiplayer session saved
  on different headsets contains identical layer *content* but permuted layer *indices*.
  Identify an artist by control-point count, not layer index.
- **`App.METERS_TO_UNITS = 10`** — Open Brush world units are decimetres. Camera
  positions, clip planes and depth values all need this conversion against real-world
  metres.
- **Depth output is genuinely metric.** The `_depth.exr` sidecar is float32 metres,
  single channel `Y`, 0 = no geometry, `distanceConvention: optical-axis`.

## Measured performance

On an RTX 4080 laptop, 4096×2160 with colour + depth + normals:

| | |
|---|---|
| Capture | ~1.4 s/frame |
| 20 s sequence (1,200 frames) | ~28 min |
| Full performance (5,867 frames) | ~2 h 15 m, 11.6 GB |

The bottleneck is host-side, not GPU — and much of it is the driving architecture rather
than the rendering. **Nothing here composites inside Open Brush.** The app writes colour +
depth + normals to disk, and a separate Python pass reads those frames back, undistorts
the real plate, applies the depth comparison and encodes the video.

The render loop is likewise external: a script issues one HTTP seek and one HTTP snapshot
per frame and then waits on the debug log for the completion marker, because `query.queue`
cannot report it (see Bugs found). So every frame pays a round trip, a full PNG + EXR
encode and a filesystem write. A render loop running **inside** Open Brush — stepping the
playback timeline and capturing in-process — would avoid all of that, and could keep the
frames in memory rather than round-tripping them through disk. We have not measured how
much of the 1.4 s that would recover, but it is the obvious place to look.

## Known limitations of the current output

- **Temporal and spatial misalignment.** The composited sequence does not line up
  perfectly against the plate, and the error appears to **differ per dancer** — which
  points at per-artist offsets (stroke-recording latency, or per-headset colocation drift)
  rather than a single global transform error. Isolating this is what makes the layer
  problem above matter: we cannot currently render one artist at a time.
- **Fine strokes read weakly in the composite.** Work painted for a viewer standing inside
  it in VR does not necessarily read through a calibrated camera at a fixed distance, and
  fine brushes are where that shows. This is an **editorial** matter, not only a technical
  one: the fix is for an editor to change or thicken the brush in post, inside Open Brush,
  judging it against the actual composited frame. That is a decision about how the piece
  reads on screen, and it needs to be made where it can be seen.

## Next steps

Move preview **into** Open Brush. The intent is to load the precomputed metric depth
alongside the sketch, so the calibrated camera can be scrubbed, aligned and previewed
against the real footage live in-app — checking and tweaking alignment interactively
before committing to a multi-hour render for final compositing.

The value is not only alignment. Once an editor can see the composite in the app, the
sketch itself becomes editable against it — reworking brush choice and stroke weight so
the piece reads on camera, as above. Open Brush is already the right tool for that; what
is missing is the view of the shot while you do it.

That is also the natural home for **keyframing**: if the stroke recording drifts against
the plate over the length of a performance, or drifts differently per artist, a static
transform cannot correct it. Being able to set alignment keys at a few points on the
timeline and interpolate between them would.
