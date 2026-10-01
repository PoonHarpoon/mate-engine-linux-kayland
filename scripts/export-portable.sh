#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 3 ]]; then
    printf 'Usage: nix run .#export-portable -- <player-build-directory> <new-output-directory>\n' >&2
    exit 2
fi

flake_root="$1"
source_dir="$(realpath -- "$2")"
output_dir="$(realpath -m -- "$3")"

if [[ ! -x "$source_dir/MateEngineX.x86_64" || ! -f "$source_dir/UnityPlayer.so" ||
      ! -d "$source_dir/MateEngineX_Data" || ! -f "$source_dir/launch.sh" ]]; then
    printf 'Incomplete Unity player build: %s\n' "$source_dir" >&2
    exit 1
fi
if [[ -e "$output_dir" || -L "$output_dir" ]]; then
    printf 'Output already exists; refusing to overwrite: %s\n' "$output_dir" >&2
    exit 1
fi
if ! command -v nix >/dev/null 2>&1; then
    printf 'Nix is required on the exporting machine.\n' >&2
    exit 1
fi

output_parent="$(dirname -- "$output_dir")"
mkdir -p -- "$output_parent"
stage="$(mktemp -d "$output_parent/.matee-portable.XXXXXXXX")"
trap 'printf "Export did not complete; staging files remain at %s\n" "$stage" >&2' ERR

cp -a -- "$source_dir/MateEngineX.x86_64" "$source_dir/UnityPlayer.so" \
    "$source_dir/MateEngineX_Data" "$source_dir/launch.sh" "$stage/"
for library in libdecor-0.so.0 libdecor-cairo.so; do
    if [[ -f "$source_dir/$library" ]]; then
        cp -a -- "$source_dir/$library" "$stage/"
    fi
done

# Pin the bundler: its AppImage embeds the presenter's Qt/Nix runtime rather
# than leaving an executable that points into the exporting host's /nix/store.
nix bundle --no-write-lock-file \
    --bundler github:ralismark/nix-appimage/7946addbc0d97e358a6d7aefe5e82310f0fe6b18 \
    "path:$flake_root#packages.x86_64-linux.wayland-presenter" \
    --out-link "$stage/.presenter-appimage"
cp -L -- "$stage/.presenter-appimage" "$stage/matee-wayland-presenter"
chmod 0755 "$stage/matee-wayland-presenter"
unlink -- "$stage/.presenter-appimage"

if find "$stage" -type l -print -quit | grep -q .; then
    printf 'Export contains a symlink; inspect staging directory: %s\n' "$stage" >&2
    exit 1
fi

mv -- "$stage" "$output_dir"
trap - ERR
printf 'Portable player directory: %s\n' "$output_dir"
printf 'On a compatible KDE Plasma 6 Wayland system: cd %q && env -u DISPLAY ./launch.sh\n' "$output_dir"
