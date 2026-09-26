set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RID="${1:?A Linux runtime identifier is required}"
VERSION="${2:-}"
FORMAT="${3:?A package format is required}"
OUTPUT="${4:?An output directory is required}"
PUBLISHED_EXECUTABLE="${5:-}"
APPLICATION_ID="io.github.KloBraticc.Voidstrap"
DESKTOP_FILE="$ROOT/build/Packaging/Linux/voidstrap.desktop"
ICON_FILE="$ROOT/src/Voidstrap.App/Voidstrap.png"
METAINFO_FILE="$ROOT/build/Packaging/Linux/$APPLICATION_ID.metainfo.xml"
LICENSE_FILE="$ROOT/LICENSE.VOIDSTRAP"
DOTNET_COMMAND="${DOTNET_ROOT:-}/dotnet"

if [ ! -x "$DOTNET_COMMAND" ]; then
  DOTNET_COMMAND="$(command -v dotnet || true)"
fi
# The SDK is only needed when this script has to publish the application itself.
# A caller that already built the executable, such as the release pipeline, can run
# on an agent that only carries the packaging tools.
if [ -z "$DOTNET_COMMAND" ] && [ -z "$PUBLISHED_EXECUTABLE" ]; then
  echo "The .NET SDK is unavailable"
  exit 1
fi

if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:^[[:space:]]*<VoidstrapVersion>\(.*\)</VoidstrapVersion>[[:space:]]*$:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
fi

case "$RID" in
  linux-x64) DEB_ARCH="amd64"; RPM_ARCH="x86_64"; APPIMAGE_ARCH="x86_64" ;;
  linux-arm64) DEB_ARCH="arm64"; RPM_ARCH="aarch64"; APPIMAGE_ARCH="aarch64" ;;
  linux-musl-x64) DEB_ARCH="amd64"; RPM_ARCH="x86_64"; APPIMAGE_ARCH="x86_64" ;;
  linux-musl-arm64) DEB_ARCH="arm64"; RPM_ARCH="aarch64"; APPIMAGE_ARCH="aarch64" ;;
  *) echo "Unsupported Linux runtime identifier"; exit 1 ;;
esac

case "$FORMAT" in
  deb|rpm|appimage|tar) ;;
  *) echo "Unsupported package format"; exit 1 ;;
esac

case "$RID:$FORMAT" in
  linux-musl-x64:deb|linux-musl-x64:rpm|linux-musl-x64:appimage|linux-musl-arm64:deb|linux-musl-arm64:rpm|linux-musl-arm64:appimage)
    echo "This package format requires a glibc runtime identifier"
    exit 1
    ;;
esac

APPIMAGE_TOOL=""
case "$FORMAT" in
  deb) command -v dpkg-deb >/dev/null 2>&1 || { echo "The Debian package tool is unavailable"; exit 1; } ;;
  rpm) command -v rpmbuild >/dev/null 2>&1 || { echo "The RPM package tool is unavailable"; exit 1; } ;;
  appimage)
    APPIMAGE_TOOL="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
    [ -n "$APPIMAGE_TOOL" ] && [ -f "$APPIMAGE_TOOL" ] || { echo "The AppImage package tool is unavailable"; exit 1; }
    ;;
  tar) command -v tar >/dev/null 2>&1 || { echo "The tar package tool is unavailable"; exit 1; } ;;
esac

if [[ ! "$VERSION" =~ ^[0-9]+([.][0-9]+){1,3}$ ]]; then
  echo "The package version is invalid"
  exit 1
fi

mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
case "$FORMAT" in
  deb) FINAL_TARGET="$OUTPUT/Voidstrap_${VERSION}_${DEB_ARCH}.deb" ;;
  rpm) FINAL_TARGET="$OUTPUT/Voidstrap_${VERSION}_${RPM_ARCH}.rpm" ;;
  appimage) FINAL_TARGET="$OUTPUT/Voidstrap_${VERSION}_${APPIMAGE_ARCH}.AppImage" ;;
  tar) FINAL_TARGET="$OUTPUT/Voidstrap_${VERSION}_${RID}.tar.gz" ;;
