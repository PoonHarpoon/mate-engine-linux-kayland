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
"$unity_editor" -batchmode -quit -nographics -projectPath "$script_dir" \
    -job-worker-count "$job_worker_count" -logFile "$build_log" \
    -executeMethod CliBuilder.Build --output "$output_path"

if [[ "${MATEENGINE_SKIP_PRESENTER_BUILD:-0}" != 1 ]]; then
    if ! command -v nix >/dev/null 2>&1; then
        printf 'Nix is required to package the native Wayland presenter.\n' >&2
        exit 1
    fi
    output_dir="$(dirname -- "$output_path")"
    presenter_link="$output_dir/.matee-wayland-presenter-runtime"
    nix build "path:$script_dir#wayland-presenter" --out-link "$presenter_link"
    presenter_store="$(readlink -f -- "$presenter_link")"
    install -m 0755 "$presenter_store/bin/matee-wayland-presenter" "$output_dir/matee-wayland-presenter"
    printf 'Packaged native Wayland presenter: %s\n' "$output_dir/matee-wayland-presenter"
fi
