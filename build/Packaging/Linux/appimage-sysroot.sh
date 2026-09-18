#!/usr/bin/env bash
set -euo pipefail

# Builds the sysroot the AppImage harvests its bundled libraries from. Those
# libraries decide how old a distribution the AppImage still starts on, so they come
# from a fixed old release instead of from whatever the build agent is running.
# Only curl, gzip and dpkg-deb are needed, which keeps this usable on a developer
# machine and on a build agent without a container runtime or root.

ARCHITECTURE="${1:?A Debian architecture is required, for example amd64 or arm64}"
DESTINATION="${2:?A destination directory is required}"
SUITE="${VOIDSTRAP_APPIMAGE_SYSROOT_SUITE:-bullseye}"
MIRROR="${VOIDSTRAP_APPIMAGE_SYSROOT_MIRROR:-https://deb.debian.org/debian}"

case "$ARCHITECTURE" in
  amd64) TRIPLET="x86_64-linux-gnu" ;;
  arm64) TRIPLET="aarch64-linux-gnu" ;;
  *) echo "Unsupported sysroot architecture"; exit 1 ;;
esac

for tool in curl gzip dpkg-deb awk; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is required to build the AppImage sysroot"; exit 1; }
done

PACKAGES="
  libx11-6 libx11-xcb1 libxext6 libxrender1 libxrandr2 libxi6 libxcursor1 libxfixes3
  libxau6 libxdmcp6 libxcb1 libxcb-randr0 libxcb-render0 libxcb-shape0 libxcb-shm0
  libxcb-sync1 libxcb-xfixes0 libxcb-present0 libxcb-dri3-0 libxcb-glx0
  libxcb-keysyms1 libxcb-util1
  libice6 libsm6 libuuid1 libbsd0 libmd0
  libfontconfig1 libfreetype6 libexpat1 libpng16-16 libbz2-1.0 libbrotli1 zlib1g
  libxkbcommon0 libxkbcommon-x11-0
  libwayland-client0
  libvulkan1
  libdbus-1-3
"

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/voidstrap-sysroot.XXXXXX")"
cleanup() { [ -n "${STAGE:-}" ] && rm -rf "$STAGE"; }
trap cleanup EXIT

printf '%s\n' $PACKAGES > "$STAGE/wanted"

INDEX="$STAGE/Packages"
curl -sSL --fail --retry 3 "$MIRROR/dists/$SUITE/main/binary-$ARCHITECTURE/Packages.gz" \
  | gzip -dc > "$INDEX"

awk -v list="$STAGE/wanted" '
  BEGIN { while ((getline line < list) > 0) if (line != "") want[line] = 1 }
  /^Package: / { package = $2; next }
  /^Filename: / { if (package in want && !(package in seen)) { seen[package] = 1; print $2 } }
' "$INDEX" > "$STAGE/filenames"

FOUND="$(wc -l < "$STAGE/filenames")"
if [ "$FOUND" -eq 0 ]; then
  echo "No packages were found in the $SUITE index for $ARCHITECTURE"
  exit 1
fi

mkdir -p "$DESTINATION" "$STAGE/archives"
while read -r filename; do
  [ -n "$filename" ] || continue
  archive="$STAGE/archives/$(basename "$filename")"
  curl -sSL --fail --retry 3 -o "$archive" "$MIRROR/$filename"
  dpkg-deb -x "$archive" "$DESTINATION"
  rm -f "$archive"
done < "$STAGE/filenames"

for required in libX11.so.6 libvulkan.so.1 libfontconfig.so.1 libfreetype.so.6; do
  if [ ! -e "$DESTINATION/usr/lib/$TRIPLET/$required" ]; then
    echo "The $ARCHITECTURE sysroot is missing $required"
    exit 1
  fi
done

echo "Prepared the $ARCHITECTURE AppImage sysroot from Debian $SUITE with $FOUND packages in $DESTINATION"