esac
ZSYNC_FINAL_TARGET="${FINAL_TARGET}.zsync"
if [ -e "$FINAL_TARGET" ]; then
  echo "The requested output already exists"
  exit 1
fi
if [ "$FORMAT" = "appimage" ] && [ -e "$ZSYNC_FINAL_TARGET" ]; then
  echo "The requested update information already exists"
  exit 1
fi
LOCK="$OUTPUT/.voidstrap-linux-$RID.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "Another package operation is already running"
  exit 1
fi
# The bundled libraries decide how old a distribution the AppImage still runs on, so
# they are harvested from a sysroot rather than from whatever the build agent happens
# to have. VOIDSTRAP_APPIMAGE_SYSROOT_DIR holds one sysroot per AppImage architecture.
# Debian 11 is the oldest distribution the packages target, so its glibc is the ceiling.
APPIMAGE_GLIBC_CEILING="${VOIDSTRAP_APPIMAGE_GLIBC_CEILING:-GLIBC_2.31}"
APPIMAGE_SYSROOT=""
APPIMAGE_SYSROOT_REQUESTED=0
if [ -n "${VOIDSTRAP_APPIMAGE_SYSROOT:-}" ]; then
  APPIMAGE_SYSROOT="$VOIDSTRAP_APPIMAGE_SYSROOT"
  APPIMAGE_SYSROOT_REQUESTED=1
elif [ -n "${VOIDSTRAP_APPIMAGE_SYSROOT_DIR:-}" ]; then
  APPIMAGE_SYSROOT="$VOIDSTRAP_APPIMAGE_SYSROOT_DIR/$APPIMAGE_ARCH"
  APPIMAGE_SYSROOT_REQUESTED=1
fi

case "$APPIMAGE_ARCH" in
  x86_64) APPIMAGE_SYSROOT_DEBIAN_ARCH="amd64" ;;
  aarch64) APPIMAGE_SYSROOT_DEBIAN_ARCH="arm64" ;;
  *) APPIMAGE_SYSROOT_DEBIAN_ARCH="" ;;
esac

# The build host is never harvested. Its libraries are usually newer than the
# oldest distribution the AppImage targets, so bundling them would make the
# download refuse to start on the very systems the bundle exists to help. When no
# sysroot is supplied a cached one is built, and failing that the AppImage simply
# ships without bundled libraries the way it always did.
resolve_default_sysroot() {
  [ -n "$APPIMAGE_SYSROOT_DEBIAN_ARCH" ] || return 1

  cache_root="${XDG_CACHE_HOME:-$HOME/.cache}/voidstrap/appimage-sysroots"
  cache_path="$cache_root/$APPIMAGE_ARCH"
  if [ -e "$cache_path/usr/lib/$APPIMAGE_ARCH-linux-gnu/libX11.so.6" ]; then
    APPIMAGE_SYSROOT="$cache_path"
    return 0
  fi

  for tool in curl gzip dpkg-deb awk; do
    command -v "$tool" >/dev/null 2>&1 || return 1
  done

  echo "Preparing the $APPIMAGE_ARCH AppImage sysroot, this happens once and is then cached"
  if ! bash "$ROOT/build/Packaging/Linux/appimage-sysroot.sh" "$APPIMAGE_SYSROOT_DEBIAN_ARCH" "$cache_path" >/dev/null 2>&1; then
    rm -rf "$cache_path"
    return 1
  fi

  APPIMAGE_SYSROOT="$cache_path"
  return 0
}

