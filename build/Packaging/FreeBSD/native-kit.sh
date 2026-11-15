#!/usr/bin/env bash
set -euo pipefail
umask 022

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
OUTPUT="${1:?Usage: bash native-kit.sh <output directory>}"
RID=freebsd-x64
NATIVE="$ROOT/external/LibreWPF/$RID"
PACKS="${DOTNET_ROOT:?DOTNET_ROOT must point at the FreeBSD .NET SDK}/packs"

[ "$(uname -s)" = "FreeBSD" ] || { echo "The FreeBSD native kit is only built on FreeBSD"; exit 1; }
mkdir -p "$OUTPUT/native" "$OUTPUT/feed"
OUTPUT="$(cd "$OUTPUT" && pwd)"

if [ ! -s "$NATIVE/libwgpu_native.so" ]; then
  bash "$ROOT/build/Packaging/Linux/build-wgpu-native.sh" "$NATIVE" "$RID"
fi
if [ ! -s "$NATIVE/libglfw.so.3" ]; then
  bash "$ROOT/build/Packaging/Linux/build-glfw-native.sh" "$NATIVE" "$RID"
fi
cp "$NATIVE/libwgpu_native.so" "$NATIVE/libglfw.so.3" "$OUTPUT/native/"
[ ! -f "$NATIVE/GLFW-LICENSE.txt" ] || cp "$NATIVE/GLFW-LICENSE.txt" "$OUTPUT/native/"

for library in voidimage voidclip voidrpc voidlog voidstate; do
  bash "$ROOT/build/Packaging/build-$library.sh" "$RID" "$OUTPUT/native/lib$library.so"
  test -s "$OUTPUT/native/lib$library.so" || { echo "lib$library.so was not built"; exit 1; }
done
bash "$ROOT/build/Packaging/Linux/build-native-compat.sh" "$RID" "$OUTPUT/native/libvoidstrapeditorcompat.so"

SDK_VERSION="$(sed -n 's/.*"LibreWPF.Sdk":[[:space:]]*"\([^"]*\)".*/\1/p' "$ROOT/global.json")"
[ -n "$SDK_VERSION" ] || { echo "The LibreWPF.Sdk version is missing from global.json"; exit 1; }
WORK="$(mktemp -d "${TMPDIR:-/tmp}/voidstrap-kit.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
fetch -q -o "$WORK/sdk.nupkg" "https://api.nuget.org/v3-flatcontainer/librewpf.sdk/$SDK_VERSION/librewpf.sdk.$SDK_VERSION.nupkg"
tar -xf "$WORK/sdk.nupkg" -C "$WORK" targets/ProGPU.Wpf.Sdk.Win32Compat.c
bash "$ROOT/build/Packaging/Linux/build-native-compat.sh" "$RID" "$OUTPUT/native/libwin32compat.so" "$WORK/targets/ProGPU.Wpf.Sdk.Win32Compat.c"

RUNTIME_VERSION=""
for id in Microsoft.NETCore.App.Runtime.$RID Microsoft.NETCore.App.Host.$RID Microsoft.AspNetCore.App.Runtime.$RID; do
  version="$(ls "$PACKS/$id" | sort -V | tail -n 1)"
  [ -n "$version" ] || { echo "$id is missing from the .NET SDK"; exit 1; }
  [ "$id" != "Microsoft.NETCore.App.Runtime.$RID" ] || RUNTIME_VERSION="$version"
  rm -rf "$WORK/pack"
  mkdir -p "$WORK/pack"
  cp -RL "$PACKS/$id/$version/." "$WORK/pack/"
  printf '<?xml version="1.0" encoding="utf-8"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>%s</id><version>%s</version><authors>dotnet</authors><description>%s</description></metadata></package>' "$id" "$version" "$id" > "$WORK/pack/$id.nuspec"
  (cd "$WORK/pack" && find . -type f | sed 's,^\./,,' | sort > "$WORK/files" && tar --format zip -cf "$OUTPUT/feed/$(echo "$id" | tr '[:upper:]' '[:lower:]').$version.nupkg" -T "$WORK/files")
done
printf '%s\n' "$RUNTIME_VERSION" > "$OUTPUT/runtime-version.txt"
echo "Built the FreeBSD native kit in $OUTPUT"
