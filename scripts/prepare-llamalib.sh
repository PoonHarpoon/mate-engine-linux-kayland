#!/usr/bin/env bash
# Keep LLMUnity from re-downloading LlamaLib while Unity builds the player.
#
# LLMUnity downloads LlamaLib into StreamingAssets on every Editor start unless
# setup/<archive>.complete exists. Its pre-build step moves that setup folder
# (and non-target libraries) into a temporary directory that is discarded, so
# the next Editor start downloads the multi-platform archive again, racing the
# build and bundling every platform. Recreating the marker when the Linux
# libraries are already present keeps builds offline and Linux-only.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd -- "$script_dir/.." && pwd)"
streaming_dir="$project_dir/Assets/StreamingAssets"

shopt -s nullglob
library_dirs=("$streaming_dir"/undreamai-*-llamacpp)
if (( ${#library_dirs[@]} == 0 )); then
    printf 'LlamaLib is not installed yet; Unity will download it during this build.\n' >&2
    printf 'If the player then bundles non-Linux libraries, run the build again.\n' >&2
    exit 0
fi
if (( ${#library_dirs[@]} > 1 )); then
    printf 'Multiple LlamaLib versions found in %s; leaving them to LLMUnity.\n' "$streaming_dir" >&2
    exit 0
fi

library_dir="${library_dirs[0]}"
linux_dirs=("$library_dir"/linux-*/)
if (( ${#linux_dirs[@]} == 0 )); then
    printf 'LlamaLib at %s has no Linux libraries; Unity will download them.\n' "$library_dir" >&2
    exit 0
fi

marker="$library_dir/setup/$(basename -- "$library_dir").zip.complete"
if [[ ! -e "$marker" ]]; then
    mkdir -p -- "$(dirname -- "$marker")"
    : > "$marker"
    printf 'Marked existing LlamaLib as installed: %s\n' "$marker"
fi