APPIMAGE_BUNDLE_LIBRARIES="
  libX11.so.6 libXext.so.6 libXrender.so.1 libXrandr.so.2 libXi.so.6
  libXcursor.so.1 libXfixes.so.3 libXau.so.6 libXdmcp.so.6 libX11-xcb.so.1
  libxcb.so.1 libxcb-randr.so.0 libxcb-render.so.0 libxcb-shape.so.0
  libxcb-shm.so.0 libxcb-sync.so.1 libxcb-xfixes.so.0 libxcb-present.so.0
  libxcb-dri3.so.0 libxcb-glx.so.0 libxcb-keysyms.so.1 libxcb-util.so.1
  libICE.so.6 libSM.so.6 libuuid.so.1
  libfontconfig.so.1 libfreetype.so.6 libexpat.so.1 libpng16.so.16
  libbz2.so.1.0 libbrotlidec.so.1 libbrotlicommon.so.1 libz.so.1
  libxkbcommon.so.0 libxkbcommon-x11.so.0
  libwayland-client.so.0
  libvulkan.so.1
  libdbus-1.so.3
"

# The C and C++ runtimes always come from the host. The whole soname is matched so
# that neighbours such as libmd or libcrypto are not mistaken for libm or libc.
APPIMAGE_EXCLUDED_RUNTIME='^(ld-linux[^ ]*|libc|libm|libdl|libpthread|librt|libresolv|libutil|libnsl|libgcc_s|libstdc\+\+)\.so'
# Graphics drivers are tied to the host kernel and GPU, so they are matched by family.
APPIMAGE_EXCLUDED_DRIVER='^(libGL|libGLX|libGLdispatch|libEGL|libOpenGL|libglapi|libdrm|libgbm|libnvidia|libcuda|libvulkan_|libxcb-dri2)'

resolve_bundled_library() {
  candidate_name="$1"
  for candidate in \
    "$APPIMAGE_SYSROOT/usr/lib/$APPIMAGE_ARCH-linux-gnu/$candidate_name" \
    "$APPIMAGE_SYSROOT/usr/lib64/$candidate_name" \
    "$APPIMAGE_SYSROOT/usr/lib/$candidate_name" \
    "$APPIMAGE_SYSROOT/lib/$APPIMAGE_ARCH-linux-gnu/$candidate_name" \
    "$APPIMAGE_SYSROOT/lib64/$candidate_name" \
    "$APPIMAGE_SYSROOT/lib/$candidate_name"; do
    if [ -e "$candidate" ]; then
      echo "$candidate"
      return 0
    fi
  done
  return 1
}

copy_bundled_library() {
  library_name="$1"
  destination="$2"
  case "$library_name" in
    "") return 0 ;;
  esac
  if printf '%s' "$library_name" | grep -Eq "$APPIMAGE_EXCLUDED_RUNTIME"; then
    return 0
  fi
  if printf '%s' "$library_name" | grep -Eq "$APPIMAGE_EXCLUDED_DRIVER"; then
    return 0
  fi
  [ -e "$destination/$library_name" ] && return 0
  library_source="$(resolve_bundled_library "$library_name")" || return 0
  library_target="$(readlink -f "$library_source")"
  cp -L "$library_target" "$destination/$library_name"
  chmod 644 "$destination/$library_name"
  readelf -d "$library_target" 2>/dev/null | awk '/NEEDED/{gsub(/[\[\]]/,"",$5); print $5}' | while read -r dependency; do
    copy_bundled_library "$dependency" "$destination"
  done
}

