# Meta Quest Colocation — branch status

**Branch:** `meta-colocation` (off `main`, named to match Andy Baker's own archived `attic/meta-colocation`)
**Status: done and confirmed working on real hardware.** Single commit `b1c80ca83`.
**Detailed design doc:** [`COLOCATION_MVP_PLAN.md`](COLOCATION_MVP_PLAN.md) — Andy Baker's original 2-person MVP plan. Read that first for the underlying protocol/rationale; this file just says what actually got built and what changed from his plan.

## What this branch does

Lets multiple Quest headsets in the same Photon multiplayer room share a physical space via Meta spatial anchors, so their sketches are aligned to the same real-world room (colocation), not just the same virtual scene.

## What changed vs. Andy's original plan

Andy's plan was scoped to exactly 2 users (one host, one joiner) and left several things as known release blockers. This branch:
- **Extends host/joiner to N users.** One explicit host creates+cloud-saves the anchor once; each subsequent joiner triggers a share+RPC targeted at that joiner specifically (not a broadcast — Andy's own plan flagged broadcast as wrong, and it's actively incorrect for N-way with multiple joiners in flight).
- **Fixes the Cloud vs Local anchor storage bug**: joiner-side anchor sync was defaulting to `OVRSpace.StorageLocation.Local` (only finds anchors created on that same physical device) instead of `.Cloud` — a real bug caught during on-device testing, not present in the design.
- Kept everything else faithful to Andy's design (explicit `Host Colocation`/`Join Colocation` actions, no implicit host election, no host migration) specifically so this reads as "extended his work," not "built something different."

## Architecture

- **State machine** (`Idle → CheckingPrerequisites → RequestingScenePermission → CreatingAnchor → SavingAnchorToCloud → JoiningRoom → WaitingForPeer → SharingAnchor → WaitingForAnchor → Localizing → Active → Leaving → Failed`) built against abstracted interfaces (`IAnchorService`, `ISignallingService`) from day one, per Andy's own §5.1 recommendation — this is what makes protocol unit tests possible without a headset (not yet written, see Not done below).
- `Assets/Scripts/Multiplayer/Photon/PhotonColocationSignalling.cs` — the real `ISignallingService`. Resolves Meta user ID ↔ Photon `PlayerRef` by scanning `MultiplayerManager.m_RemotePlayers`.
- `Assets/Scripts/Multiplayer/Colocation/ColocationBootstrap.cs` — scene-resident `MonoBehaviour` owning the `ColocationManager` instance, exposes `Host()`/`Join()`/`Leave()`. Instantiates `Assets/OculusMR/OculusMR.prefab` at runtime, entirely inside `#if OCULUS_SUPPORTED` — that prefab carries Meta's `OVRSceneManager`, which has unconditional lifecycle methods not gated by our own platform defines, so it must never be placed statically in a scene.
- 4 new Photon RPCs in `PhotonRPC.cs` (`RPC_ColocationHostReady`, `RPC_ColocationRequestToJoin`, `RPC_ColocationAnchorUuid`, `RPC_ColocationLocalizationResult`) — only the anchor UUID RPC is targeted at a specific joiner, the other three are broadcast (safe here since receivers self-filter by role/state).
- API commands `colocation.host` / `colocation.join` / `colocation.leave` (`Assets/Scripts/API/ApiMethods.Multiplayer.cs`) — HTTP-triggerable, useful for scripted/remote testing without touching the UI.
- UI: `Host Colocation` / `Join Colocation` buttons and a local `hide remote avatars` toggle on `MultiplayerPanel` (`GlobalCommands.MultiplayerColocationHost/Join`, `MultiplayerToggleHideRemoteAvatars`, new `MultiplayerPanelToggleButton.cs`).

## Confirmed working on real hardware (2026-08-01)

Full flow via the real UI buttons (not just the API): host → `CreatingAnchor → Localizing → Active`, joiner → `WaitingForPeer → WaitingForAnchor → Active`, anchor shared and loaded, both sides confirm colocation. Hide-remote-avatars toggle (headset mesh, controller models, and nickname label) also confirmed working both live.

## Known constraints / not done

- **Quest/Android-only** — Meta's Scene/Spatial-Anchor services aren't available over Link, and Oculus Platform Core doesn't initialize on PC in this project at all.
- **Temporary test identity**: builds currently ship under `uk.org.collusion.MRMotifs` (a self-owned app registration used purely because the real Open Brush Meta App ID has no entitlement on the dev Meta account testing this). Search `BuildTiltBrush.cs` for `TEMP DEV WORKAROUND` and revert both that and `Assets/Resources/OculusPlatformSettings.asset`'s `ovrMobileAppID` before any real release build.
- **No host migration** — if the anchor host leaves mid-session, already-colocated users keep working but no new joins can happen until someone re-hosts. Matches Andy's explicit MVP scope.
- **Unresolved, real bug**: taking a headset outside the Guardian/roomscale boundary (even with the proximity sensor disabled) disconnects it from the Photon room after ~10s. Root cause not confirmed — `PhotonManager.OnShutdown`'s `shutdownReason` is now logged to help nail this down next time it's reproduced.
- **Phase 5 (unit tests) not written** — the interface abstraction makes this possible without a headset, just not done yet.
- Two files with unrelated-looking changes were deliberately left uncommitted rather than included in `b1c80ca83` — worth checking before assuming they're needed: `RuntimeActionBindings.json` (wipes MX Ink stylus bindings — looks like an accidental regression) and `Assets/Resources/Environments/CustomSkybox.mat` (`_Tint` changed to black).

## Also on this branch

[`flight-recorder`](https://github.com/richhall/open-brush) branches off this one and builds on the same anchor/`PhotonPlayerRig` infrastructure — see its own `FLIGHT_RECORDER.md` for what it adds.
