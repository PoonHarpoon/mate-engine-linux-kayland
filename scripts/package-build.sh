#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s <build_dir>\n' "$0" >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd -- "$script_dir/.." && pwd)"
output_dir="$1"

if [[ ! -d "$output_dir" ]]; then
    printf 'Build directory does not exist: %s\n' "$output_dir" >&2
    exit 1
fi
if ! command -v nix >/dev/null 2>&1; then
    printf 'Nix is required to package the native Wayland presenter.\n' >&2
    exit 1
fi

presenter_link="$output_dir/.matee-wayland-presenter-runtime"
nix build "path:$project_dir#wayland-presenter" --out-link "$presenter_link"
presenter_store="$(readlink -f -- "$presenter_link")"
install -m 0755 "$presenter_store/bin/matee-wayland-presenter" "$output_dir/matee-wayland-presenter"
printf 'Packaged native Wayland presenter: %s\n' "$output_dir/matee-wayland-presenter"