bundle_runtime_libraries() {
  destination="$1"
  if [ "$APPIMAGE_SYSROOT_REQUESTED" -eq 0 ]; then
    if ! resolve_default_sysroot; then
      echo "No $APPIMAGE_ARCH sysroot is available, so the AppImage ships without bundled libraries."
      echo "It still runs wherever the X11, Vulkan and font packages are installed."
      echo "For a download that needs nothing preinstalled, set VOIDSTRAP_APPIMAGE_SYSROOT_DIR"
      echo "to a sysroot for $APPIMAGE_ARCH, or make curl, gzip and dpkg-deb available so one"
      echo "can be prepared automatically."
      return 0
    fi
  fi

  mkdir -p "$destination"
  for library_name in $APPIMAGE_BUNDLE_LIBRARIES; do
    copy_bundled_library "$library_name" "$destination"
  done
  bundled_total="$(find "$destination" -maxdepth 1 -name '*.so*' | wc -l)"
  if [ "$bundled_total" -eq 0 ]; then
    echo "No runtime libraries could be harvested from $APPIMAGE_SYSROOT"
    exit 1
  fi
  echo "Bundled $bundled_total runtime libraries from $APPIMAGE_SYSROOT"
  report_glibc_baseline "$destination"
}

# The oldest distribution the AppImage can run on is decided by the highest glibc
# symbol version the harvested libraries ask for, so make that visible in the log.
report_glibc_baseline() {
  destination="$1"
  command -v objdump >/dev/null 2>&1 || return 0
  baseline="$(find "$destination" -maxdepth 1 -name '*.so*' -exec objdump -T {} + 2>/dev/null \
    | grep -o 'GLIBC_[0-9][0-9.]*' | sort -u -V | tail -n 1)"
  [ -n "$baseline" ] || return 0
  echo "The bundled libraries require at most $baseline, which is the oldest glibc this AppImage supports"
  if [ "$(printf '%s\n%s\n' "$baseline" "$APPIMAGE_GLIBC_CEILING" | sort -V | tail -n 1)" != "$APPIMAGE_GLIBC_CEILING" ]; then
    echo "Warning: the harvested libraries need $baseline, which is newer than $APPIMAGE_GLIBC_CEILING."
    echo "Warning: this AppImage will not start on older distributions, so point"
    echo "Warning: VOIDSTRAP_APPIMAGE_SYSROOT_DIR at a sysroot built from an older distribution."
  fi
}

STAGE=""

cleanup() {
	if [ -n "$STAGE" ] && [ -e "$STAGE" ]; then
		rm -rf "$STAGE"
	fi
	rmdir "$LOCK" 2>/dev/null || true
}

trap cleanup EXIT
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/voidstrap-linux.XXXXXX")"
PUBLISH="$STAGE/publish"
mkdir -p "$PUBLISH"
if [ -n "$PUBLISHED_EXECUTABLE" ]; then
  PUBLISHED_EXECUTABLE="$(readlink -f "$PUBLISHED_EXECUTABLE")"
  [ -f "$PUBLISHED_EXECUTABLE" ] && [ -s "$PUBLISHED_EXECUTABLE" ] || { echo "The published Voidstrap executable is unavailable"; exit 1; }
  cp "$PUBLISHED_EXECUTABLE" "$PUBLISH/Voidstrap"
else
  "$DOTNET_COMMAND" publish "$ROOT/src/Voidstrap.Cross/Voidstrap.Cross.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH" -p:VoidstrapLinuxPackagingRoot="$STAGE/" -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false
fi
test -s "$PUBLISH/Voidstrap"
if [ "$(find "$PUBLISH" -mindepth 1 | wc -l)" -ne 1 ]; then
  echo "The Linux publish is not a single file"
  find "$PUBLISH" -mindepth 1
  exit 1
fi
chmod 755 "$PUBLISH/Voidstrap"
SOURCES="$STAGE/sources"
mkdir -p "$SOURCES"
cp "$DESKTOP_FILE" "$ICON_FILE" "$METAINFO_FILE" "$LICENSE_FILE" "$SOURCES/"
chmod 644 "$SOURCES/"*
DESKTOP_FILE="$SOURCES/$(basename "$DESKTOP_FILE")"
ICON_FILE="$SOURCES/$(basename "$ICON_FILE")"
METAINFO_FILE="$SOURCES/$(basename "$METAINFO_FILE")"
LICENSE_FILE="$SOURCES/$(basename "$LICENSE_FILE")"

