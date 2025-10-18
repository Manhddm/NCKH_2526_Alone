# NCKH_2526_Alone

Movement and networking use server-authoritative simulation with client-side prediction + reconciliation for owners, and custom interpolation for non-owners. NetworkTransform is not used.

Key Notes
- Owner prediction: inputs are buffered with sequence numbers; the client predicts immediately and later reconciles with authoritative snapshots.
- Render smoothing: the visible transform smooths toward the predicted motor state to avoid owner jitter; small corrections are blended, large ones snap.
- Non-owners: interpolate snapshots ~120 ms behind server time with a small extrapolation window.
- Server-only/headless: all gameplay ticks on server; visuals and cameras are disabled.

Setup
- Player prefab should have NetworkIdentity, Rigidbody2D (kinematic is fine), CapsuleCollider2D, Animator, SpriteRenderer, and the scripts:
  - `PlayerController` (prediction, reconciliation, interpolation)
  - `PlayerCamera` (optional; attach and assign a Cinemachine vcam prefab or use an existing child virtual camera)
- Remove Mirror `NetworkTransform` from the player prefab.

Testing
- Open `Assets/Projects/Scenes/Bootstrap.unity`, run as Host + a Client.
- Add latency (e.g., 150–200 ms) and ~3% packet loss via transport/network tools.
- Owner movement should feel smooth with minimal rubber-banding; remote players should appear smooth at 60 FPS.

Owner Jitter/Stutter Fix Overview
- Separate simulation vs rendering: prediction/reconciliation ticks run at fixed dt (0.02), while visuals SmoothDamp in LateUpdate.
- Single writer to visible transform: only PlayerController.LateUpdate sets `transform.position`.
- Reconciliation smoothing: small errors blend out over ~80 ms via a correction offset; large errors hard-snap predicted + visuals to authoritative to avoid long rubber-bands.
- Host transform fights resolved: server no longer writes `transform.position` for the local player when hosting; server-only/headless can still write for correctness.
- Deterministic motor: PlayerMotor integrates with the provided `dt` only; no `Time.deltaTime` usage.
- Rigidbody2D on clients is kinematic, simulated=false, interpolation=None (visual smoothing is custom).

Tuning Knobs (PlayerController)
- `visualSmoothTime` (default 60 ms): visual SmoothDamp time toward the predicted state.
- `correctionSmoothTime` (default 80 ms): how quickly small reconciliation errors blend out.
- `smallCorrectionDistance` (default 0.2): threshold under which errors are blended, not snapped.
- `snapDistance` (default 0.5): immediate teleport if visual-to-predicted distance exceeds this.
- `interpolationDelay` (default 120 ms): remote interpolation lookback for non-owners.
- `inputSendRate` (default 50 Hz) and `inputRateCapPerSecond`: owner input cadence.
- `MaxReplaysPerFrame` (hardcoded 5): cap replays to avoid hitches.

Debug Overlay
- Toggle in inspector: `showDebugOverlay`.
- Shows: current RTT, snapshot counts, next input seq, predicted/visual/auth positions, correction magnitude, client/server flags.

Gizmos (Editor)
- Draws spheres for Predicted (green), Visual (yellow), Authoritative (red) positions to help diagnose offsets.

Camera (per-client)
- `PlayerCamera` spawns/enables a local-only Cinemachine Virtual Camera on `OnStartLocalPlayer` and sets Follow/LookAt to the player transform (render position).
- Headless/server-only is guarded (no camera/graphics code).

Project/Inspector Checklist
- Player prefab: remove `NetworkTransform`.
- Rigidbody2D: Interpolation=None, isKinematic=true, Simulated=false (on clients).
- Animator: applyRootMotion=false (set in code).
- Physics 2D: Project Settings → Physics 2D → uncheck "Auto Sync Transforms".
- Quality: set `Application.targetFrameRate=60` and `vSyncCount=0` for consistent testing cadence (optional; set in your bootstrap if desired).

Test Steps
1) Run as Host + one remote Client. Move the owner horizontally and jump; observe smooth visuals.
2) Introduce RTT 120–200 ms and small packet loss. Owner should remain smooth with minimal micro-stutter.
3) Stress inputs (tap and hold) to verify capped replays avoid hitches.
4) Confirm only LateUpdate writes to the transform (via debug overlay and code inspection).

