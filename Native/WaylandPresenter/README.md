# Native Wayland presenter

This is the native KDE Wayland presentation foundation for Mate Engine. It owns
a `zwlr_layer_shell_v1` overlay surface with an alpha channel and an independent
input region. It intentionally reserves no panel/work area. An exclusive zone
of -1 positions against the whole output, including panel space, without
reserving space. Keyboard focus is
requested on demand only while Avatar Controls is open.

This is the project's default and only supported presentation backend. The
integrated player/presenter path completed its first native Wayland milestone: RGBA transport, alpha-derived
click-through, pointer and wheel input, dragging, scaling, position and topmost
propagation, clean exit, and failure recovery were validated on KDE Plasma 6.
The sitting integration uses KWin desktop snapshots and explicit output binding.
The user has accepted sitting and detection, fractional scaling, and expected
performance / parity on their KDE native Wayland setup. See
[validation](VALIDATION.md) for the acceptance scope, automated checks, and
interactive regression matrix.

Without arguments the executable renders a translucent feasibility-test shape.
With `--frame-file PATH`, it consumes the versioned, double-buffered RGBA stream
written by `WaylandFrameBridge` and derives an input region from pixels
whose alpha is at least 8/255. Each logical input cell must be opaque across
its covered source pixels, so scaling cannot turn a masked area into a click
target. A partially written frame is skipped.
The frame header also carries position, topmost state, and the graphics-API
dependent vertical-orientation flag. Topmost uses the layer-shell overlay layer;
disabling it uses the bottom layer so ordinary application windows can cover
Matee.

`--input-file PATH` publishes pointer state, scroll totals, lossless per-button
transition counters, and a bounded Unicode text-event ring. Unity installs the
pointer source through `BaseInput.inputOverride` and delivers text to focused
Avatar Controls fields. A 250 ms heartbeat lets the
Unity bridge restore its source window if the presenter stops.

Run the wrapped, reproducible package from the repository root:

```sh
env -u DISPLAY QT_QPA_PLATFORM=wayland nix run .#wayland-presenter
```

To consume frames from a player started with the bridge environment variable:

```sh
env -u DISPLAY QT_QPA_PLATFORM=wayland nix run .#wayland-presenter -- \
  --frame-file "$XDG_RUNTIME_DIR/matee-presenter.frames"
```

For an iterative development build, enter `nix develop` and run
`scripts/build-wayland-presenter.sh`. The raw build-tree executable is not
wrapped with Qt plugin paths; use the flake app for runtime validation.

With `WAYLAND_DEBUG=client`, a successful run includes
`zwlr_layer_shell_v1.get_layer_surface` and `wl_surface.set_input_region`.

## Window sitting

Enable **window sitting** in the existing settings. Hold a drag for at least one
second and bring the pet's hip probe near an exposed window or panel top edge.
The pet follows movement and resizing, and can slide along the edge or be dragged
off. Fullscreen/maximized application windows, popups, and desktop surfaces are
not seats. Panel top edges need enough room above them for the sitting pose.

Higher windows mask the seated pet using rectangular frame bounds. Attachment
survives overlap; close, minimize, panel auto-hide, switching away from the target's
desktop/activity, and invalid desktop state detach in place. Reappearance does not
automatically reattach. While seated, the presenter temporarily uses the overlay
layer even if always-on-top is disabled, then restores the saved preference.
Window translucency, rounded corners, shadows, and compositor animations are not
pixel-accurate occluders. The presenter migrates between outputs; content extending
beyond the selected output is clipped by the compositor. During a held drag,
the old surface stays mapped to receive the button release while a new surface
displays the pet on the destination output.

The KWin observer is loaded for the player session, uses public scripting
properties, and is unloaded on exit. Snapshots contain geometry and classification,
not window titles. Missing `hidden` or `maximizeMode` capability disables sitting
with a warning while preserving native presentation. Snapshot expiry detaches the
pet and starts observer recovery; no global settings or XWayland fallback are used.

## Transport version 3

Rebuild the player and presenter together. Older frame versions are rejected.
The 256-byte little-endian header contains RGBA dimensions/stride/slot metadata
at offsets 0–55; global logical x/y/width/height floats at 56–71; flags at 72;
a NUL-terminated UTF-8 output name (128 bytes) at 80; output x/y/width/height and
scale floats at 208–227. Remaining bytes are reserved zeroes. Flags are topmost
(1), vertical flip (2), keyboard capture (4), and seated (8).

Capture freezes placement, output, layer flags, and occlusion before asynchronous
GPU readback. The presenter scales the raster into the logical rectangle and maps
pointer input back to raster pixels. It recreates the layer surface when changing
outputs outside a held drag, applying margins relative to that output's origin. Seated masks update
with every accepted frame; other masks update at 15 Hz. After two seconds without
a valid frame, the presenter hides and stops its heartbeat so Unity can restore
its source surface.