case "$FORMAT" in
  deb)
    TARGET="$STAGE/Voidstrap_${VERSION}_${DEB_ARCH}.deb"
    ROOTFS="$STAGE/deb"
    mkdir -p "$ROOTFS/DEBIAN" "$ROOTFS/usr/lib/voidstrap" "$ROOTFS/usr/bin" "$ROOTFS/usr/share/applications" "$ROOTFS/usr/share/icons/hicolor/256x256/apps" "$ROOTFS/usr/share/metainfo" "$ROOTFS/usr/share/doc/voidstrap"
    cp -R "$PUBLISH/." "$ROOTFS/usr/lib/voidstrap/"
    ln -s /usr/lib/voidstrap/Voidstrap "$ROOTFS/usr/bin/voidstrap"
    cp "$DESKTOP_FILE" "$ROOTFS/usr/share/applications/$APPLICATION_ID.desktop"
    cp "$ICON_FILE" "$ROOTFS/usr/share/icons/hicolor/256x256/apps/$APPLICATION_ID.png"
    cp "$METAINFO_FILE" "$ROOTFS/usr/share/metainfo/$APPLICATION_ID.metainfo.xml"
    cp "$LICENSE_FILE" "$ROOTFS/usr/share/doc/voidstrap/copyright"
    printf 'Package: voidstrap\nVersion: %s\nSection: games\nPriority: optional\nArchitecture: %s\nMaintainer: Voidstrap\nDepends: libc6, libgcc-s1 | libgcc1, libstdc++6, libvulkan1, mesa-vulkan-drivers | vulkan-icd, libx11-6, libxext6, libxrender1, libxrandr2, libxi6, libxcursor1, libxfixes3, libxcomposite1, libxdamage1, libice6, libsm6, libfontconfig1, libfreetype6, libxkbcommon0, libxkbcommon-x11-0, libwayland-client0, libdbus-1-3, libgl1 | libgl1-mesa-glx, libegl1 | libegl1-mesa, zlib1g, libssl3 | libssl3t64, ca-certificates, flatpak\nRecommends: xdg-utils, libnotify-bin, libsecret-tools, libgtk-3-0, gstreamer1.0-tools, gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, gstreamer1.0-plugins-bad, gstreamer1.0-libav, libwebkit2gtk-4.1-0 | libwpewebkit-2.0-1\nDescription: Customize and launch Roblox through Sober on Linux\n Voidstrap is an open source Roblox bootstrapper for Linux that installs and launches Roblox through Sober.\n .\n It brings FastFlags, performance and graphics controls, mods, themes, account tools, server tools, Discord Rich Presence, and desktop integrations together in one customizable app.\n' "$VERSION" "$DEB_ARCH" > "$ROOTFS/DEBIAN/control"
    printf '#!/bin/sh\nset -e\ncommand -v update-desktop-database >/dev/null 2>&1 && update-desktop-database /usr/share/applications || true\ncommand -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t /usr/share/icons/hicolor || true\n' > "$ROOTFS/DEBIAN/postinst"
    printf '#!/bin/sh\nset -e\ncommand -v update-desktop-database >/dev/null 2>&1 && update-desktop-database /usr/share/applications || true\ncommand -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t /usr/share/icons/hicolor || true\n' > "$ROOTFS/DEBIAN/postrm"
    chmod 755 "$ROOTFS/DEBIAN/postinst" "$ROOTFS/DEBIAN/postrm"
    chmod -R go-w "$ROOTFS"
    dpkg-deb --root-owner-group --build "$ROOTFS" "$TARGET"
    ;;
  rpm)
    TARGET="$STAGE/Voidstrap_${VERSION}_${RPM_ARCH}.rpm"
    RPMROOT="$STAGE/rpmbuild"
    mkdir -p "$RPMROOT/BUILD" "$RPMROOT/BUILDROOT" "$RPMROOT/RPMS" "$RPMROOT/SOURCES" "$RPMROOT/SPECS" "$RPMROOT/SRPMS"
    cp -R "$PUBLISH/." "$RPMROOT/SOURCES/publish"
    cp "$DESKTOP_FILE" "$RPMROOT/SOURCES/$APPLICATION_ID.desktop"
    cp "$ICON_FILE" "$RPMROOT/SOURCES/$APPLICATION_ID.png"
    cp "$METAINFO_FILE" "$RPMROOT/SOURCES/$APPLICATION_ID.metainfo.xml"
    cp "$LICENSE_FILE" "$RPMROOT/SOURCES/LICENSE"
    RPM_DATE="$(LC_ALL=C date -u -d "@${SOURCE_DATE_EPOCH:-$(date +%s)}" '+%a %b %d %Y')"
    sed -e "s/@VERSION@/$VERSION/g" -e "s/@ARCH@/$RPM_ARCH/g" -e "s/@DATE@/$RPM_DATE/g" "$ROOT/build/Packaging/Linux/voidstrap.spec.in" > "$RPMROOT/SPECS/voidstrap.spec"
    rpmbuild --define "_topdir $RPMROOT" --target "$RPM_ARCH" -bb "$RPMROOT/SPECS/voidstrap.spec"
    cp "$RPMROOT/RPMS/$RPM_ARCH/voidstrap-$VERSION-1.$RPM_ARCH.rpm" "$TARGET"
    ;;
  appimage)
    TARGET="$STAGE/Voidstrap_${VERSION}_${APPIMAGE_ARCH}.AppImage"
    APPDIR="$STAGE/AppDir"
    mkdir -p "$APPDIR/usr/lib/voidstrap" "$APPDIR/usr/bin"
    cp -R "$PUBLISH/." "$APPDIR/usr/lib/voidstrap/"
    ln -s ../lib/voidstrap/Voidstrap "$APPDIR/usr/bin/voidstrap"
    bundle_runtime_libraries "$APPDIR/usr/lib/bundled"
    cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
