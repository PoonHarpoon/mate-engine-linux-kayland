{
  description = "Mate Engine Linux development environment";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.05";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
      pkgsFor = system: import nixpkgs {
        inherit system;
        config.allowUnfreePredicate = pkg:
          builtins.elem (nixpkgs.lib.getName pkg) [ "unity-editor" "unityhub" ];
      };
    in {
      packages = forAllSystems (system:
        let
          pkgs = pkgsFor system;
          editor = pkgs.stdenvNoCC.mkDerivation {
            pname = "unity-editor";
            version = "6000.2.6f2";
            src = pkgs.fetchurl {
              url = "https://download.unity3d.com/download_unity/4a4dcaec6541/LinuxEditorInstaller/Unity-6000.2.6f2.tar.xz";
              hash = "sha256-Lu30truvOQeZ5ZALJ3JF0GQjGBo6ZvfViCHehM508DY=";
            };
            sourceRoot = ".";
            dontConfigure = true;
            dontBuild = true;
            # Preserve Unity's bundled runtimes and native plugins; the FHS
            # launcher supplies their dynamic linker and shared libraries.
            dontFixup = true;
            installPhase = ''
              runHook preInstall
              mkdir -p "$out"
              mv Editor "$out/Editor"
              test -x "$out/Editor/Unity"
              test -d "$out/Editor/Data/PlaybackEngines/LinuxStandaloneSupport"
              runHook postInstall
            '';
            meta = {
              description = "Unity Editor with bundled Linux Mono build support";
              license = pkgs.lib.licenses.unfree;
              platforms = systems;
              sourceProvenance = [ pkgs.lib.sourceTypes.binaryNativeCode ];
            };
          };
          unity = pkgs.buildFHSEnv {
            name = "unity";
            targetPkgs = p: with p; [
              bash coreutils git clang gnumake pkg-config cacert xdg-utils
              gtk3 glib gdk-pixbuf pango cairo atk at-spi2-core
              fontconfig freetype harfbuzz dejavu_fonts liberation_ttf
              gsettings-desktop-schemas hicolor-icon-theme
              alsa-lib libpulseaudio dbus libsecret libnotify libuuid udev
              nss nspr openssl krb5 lttng-ust_2_12 icu libxml2 zlib
              cups expat libcap libxkbcommon libdrm libgbm libglvnd mesa
              vulkan-loader wayland libva libayatana-appindicator
              xorg.libX11 xorg.libXext xorg.libXrender xorg.libXrandr
              xorg.libXdamage xorg.libXcursor xorg.libXcomposite
              xorg.libXfixes xorg.libXi xorg.libXtst xorg.libXScrnSaver
              xorg.libxcb xorg.libxshmfence xorg.libICE xorg.libSM
            ];
            multiPkgs = _: [ ];
            # Unity preserves bundled-package modes when copying them into a
            # project, then edits package.json. Store modes would make that
            # copy read-only. Prepare a versioned cache with writable modes,
            # and mount it read-only over just the bundled-package directory.
            extraPreBwrapCmds = ''
              unity_cache_root="''${XDG_CACHE_HOME:-$HOME/.cache}/mate-engine/unity"
              unity_cache_root=$(${pkgs.coreutils}/bin/realpath -m "$unity_cache_root")
              ${pkgs.coreutils}/bin/mkdir -p -m 700 "$unity_cache_root" || exit 1
              unity_builtin_cache="$unity_cache_root/${builtins.baseNameOf (toString editor)}/BuiltInPackages"
              (
                set -eu
                umask 077
                ${pkgs.coreutils}/bin/mkdir -p "$unity_cache_root/${builtins.baseNameOf (toString editor)}"
                ${pkgs.util-linux}/bin/flock 9
                if [ ! -d "$unity_builtin_cache" ]; then
                  unity_stage=$(${pkgs.coreutils}/bin/mktemp -d "$unity_cache_root/.packages-XXXXXX")
                  trap '${pkgs.coreutils}/bin/rm -rf -- "$unity_stage"' EXIT
                  ${pkgs.coreutils}/bin/cp -r --reflink=auto \
                    ${editor}/Editor/Data/Resources/PackageManager/BuiltInPackages/. "$unity_stage/"
                  ${pkgs.coreutils}/bin/chmod -R u+w "$unity_stage"
                  ${pkgs.coreutils}/bin/mv "$unity_stage" "$unity_builtin_cache"
                fi
              ) 9>"$unity_cache_root/.lock" || exit 1
            '';
            extraBwrapArgs = [
              ''--ro-bind "$unity_builtin_cache" ${editor}/Editor/Data/Resources/PackageManager/BuiltInPackages''
            ];
            # Use matching Nix Mesa libraries/drivers on Arch instead of
            # loading host drivers built against a different libc.
            profile = ''
              export LIBGL_DRIVERS_PATH="''${LIBGL_DRIVERS_PATH:-${pkgs.mesa}/lib/dri}"
              export __EGL_VENDOR_LIBRARY_DIRS="''${__EGL_VENDOR_LIBRARY_DIRS:-${pkgs.mesa}/share/glvnd/egl_vendor.d}"
              export XDG_DATA_DIRS="${pkgs.mesa}/share:''${XDG_DATA_DIRS:-/usr/local/share:/usr/share}"
            '';
            runScript = "${editor}/Editor/Unity";
          };
          waylandPresenter = pkgs.stdenv.mkDerivation {
            pname = "matee-wayland-presenter";
            version = "0.1.0";
            src = pkgs.lib.cleanSourceWith {
              src = ./Native/WaylandPresenter;
              filter = path: type:
                type != "directory" || builtins.baseNameOf path != "build";
            };
            nativeBuildInputs = [ pkgs.cmake pkgs.qt6.wrapQtAppsHook ];
            buildInputs = [
              pkgs.qt6.qtbase
              pkgs.qt6.qtwayland
              pkgs.kdePackages.layer-shell-qt
            ];
            meta.mainProgram = "matee-wayland-presenter";
          };
        in {
          inherit unity;
          wayland-presenter = waylandPresenter;
          unity-editor = editor;
          unityhub = pkgs.unityhub;
          default = unity;
        });

      apps = forAllSystems (system:
        let pkgs = pkgsFor system;
        in {
          unity = {
            type = "app";
            program = "${self.packages.${system}.unity}/bin/unity";
          };
          unityhub = {
            type = "app";
            program = "${self.packages.${system}.unityhub}/bin/unityhub";
          };
          wayland-presenter = {
            type = "app";
            program = "${self.packages.${system}.wayland-presenter}/bin/matee-wayland-presenter";
          };
          export-portable = {
            type = "app";
            program = "${pkgs.writeShellScriptBin "export-portable" ''
              exec ${pkgs.bash}/bin/bash ${./scripts/export-portable.sh} ${self} "$@"
            ''}/bin/export-portable";
          };
          default = self.apps.${system}.unity;
        });

      devShells = forAllSystems (system:
        let pkgs = pkgsFor system;
        in {
          default = pkgs.mkShell {
            GI_TYPELIB_PATH = "${pkgs.glib.out}/lib/girepository-1.0";
            packages = with pkgs; [
              bashInteractive cmake pkg-config gnumake gcc nodejs patchelf
              (python3.withPackages (p: [ p.dbus-python p.pygobject3 ]))
              gtk3 glib libayatana-appindicator
              xorg.libX11 xorg.libXext xorg.libXrender xorg.libXrandr xorg.libXdamage xorg.libXcursor xorg.libXcomposite
              libpulseaudio wayland wayland-protocols vulkan-tools
              mesa-demos xorg.xdpyinfo dbus cargo rustc
              qt6.qtbase qt6.qtwayland kdePackages.layer-shell-qt
            ] ++ [ self.packages.${system}.unity ];
          };
        });
    };
}
