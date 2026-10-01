#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd -- "$script_dir/.." && pwd)"
source_dir="$project_dir/Native/WaylandPresenter"
build_dir="$source_dir/build"

cmake -S "$source_dir" -B "$build_dir" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build_dir" --parallel
printf 'Built native Wayland presenter: %s\n' "$build_dir/matee-wayland-presenter"