APPDIR="$(dirname "$(readlink -f "$0")")"
BUNDLED="$APPDIR/usr/lib/bundled"

# The bundled libraries only fill in what a machine never installed. A library the
# host already provides has to keep winning, because host components such as cairo
# are built against the host versions and abort when an older bundled copy shadows
# them. LD_LIBRARY_PATH is searched ahead of the system directories no matter where
# the entry sits, so the fallback directory is populated with the missing libraries
# alone rather than pointing at the whole bundle.
if [ -d "$BUNDLED" ]; then
    FALLBACK="${XDG_RUNTIME_DIR:-${TMPDIR:-/tmp}}/voidstrap-appimage-libraries"
    rm -rf "$FALLBACK"
    if mkdir -p "$FALLBACK" 2>/dev/null; then
        CACHE="$(ldconfig -p 2>/dev/null || /sbin/ldconfig -p 2>/dev/null || echo)"
        for library in "$BUNDLED"/*.so*; do
            [ -e "$library" ] || continue
            name="${library##*/}"
            if [ -n "$CACHE" ]; then
                case "$CACHE" in
                    *"	$name ("*) continue ;;
                esac
            else
                if [ -e "/usr/lib/$name" ] || [ -e "/usr/lib64/$name" ] \
                    || [ -e "/usr/lib/x86_64-linux-gnu/$name" ] \
                    || [ -e "/usr/lib/aarch64-linux-gnu/$name" ]; then
                    continue
                fi
            fi
            ln -s "$library" "$FALLBACK/$name" 2>/dev/null
        done
        if [ -n "$(ls -A "$FALLBACK" 2>/dev/null)" ]; then
            LD_LIBRARY_PATH="$FALLBACK${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
            export LD_LIBRARY_PATH
        else
            rm -rf "$FALLBACK"
        fi
    fi
