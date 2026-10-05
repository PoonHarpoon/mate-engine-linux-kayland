# Mate Engine native Wayland usage

This fork runs Mate Engine on KDE Plasma 6 as a native Wayland application. It
does not support X11 or XWayland. The Unity player renders the scene, and the
packaged `matee-wayland-presenter` displays those frames in a transparent
layer-shell surface. The presenter also sends pointer buttons, wheel movement,
and pointer position back to Unity.

## Requirements

- KDE Plasma 6 in a Wayland session
- Nix with flakes enabled
- For the Unity Editor GUI only: a Unity account with a Unity Personal or other
  suitable license activated for Unity `6000.2.6f2`. `build.sh` needs no
  account or license.

Check the session before launching:

```sh
printf 'session=%s wayland=%s display=%s\n' \
  "$XDG_SESSION_TYPE" "${WAYLAND_DISPLAY:-unset}" "${DISPLAY:-unset}"
```

`XDG_SESSION_TYPE` must be `wayland`, and `WAYLAND_DISPLAY` must be set.

## Build a player

From a graphical session, enter the pinned development environment and build to
a local output directory:

```sh
nix develop
./build.sh ./Build
```

The first `nix develop` downloads the pinned Unity Editor and native build
dependencies. `build.sh` builds the reviewed StandaloneFileBrowser plugin, runs
Unity in batch mode, builds the native presenter, and places the presenter next
to `Build/MateEngineX.x86_64`. Batch mode needs no Unity account, Unity Hub, or
license activation.

To rebuild, run `./build.sh ./Build` again. It overwrites the player in place
and reuses the `Library/` cache. Re-export any portable copy afterwards.

