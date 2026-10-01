#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
binary="${MATEENGINE_BINARY:-$script_dir/MateEngineX.$(uname -m)}"
backend="${MATEENGINE_BACKEND:-wayland}"

if [[ ! -x "$binary" ]]; then
    printf 'MateEngine executable not found or not executable: %s\n' "$binary" >&2
    printf 'Set MATEENGINE_BINARY to the Unity Linux player executable.\n' >&2
    exit 1
fi

if [[ "$backend" != wayland ]]; then
    printf 'Mate Engine is native-Wayland-only; MATEENGINE_BACKEND must be wayland.\n' >&2
    exit 2
fi
if [[ "${XDG_SESSION_TYPE:-}" != wayland || -z "${WAYLAND_DISPLAY:-}" ]]; then
    printf 'Native Wayland requires an active Wayland session (XDG_SESSION_TYPE=wayland and WAYLAND_DISPLAY set).\n' >&2
    exit 1
fi

export MATEENGINE_BACKEND=wayland SDL_VIDEODRIVER=wayland GDK_BACKEND=wayland
# SDL otherwise declares the whole Wayland surface opaque even when Unity
# presents an alpha-capable buffer.
export SDL_VIDEO_EGL_ALLOW_TRANSPARENCY=1
printf 'MateEngine backend request: native Wayland\n'

presenter="${MATEENGINE_PRESENTER_BINARY:-$(dirname -- "$binary")/matee-wayland-presenter}"
if [[ ! -x "$presenter" ]]; then
    presenter="$(command -v matee-wayland-presenter || true)"
fi
if [[ -z "$presenter" || ! -x "$presenter" ]]; then
    printf 'Native Wayland presenter not found beside the player or on PATH.\n' >&2
    printf 'Rebuild with ./build.sh, or set MATEENGINE_PRESENTER_BINARY.\n' >&2
    exit 1
fi

runtime_parent="${XDG_RUNTIME_DIR:-}"
if [[ -z "$runtime_parent" || ! -d "$runtime_parent" ]]; then
    printf 'XDG_RUNTIME_DIR is required for private presenter transport.\n' >&2
    exit 1
fi
transport_dir="$(mktemp -d "$runtime_parent/matee-presenter.XXXXXX")"
frame_file="$transport_dir/frames"
input_file="$transport_dir/input"
presenter_pid=""
player_pid=""
cleanup() {
    trap - EXIT INT TERM
    if [[ -n "$presenter_pid" ]]; then kill "$presenter_pid" 2>/dev/null || true; fi
    if [[ -n "$player_pid" ]]; then kill "$player_pid" 2>/dev/null || true; fi
    rm -rf -- "$transport_dir"
}
trap cleanup EXIT INT TERM

env -u DISPLAY QT_QPA_PLATFORM=wayland "$presenter" \
    --frame-file "$frame_file" --input-file "$input_file" &
presenter_pid=$!
export MATEENGINE_PRESENTER_PID="$presenter_pid"
export MATEENGINE_PRESENTER_FRAME_FILE="$frame_file"
export MATEENGINE_PRESENTER_INPUT_FILE="$input_file"
"$binary" -force-wayland "$@" &
player_pid=$!
if wait "$player_pid"; then status=0; else status=$?; fi
player_pid=""
exit "$status"
