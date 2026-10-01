# Window sitting validation

Run from the repository root. Unity Editor 6000.2.6f2 and the flake development
environment are required for the player checks. Do not change global display or
compositor settings automatically to satisfy this matrix.

## Automated checks

```sh
nix develop -c node tests/desktop-observer.test.cjs
nix develop -c unity -batchmode -quit -nographics -projectPath "$PWD" \
  -job-worker-count 4 -logFile /tmp/matee-sitting-tests.log \
  -executeMethod WaylandSittingTests.Run
nix develop -c unity -batchmode -quit -nographics -projectPath "$PWD" \
  -job-worker-count 4 -logFile /tmp/matee-sitting-handler-tests.log \
  -executeMethod WaylandSittingTests.RunHandler
nix develop -c bash -c 'cmake -S Native/WaylandPresenter -B /tmp/matee-presenter-tests -DBUILD_TESTING=ON && cmake --build /tmp/matee-presenter-tests -j4 && ctest --test-dir /tmp/matee-presenter-tests --output-on-failure'
nix develop -c ./build.sh build
```

The observer test exercises events, visibility, panel auto-hide, desktop/activity
membership, stacking, and capability loss. The Unity check covers immutable
snapshots, stable IDs, removed handles, seat eligibility, malformed input, and
fractional geometry. The handler regression replays geometry from a sitting transition that moved the
hip outside the snap band with a stationary cursor, verifies held dragging preserves
seat alignment, and checks incremental sliding and intentional drag-away. The native test verifies protocol rejection and conservative
input masks under scaling and vertical flipping.

An optional live test uses Python with dbus-python and PyGObject from the Nix
development shell. It briefly displays a test
rectangle on each attached output, checks KWin geometry and layer state, and
checks that stale frames stop the heartbeat. It loads and unloads its own
read-only KWin observer and leaves diagnostic evidence in a unique `/tmp` folder.
If KWin has an occupied script object path, the test retains that attempt and
tries the next script ID, matching the player's startup recovery.
It does not test the animated pet's seat alignment.

```sh
nix develop -c python3 tests/wayland-presenter-live.py ./build/matee-wayland-presenter
```

## Interactive acceptance

Launch the packaged player from `build/`:

```sh
env -u DISPLAY ./launch.sh -logFile "$PWD/Player.log"
```

Record the commit/build, command, session type, Wayland display, DISPLAY presence,
KWin version, output geometry/scales, Unity graphics backend, and the Unity log
line `Selected window backend: wayland`. Startup should also report
`KWin desktop observer ready` and the presenter handoff. Do not publish unrelated
environment variables, private application titles, or personal settings.

| Scenario | Required result |
| --- | --- |
| Drag held less than one second | No new seat attachment |
| Exposed application top edge | Snap, release, and stable sitting alignment |
| Drag along edge / away | Slide / detach without fighting cursor movement |
| Move and resize supporting window | Follow position and horizontal seat fraction |
| Overlap with dialog or application | Stay attached; covered pixels are invisible and click-through |
| Close, minimize, maximize, fullscreen | Detach in place; no automatic reattachment |
| Visible floating or edge panel | Sit on its top edge if there is room above |
| Panel auto-hide | Detach; never force panel visibility |
| Desktop/activity switch, show desktop | Detach from surfaces no longer visible |
| Always-on-top off | Temporary overlay while seated, configured layer restored on detach |
| Observer interruption and restart | Detach after expiry; fresh detection resumes without old targets |
| Presenter interruption / stalled frames | Unity source returns; no invisible input-blocking overlay |
| Each output and negative origins | Correct logical alignment, sizing, masks, and pointer mapping |
| Cross-output motion | Hold a drag across outputs and release once; pet drops immediately and can be dragged again on the destination output. |
| Ordinary dragging, menus, scale changes | Existing input, transparency, and lifecycle behavior preserved |

The user accepted window sitting and detection, fractional scaling, and expected
performance / parity on their KDE native Wayland setup on September 30, 2026.
These acceptance items are complete for that setup. Exact display scales, GPU
details, and benchmark measurements were not recorded with the confirmation;
the result does not establish every scenario in the matrix on every configuration.
Use the matrix above for regression checks and additional display configurations.
Synthetic geometry and successful builds do not substitute for interactive
acceptance on those configurations.

## Diagnosing a rejected snap

From `build/`, run `MATEENGINE_SITTING_DIAGNOSTICS=1 env -u DISPLAY ./launch.sh -logFile /tmp/matee-sitting.log`.
Hold a drag for at least one second, move at least four logical pixels, and slowly
cross an exposed window top edge with the avatar's hips. The room-above check can
reject edges near the top of an output; start with a restored window lower down.

`Sitting decision:` lines identify initialization, setting, drag, hold, projection,
clearance, and occlusion failures. They include both movement and avatar drag states,
snapshot freshness, probe coordinates, nearest candidate edge, and required clearance.
Repeated unchanged decisions are limited to one per second. Attachment and detach
reasons are logged separately. Window titles and avatar file paths are not included
in these diagnostic lines; review other player log messages before sharing a full log.
Disable the environment variable after diagnosis. An observer-ready message or a
presenter drag message alone does not prove that the avatar has attached to a seat.

Native Wayland snap tolerance scales with the logical height of the monitor containing
the hip probe: `40 × monitorHeight / 1440` logical pixels above or below the edge
(30 at 1080, 40 at 1440, 60 at 2160). Avatar size does not change this tolerance.
The same tolerance is used for the drag-away band. At output boundaries, the probe's
monitor determines the tolerance; outside all outputs, the nearest output is used.
Fractional output scale is already reflected in logical geometry and is not applied twice.
