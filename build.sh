#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s <output_path>\n' "$0" >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output_path="$1"
if [[ "$output_path" != *.x86_64 ]]; then
    output_path="$output_path/MateEngineX.x86_64"
fi

unity_editor="${UNITY_EDITOR_PATH:-${UNITY_EDITOR:-}}"
if [[ -z "$unity_editor" ]]; then
    unity_editor="$(command -v unity || true)"
    unity_editor="${unity_editor:-$HOME/Unity/Hub/Editor/6000.2.6f2/Editor/Unity}"
fi
if [[ ! -x "$unity_editor" ]]; then
    printf 'Unity 6000.2.6f2 editor was not found at: %s\n' "$unity_editor" >&2
    printf 'Run this build inside nix develop, or set UNITY_EDITOR_PATH to an installed Unity executable.\n' >&2
    exit 1
fi

if [[ "${MATEENGINE_SKIP_FILE_BROWSER_BUILD:-0}" != 1 ]]; then
    "$script_dir/scripts/build-file-browser.sh"
fi
"$script_dir/scripts/prepare-llamalib.sh"

build_log="${MATEENGINE_BUILD_LOG:-$script_dir/Library/Logs/cli-build.log}"
job_worker_count="${MATEENGINE_JOB_WORKER_COUNT:-4}"
if [[ ! "$job_worker_count" =~ ^[1-9][0-9]*$ ]]; then
    printf 'MATEENGINE_JOB_WORKER_COUNT must be a positive integer.\n' >&2
    exit 2
fi
mkdir -p -- "$(dirname -- "$build_log")"
printf 'Reusing Unity project cache: %s\n' "$script_dir/Library"
printf 'Unity build log: %s\n' "$build_log"
printf 'Unity job workers: %s\n' "$job_worker_count"

# Unity needs a graphics device while building: with -nographics the player
# data renders a broken splash screen. MATEENGINE_BUILD_HEADLESS=1 restores the
# display-free build for quick iteration on machines without a session.
graphics_args=()
if [[ "${MATEENGINE_BUILD_HEADLESS:-0}" == 1 ]]; then
    graphics_args=(-nographics)
    printf 'WARNING: Headless build (-nographics); the player will show a broken splash screen. Do not publish it.\n' >&2
fi
"$unity_editor" -batchmode -quit "${graphics_args[@]}" -projectPath "$script_dir" \
    -job-worker-count "$job_worker_count" -logFile "$build_log" \
    -executeMethod CliBuilder.Build --output "$output_path"

# Unity falls back to a null device when it cannot reach the GPU from inside
# Nix (for example with NVIDIA's proprietary driver); that build has the same
# broken splash screen as -nographics.
if [[ "${MATEENGINE_BUILD_HEADLESS:-0}" != 1 ]] && grep -q "NullGfxDevice" "$build_log"; then
    printf 'WARNING: Unity found no GPU during the build; the player will show a broken splash screen.\n' >&2
    printf 'WARNING: Expose host graphics to Nix, for example: nixGL ./build.sh %s\n' "$1" >&2
fi

# LLMUnity should leave only Linux LlamaLib libraries in a Linux player.
shopt -s nullglob
for library_dir in "$(dirname -- "$output_path")"/*_Data/StreamingAssets/undreamai-*-llamacpp; do
    for entry in "$library_dir"/*/; do
        if [[ "$(basename -- "$entry")" != linux-* ]]; then
            printf 'WARNING: The player bundles non-Linux LlamaLib files (%s); run the build again.\n' "$(basename -- "$entry")" >&2
            break 2
        fi
    done
done
shopt -u nullglob

if [[ "${MATEENGINE_SKIP_PRESENTER_BUILD:-0}" != 1 ]]; then
    "$script_dir/scripts/package-build.sh" "$(dirname -- "$output_path")"
fi
