#!/usr/bin/env bash
set -euo pipefail
umask 022

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
VERSION="${1:-}"
OUTPUT="${2:?Usage: bash package.sh <version or empty> <output directory>}"
APPLICATION_ID="io.github.KloBraticc.Voidstrap"
NATIVE="$ROOT/external/LibreWPF/freebsd-x64"
RUNTIME_DEPENDENCIES="libX11 libXext libXrandr libXinerama libXcursor libXi libXcomposite libXdamage libxkbcommon fontconfig freetype2 vulkan-loader mesa-libs sqlite3 libarchive onnxruntime ffmpeg krb5"
DEPENDENCIES="bash dotnet rust cmake git llvm pkgconf xorgproto libX11 libXrandr libXinerama libXcursor libXi libxkbcommon vulkan-loader mesa-libs krb5"

[ "$(uname -s)" = "FreeBSD" ] || { echo "FreeBSD packages are only built on FreeBSD"; exit 1; }
[ "$(uname -m)" = "amd64" ] || { echo "Only amd64 FreeBSD is supported"; exit 1; }
for tool in dotnet cargo cmake git tar gzip; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is unavailable, install: pkg install $DEPENDENCIES"; exit 1; }
done

RID="${VOIDSTRAP_FREEBSD_RID:-$(dotnet --info | sed -n 's/^[[:space:]]*RID:[[:space:]]*//p' | head -n 1)}"
case "$RID" in
  freebsd-x64|freebsd.*-x64) ;;
  *) echo "The .NET SDK runtime identifier $RID is not a FreeBSD amd64 identifier"; exit 1 ;;
esac

if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:^[[:space:]]*<VoidstrapVersion>\(.*\)</VoidstrapVersion>[[:space:]]*$:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
fi
if [[ ! "$VERSION" =~ ^[0-9]+([.][0-9]+){1,3}$ ]]; then
  echo "The package version is invalid"
  exit 1
fi

mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
FINAL_TARGET="$OUTPUT/Voidstrap_${VERSION}_freebsd-x64.tar.gz"
if [ -e "$FINAL_TARGET" ]; then
  echo "The requested output already exists"
  exit 1
fi

if [ ! -s "$NATIVE/libwgpu_native.so" ]; then
  bash "$ROOT/build/Packaging/Linux/build-wgpu-native.sh" "$NATIVE" freebsd-x64
fi
if [ ! -s "$NATIVE/libglfw.so.3" ]; then
  bash "$ROOT/build/Packaging/Linux/build-glfw-native.sh" "$NATIVE" freebsd-x64
fi

STAGE=""
cleanup() {
  if [ -n "$STAGE" ] && [ -e "$STAGE" ]; then
    rm -rf "$STAGE"
  fi
}
trap cleanup EXIT
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/voidstrap-freebsd.XXXXXX")"
PUBLISH="$STAGE/publish"
mkdir -p "$PUBLISH"

dotnet publish "$ROOT/src/Voidstrap.Cross/Voidstrap.Cross.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH" -p:VoidstrapLinuxPackagingRoot="$STAGE/" -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false
test -s "$PUBLISH/Voidstrap"
if [ "$(find "$PUBLISH" -mindepth 1 ! -path "$PUBLISH/licenses" ! -path "$PUBLISH/licenses/*" | wc -l | tr -d ' ')" -ne 1 ]; then
  echo "The FreeBSD publish is not a single file"
  find "$PUBLISH" -mindepth 1
  exit 1
fi

BUNDLE="$STAGE/Voidstrap"
mkdir -p "$BUNDLE/share/applications" "$BUNDLE/share/icons/hicolor/256x256/apps" "$BUNDLE/share/metainfo" "$BUNDLE/share/licenses/voidstrap"
install -m 755 "$PUBLISH/Voidstrap" "$BUNDLE/Voidstrap"
printf 'Install the FreeBSD runtime dependencies:\n\n  pkg install %s\n\nRun ./Voidstrap from this directory.\n' "$RUNTIME_DEPENDENCIES" > "$BUNDLE/README.txt"
install -m 644 "$ROOT/build/Packaging/Linux/voidstrap.desktop" "$BUNDLE/share/applications/$APPLICATION_ID.desktop"
install -m 644 "$ROOT/src/Voidstrap.App/Voidstrap.png" "$BUNDLE/share/icons/hicolor/256x256/apps/$APPLICATION_ID.png"
install -m 644 "$ROOT/build/Packaging/Linux/$APPLICATION_ID.metainfo.xml" "$BUNDLE/share/metainfo/$APPLICATION_ID.metainfo.xml"
install -m 644 "$ROOT/LICENSE.VOIDSTRAP" "$BUNDLE/share/licenses/voidstrap/LICENSE"
install -m 644 "$ROOT/THIRD-PARTY-NOTICES.txt" "$ROOT/legal/sources/Bibata-Cursor-v2.0.7.tar.gz" "$BUNDLE/share/licenses/voidstrap/"
if [ -f "$NATIVE/GLFW-LICENSE.txt" ]; then
  install -m 644 "$NATIVE/GLFW-LICENSE.txt" "$BUNDLE/share/licenses/voidstrap/GLFW-LICENSE.txt"
fi
if [ -d "$PUBLISH/licenses" ]; then
  find "$PUBLISH/licenses" -type f -exec install -m 644 {} "$BUNDLE/share/licenses/voidstrap/" \;
fi

TARGET="$STAGE/Voidstrap_${VERSION}_freebsd-x64.tar.gz"
tar --uid 0 --gid 0 --numeric-owner -C "$STAGE" -czf "$TARGET" Voidstrap
mv -n "$TARGET" "$FINAL_TARGET"
if [ -e "$TARGET" ]; then
  echo "The requested output already exists"
  exit 1
fi
echo "Built $FINAL_TARGET"
echo "Runtime packages: pkg install $RUNTIME_DEPENDENCIES"
