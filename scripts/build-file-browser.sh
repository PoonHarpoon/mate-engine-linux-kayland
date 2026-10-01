#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd -- "$script_dir/.." && pwd)"
source_dir="$project_dir/Plugins/StandaloneFileBrowser"
build_dir="$source_dir/build"
destination="$project_dir/Assets/MATE ENGINE - Packages/StandaloneFileBrowser/Plugins/Linux/x86_64/libStandaloneFileBrowser.so"

if [[ "${MATEENGINE_PORTABLE_PLUGIN:-0}" == 1 ]]; then
    # Use the destination host's loader and GTK, without development RPATHs.
    command -v patchelf >/dev/null
    # Nix dev shells inject a local output-directory RPATH. Avoid creating
    # that string: removing its ELF tag later can leave unused bytes behind.
    read -r -a linker_flags <<< "${NIX_LDFLAGS:-}"
    portable_flags=()
    for flag in "${linker_flags[@]}"; do
        if [[ "$flag" == */outputs/out/lib ]]; then
            count=${#portable_flags[@]}
            if (( count > 0 )) && [[ "${portable_flags[count-1]}" == -rpath || "${portable_flags[count-1]}" == -L ]]; then
                unset 'portable_flags[count-1]'
            fi
        else
            portable_flags+=("$flag")
        fi
    done
    export NIX_LDFLAGS="${portable_flags[*]}"
    make -C "$source_dir" build/dialog
    patchelf --set-interpreter /lib64/ld-linux-x86-64.so.2 --remove-rpath "$build_dir/dialog"
    touch "$build_dir/dialog"
fi
make -C "$source_dir" build/libStandaloneFileBrowser.so
if [[ "${MATEENGINE_PORTABLE_PLUGIN:-0}" == 1 ]]; then
    patchelf --remove-rpath "$build_dir/libStandaloneFileBrowser.so"
fi
install -Dm755 "$build_dir/libStandaloneFileBrowser.so" "$destination"
printf 'Installed audited StandaloneFileBrowser plugin to %s\n' "$destination"