fi

export APPDIR
exec "$APPDIR/usr/lib/voidstrap/Voidstrap" "$@"
APPRUN
    chmod 755 "$APPDIR/AppRun"
    cp "$DESKTOP_FILE" "$APPDIR/$APPLICATION_ID.desktop"
    cp "$ICON_FILE" "$APPDIR/$APPLICATION_ID.png"
    ln -s "$APPLICATION_ID.png" "$APPDIR/.DirIcon"
    mkdir -p "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/metainfo" "$APPDIR/usr/share/licenses/voidstrap"
    cp "$DESKTOP_FILE" "$APPDIR/usr/share/applications/$APPLICATION_ID.desktop"
    cp "$ICON_FILE" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$APPLICATION_ID.png"
    cp "$METAINFO_FILE" "$APPDIR/usr/share/metainfo/$APPLICATION_ID.appdata.xml"
    cp "$LICENSE_FILE" "$APPDIR/usr/share/licenses/voidstrap/LICENSE"
    sed -i -E "s#<release version=\"[^\"]+\" date=\"[^\"]+\" />#<release version=\"$VERSION\" date=\"$(date -u +%Y-%m-%d)\" />#" "$APPDIR/usr/share/metainfo/$APPLICATION_ID.appdata.xml"
    chmod -R go-w "$APPDIR"
    UPDATE_INFORMATION="gh-releases-zsync|KloBraticc|Voidstrap|latest|Voidstrap_*_${APPIMAGE_ARCH}.AppImage.zsync"
    (
      cd "$STAGE"
      ARCH="$APPIMAGE_ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGE_TOOL" -u "$UPDATE_INFORMATION" "$APPDIR" "$TARGET"
    )
    ;;
  tar)
    TARGET="$STAGE/Voidstrap_${VERSION}_${RID}.tar.gz"
    UNCOMPRESSED="$STAGE/Voidstrap_${VERSION}_${RID}.tar"
    BUNDLE="$STAGE/Voidstrap"
    mkdir -p "$BUNDLE/share/applications" "$BUNDLE/share/icons/hicolor/256x256/apps" "$BUNDLE/share/metainfo" "$BUNDLE/share/licenses/voidstrap"
    cp "$PUBLISH/Voidstrap" "$BUNDLE/Voidstrap"
    chmod 755 "$BUNDLE/Voidstrap"
    cp "$DESKTOP_FILE" "$BUNDLE/share/applications/$APPLICATION_ID.desktop"
    cp "$ICON_FILE" "$BUNDLE/share/icons/hicolor/256x256/apps/$APPLICATION_ID.png"
    cp "$METAINFO_FILE" "$BUNDLE/share/metainfo/$APPLICATION_ID.metainfo.xml"
    cp "$LICENSE_FILE" "$BUNDLE/share/licenses/voidstrap/LICENSE"
    chmod -R go-w "$BUNDLE"
    tar --sort=name --mtime="@${SOURCE_DATE_EPOCH:-0}" --owner=0 --group=0 --numeric-owner --exclude="Voidstrap/Voidstrap" -C "$STAGE" -cf "$UNCOMPRESSED" Voidstrap
    tar --mtime="@${SOURCE_DATE_EPOCH:-0}" --owner=0 --group=0 --numeric-owner --mode=0755 -C "$STAGE" -rf "$UNCOMPRESSED" Voidstrap/Voidstrap
    gzip -n -f "$UNCOMPRESSED"
    ;;
esac

mv -n "$TARGET" "$FINAL_TARGET"
if [ -e "$TARGET" ]; then
  echo "The requested output already exists"
  exit 1
fi
if [ "$FORMAT" = "appimage" ] && [ -f "$STAGE/$(basename "$TARGET").zsync" ]; then
  mv -n "$STAGE/$(basename "$TARGET").zsync" "$ZSYNC_FINAL_TARGET"
fi