`build.sh` runs Unity in batch mode with a graphics device. Building with
`-nographics`, or without GPU access, produces player data that renders a
broken splash screen, so a display and a working GPU are required for builds
you publish. The pinned Unity environment bundles Mesa for AMD and Intel GPUs.
Other drivers, such as NVIDIA's proprietary driver, need host graphics exposed
to Nix through [nixGL](https://github.com/nix-community/nixGL), for example
`nixGL ./build.sh ./Build`. `build.sh` prints a warning when the Unity log shows
that no GPU was available (`NullGfxDevice`). On a machine without one,
`MATEENGINE_BUILD_HEADLESS=1` restores the `-nographics` build for quick
iteration; `build.sh` then prints a warning, and the result should not be
published.

LLMUnity downloads its LlamaLib runtime into `Assets/StreamingAssets` on
every Editor start unless it finds its completion marker, and its own pre-build
step removes that marker. `build.sh` runs `scripts/prepare-llamalib.sh` to
restore the marker when the Linux libraries are already present, so builds stay
offline and bundle only the Linux libraries. The first build on a fresh
checkout still downloads LlamaLib; if `build.sh` then warns that the player
bundles non-Linux files, run it again.

To build from the Unity Editor GUI instead, first sign in and activate a license
with `nix run .#unityhub`, which the Editor requires, then build the plugin and
open the project:

```sh
nix develop
./scripts/build-file-browser.sh
./scripts/prepare-llamalib.sh
unity -projectPath "$PWD"
```

Choose **MateEngine > Build Linux Player...** and select `Build`. The menu uses
the same scene and build options as `build.sh`. Then package the presenter:

```sh
./scripts/package-build.sh ./Build
```

To prepare a copyable player directory intended to run without Nix on the
destination machine, run:

```sh
nix run .#export-portable -- ./Build ./MateEngine-portable
```

The output directory must not already exist. The command copies the Unity
player, its data and launcher, and replaces the Nix-store-dependent presenter
with a pinned AppImage bundle containing its Qt runtime. Run
`env -u DISPLAY ./launch.sh` from inside the exported directory. This remains
an x86-64 Linux build for KDE Plasma 6 native Wayland, not a universal Linux
binary: system graphics drivers, KWin layer-shell support, audio and GTK
libraries, and compatible host libraries for Unity's native plugins are still
required. AppImage runtime support is also needed. The exporter intentionally
omits local logs and debug output. Test the exported directory on the target
machine before distribution; that cross-system launch is not yet validated.

Rebuild the player after updating the export integration. The launcher passes
the presenter's process ID to the KWin observer so bundled presenters with an
empty window class are excluded from sitting targets and occlusion checks.
Always start exported players through `launch.sh` to supply that identity.

Useful build controls:

```sh
# Use more Unity workers on a machine with enough memory.
MATEENGINE_JOB_WORKER_COUNT=8 ./build.sh ./Build

# Use an explicitly installed matching Unity Editor.
UNITY_EDITOR_PATH=/absolute/path/to/Unity ./build.sh ./Build

# Build without a graphics device (broken splash screen; don't publish).
MATEENGINE_BUILD_HEADLESS=1 ./build.sh ./Build

# Put the Unity batch-build log somewhere else.
MATEENGINE_BUILD_LOG=/tmp/matee-build.log ./build.sh ./Build
```

The default worker count is four to avoid exhausting memory during shader-heavy
imports. The main build log is `Library/Logs/cli-build.log` unless overridden.

To build only a native component while developing:

```sh
./scripts/build-file-browser.sh
./scripts/build-wayland-presenter.sh
```

## Run the built player

Run through the launcher from the build directory:

```sh
cd Build
env -u DISPLAY ./launch.sh
```

If `launch.sh` is being run from the repository instead, point it at the player:

```sh
env -u DISPLAY \
  MATEENGINE_BINARY="$PWD/Build/MateEngineX.x86_64" \
  MATEENGINE_PRESENTER_BINARY="$PWD/Build/matee-wayland-presenter" \
  ./launch.sh
```

The launcher creates a private transport directory under `XDG_RUNTIME_DIR`,
starts the presenter with Qt's Wayland backend, and starts Unity with
`-force-wayland`. It also enables SDL's alpha-buffer transparency hint and
requests OpenGL Core (`-force-glcore`), because that hint only applies to EGL;
under Vulkan the window is opaque. Passing your own `-force-vulkan`,
`-force-glcore`, or `-force-gles*` argument overrides that default. The
temporary transport is removed when the player exits.

The presenter targets 60 captured frames per second by default. Lower values
reduce readback and memory-bandwidth cost if a particular GPU cannot sustain
that rate:

```sh
MATEENGINE_PRESENTER_FPS=30 env -u DISPLAY ./launch.sh
MATEENGINE_PRESENTER_FPS=20 env -u DISPLAY ./launch.sh
```

This setting controls Unity-to-presenter capture, not Mate Engine's in-app FPS
limit. Values from 1 through 60 are accepted.

Extra Unity player arguments can be appended to the launcher command. For
example, save a dedicated log while testing:

```sh
env -u DISPLAY ./launch.sh -logFile "$PWD/Player.log"
```

Confirm native Wayland in the log:

```sh
rg 'Selected window backend|Wayland presenter capture|GfxDevice' Player.log
```

The expected backend line contains `Selected window backend: wayland`. The
presenter capture line records the graphics API, capture rate, and whether the
frame transport needs a vertical flip.

## Controls

- Right-click the model to open its radial settings menu.
- Left-click and drag the model to move it.
- Scroll over the model to change its scale.
- Use the tray/status-notifier menu for lifecycle and window actions.

## Run only the presenter test

The presenter can show its built-in translucent test shape without Unity:

```sh
env -u DISPLAY QT_QPA_PLATFORM=wayland nix run .#wayland-presenter
```

For protocol diagnostics:

```sh
env -u DISPLAY WAYLAND_DEBUG=client QT_QPA_PLATFORM=wayland \
  nix run .#wayland-presenter 2>wayland-presenter.log
rg 'get_layer_surface|set_input_region' wayland-presenter.log
```

## Development and troubleshooting

Run the Unity Editor from the pinned environment with `nix develop` followed by
`unity -projectPath "$PWD"`. Unity Hub is used for sign-in and license
activation, not for selecting a different Editor version:

```sh
nix run .#unityhub
```

If the splash screen is broken, the player was probably built without a
graphics device (`-nographics`, `MATEENGINE_BUILD_HEADLESS=1`, or no GPU access
inside Nix). Check the build output for the GPU warning, then rebuild from a
graphical session with plain `./build.sh`, using nixGL if needed.

If the model is upside down, retain `Player.log` and check the logged graphics
API and vertical-flip decision. If animation is choppy, compare the default 60
FPS with `MATEENGINE_PRESENTER_FPS=30` while recording the GPU, driver, monitor
layout, scale factor, and resolution. Frame transport currently performs an
asynchronous GPU readback, so higher rates consume more memory bandwidth.

For stutter only while dragging, enable the opt-in timing counters and drag for
at least three seconds:

```sh
MATEENGINE_DRAG_DIAGNOSTICS=1 env -u DISPLAY ./launch.sh \
  -logFile "$PWD/Player.log" 2>presenter-drag.log
rg 'Wayland drag|KWin native drag|Matee drag presenter' Player.log presenter-drag.log
```

The Unity line reports frames slower than 33 ms, the KWin line reports move
requests and scripting latency, and the presenter line reports consumed frames,
position changes, and its longest frame gap. These logs distinguish a Unity
frame stall from lag in moving or displaying the presenter. During a working
native drag, KWin should no longer receive one move request per position
change; it should receive a single source-window move at release. A warning
about the cursor stream means the older per-move path was used instead. Keep
logs local and share only the relevant lines.

For a useful issue report, include the Git revision, exact launch command,
Plasma and KWin versions, graphics API and renderer, monitor layout and scaling,
the three display environment variables shown above, and the relevant Unity and
presenter logs. Multi-monitor, fractional-scaling, HDR, and physical-GPU results
must be reported as tested on the actual configuration rather than inferred from
a different setup.
# Portable file-browser dependencies

Build with `MATEENGINE_PORTABLE_PLUGIN=1 ./build.sh <output>` inside the pinned
Nix development environment before portable export. This mode rewrites the
embedded file dialog to use `/lib64/ld-linux-x86-64.so.2` and removes development
RPATHs from both the dialog and its plugin. The destination must provide glibc
compatible with the pinned build (2.40 or newer), GTK 3, and its shared-library
dependencies. The presenter remains self-contained in its AppImage. This mode
changes only the built native plugin; it does not change host settings.
