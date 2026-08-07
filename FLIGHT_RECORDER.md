# Flight Recorder — branch status

**Branch:** `flight-recorder`, stacked on [`meta-colocation`](COLOCATION.md) (builds on its anchor/`PhotonPlayerRig` infrastructure — read that file first for the multiplayer/colocation base this sits on).

**Status: functionally working end-to-end on real hardware (confirmed 2026-08-07), but not yet committed.** Everything below exists only as uncommitted working-tree changes on top of `meta-colocation`'s tip (`b1c80ca83`) — `git status` on this branch will show it dirty. This file exists so the state of that work is legible before it gets committed.

## What this branch adds

Two related but distinct features, both under the "record a multiplayer session, use it later" umbrella:

### 1. Session recording + replay (the actual "flight recorder")

Records strokes and every player's head/hand/tool pose during a live multiplayer session to a local timestamped `.jsonl` log, for later replay into a sketch — intended for "fix-it-in-post" video compositing (record now, get the camera move right later).

- **New files**: `Assets/Scripts/Multiplayer/Recording/SessionRecorder.cs`, `SessionPlaybackController.cs`.
- **API-triggered** (`Assets/Scripts/API/ApiMethods.Multiplayer.cs`): `recording.start`, `recording.stop`, `replay.list`, `replay.start` (defaults to most recent recording if no filename given). No UI yet — API/HTTP only.
- **Stroke replay** reuses Open Brush's own native timestamp-based playback system (`SketchMemoryScript.PlaybackMode.Timestamps` / `ScenePlaybackByTimeLayered` — the same mechanism that makes a loaded `.tilt` file "draw itself"), so strokes reveal fluidly and in real recorded timing, not materialized instantly.
- **Pose replay** uses simple primitive "ghost" markers (`SessionPlaybackController.GhostRig`) at each recorded player's head/hand/tool position — deliberately minimal, not the full networked avatar prefab (can't easily be spawned outside Fusion's own spawn flow). **Still sphere primitives, not real headset/controller models** — asked for twice, not yet done, tracked as a known gap.
- **Local-player pose bug fixed this session**: `PhotonPlayerRig.RecordPose()` only ever fired from the `!HasStateAuthority` branch of `Render()` — i.e. only for *other* players as seen by you, never your own device's own movement. Fixed by adding the equivalent `SessionRecorder.RecordPose()` call in `MultiplayerManager.cs`, reusing the same per-frame local head/hand/tool transforms already computed there for the Photon network send. Before this fix, a solo recording captured nothing for the recording device itself.
- **Hide-remote-avatars toggle** wired into ghost-marker visibility too (checked every `ApplyPose()` call, same head/left/right split as live `PhotonPlayerRig`) — reported as not visibly toggling during a replay test; not yet root-caused (need to confirm whether `MultiplayerManager.HideRemotePlayerAvatars` is even reachable/set during a solo replay outside an active room).

### 2. Live multiplayer stroke streaming (separate feature, same branch/session)

A purely additive, best-effort real-time preview of a remote player's stroke *while they're still drawing it* — the existing authoritative Begin/Continue/Complete stroke pipeline (`PhotonManager.CommandBrushStroke`) sends a whole completed stroke as one burst after local drawing finishes, so without this, remote strokes only appeared once fully done.

- **Sender**: `PhotonManager.UpdateLiveStrokeStream()` — per-frame polling of the local drawing pointer, throttled by `MultiplayerManager.LiveStrokeFlushIntervalMs` (default 100ms, exposed via `multiplayer.liveStrokeStreamIntervalMs` API for live tuning).
- **Receiver**: `PhotonRPC.cs` "Live stroke streaming" region — dedicated per-stroke `PointerScript` per in-progress remote stroke (`PointerManager.CreateRemotePointer()`), tracked by the drawing stroke's `RandomSeed` (no `Stroke`/`Guid` exists yet mid-draw), torn down the instant the authoritative stroke arrives (`CleanUpLivePreview`), with an 8s staleness sweep as a safety net for dropped RPCs.
- **Fixed a real rendering bug** ("strokes fanned back to the start point while being drawn, self-corrected on completion"): root cause was that the live-preview pointer's own `transform` was set once at stroke-start and never moved again, but `PointerManager.Update()` calls `UpdatePointer()` → `UpdateLineFromObject()` on every remote pointer every frame regardless of the explicit per-batch draw calls — that derived a position from the stale transform and yanked the brush's internal "last position" back to the stroke's start between each network batch. Fixed by syncing the preview pointer's transform to the latest point at the end of each batch (`PhotonRPC.LiveStrokeContinue`). Confirmed fixed via on-device diagnostic logging across multiple full strokes (`[LiveStrokeDiag]` logs added, then fully removed once confirmed).
- `SketchMemoryScript.MemorizeBrushStroke` now sets `rNewStroke.m_Seed` (previously left at the int default `0`) — needed to correlate a completed authoritative stroke back to its live-preview seed, also incidentally fixes a pre-existing `DeleteStroke` seed-ambiguity issue.

## Known gaps / next steps

- Ghost markers are still spheres, not real headset/controller models.
- Hide-remote-avatars toggle not confirmed working during replay specifically (works live, per `meta-colocation`).
- No pause/seek, and no "lock camera to a recorded player's POV" — both flagged as follow-ups in the original design discussion, not built.
- No UI for recording/replay — API-only right now (`recording.start`/`stop`, `replay.list`/`start`).
- **Not committed.** Also present in the same dirty working tree, unrelated to this feature and should be excluded from any commit: `RuntimeActionBindings.json` (MX Ink stylus binding wipe), `Assets/Resources/Environments/CustomSkybox.mat` (`_Tint` change), `Assets/XR/Settings/Open XR Package Settings.asset` (GUID reorder noise), `Assets/Scripts/Input/UnityXRInputAction.cs` (unexplained guard removal) — see `meta-colocation`'s own notes, these have been carried as uncommitted noise since before this branch existed.
