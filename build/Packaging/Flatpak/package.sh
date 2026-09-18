set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RID="${1:?A Linux runtime identifier is required}"
VERSION="${2:-}"
OUTPUT="${3:?An output directory is required}"
PUBLISHED_EXECUTABLE="${4:-}"
APPLICATION_ID="io.github.KloBraticc.Voidstrap"

if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:^[[:space:]]*<VoidstrapVersion>\(.*\)</VoidstrapVersion>[[:space:]]*$:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
fi

case "$RID" in
  linux-x64) ARCH="x86_64" ;;
  linux-arm64) ARCH="aarch64" ;;
  *) echo "Flatpak packages require a glibc Linux runtime identifier"; exit 1 ;;
esac

if [[ ! "$VERSION" =~ ^[0-9]+([.][0-9]+){1,3}$ ]]; then
  echo "The package version is invalid"
  exit 1
fi

command -v flatpak >/dev/null 2>&1 || { echo "Flatpak is unavailable"; exit 1; }
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
FINAL_TARGET="$OUTPUT/Voidstrap_${VERSION}_${ARCH}.flatpak"
if [ -e "$FINAL_TARGET" ]; then
  echo "The requested output already exists"
  exit 1
fi

LOCK="$OUTPUT/.voidstrap-flatpak-$ARCH.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "Another Flatpak package operation is already running"
  exit 1
fi

STAGE=""
cleanup() {
	if [ -n "$STAGE" ] && [ -e "$STAGE" ]; then
		rm -rf "$STAGE"
	fi
	rmdir "$LOCK" 2>/dev/null || true
}
trap cleanup EXIT

STAGE="$(mktemp -d "$OUTPUT/.voidstrap-flatpak.XXXXXX")"
ARCHIVE_DIRECTORY="$STAGE/archive"
mkdir -p "$ARCHIVE_DIRECTORY"
bash "$ROOT/build/Packaging/Linux/package.sh" "$RID" "$VERSION" tar "$ARCHIVE_DIRECTORY" "$PUBLISHED_EXECUTABLE"
tar -xzf "$ARCHIVE_DIRECTORY/Voidstrap_${VERSION}_${RID}.tar.gz" -C "$STAGE"
mv "$STAGE/Voidstrap" "$STAGE/payload"
sed -i -E "s#<release version=\"[^\"]+\" date=\"[^\"]+\" />#<release version=\"$VERSION\" date=\"$(date -u +%Y-%m-%d)\" />#" "$STAGE/payload/share/metainfo/$APPLICATION_ID.metainfo.xml"

MANIFEST="$STAGE/$APPLICATION_ID.yml"
printf 'id: %s\nruntime: org.gnome.Platform\nruntime-version: "50"\nsdk: org.gnome.Sdk\ncommand: voidstrap\nseparate-locales: false\nfinish-args:\n  - --share=network\n  - --share=ipc\n  - --socket=fallback-x11\n  - --socket=wayland\n  - --socket=pulseaudio\n  - --device=dri\n  - --filesystem=xdg-download\n  - --filesystem=~/.var/app/org.vinegarhq.Sober:create\n  - --filesystem=~/.var/app/org.vinegarhq.Vinegar:create\n  - --filesystem=xdg-run/app/com.discordapp.Discord:create\n  - --filesystem=xdg-run/discord-ipc-0\n  - --filesystem=xdg-run/discord-ipc-1\n  - --filesystem=xdg-run/discord-ipc-2\n  - --filesystem=xdg-run/discord-ipc-3\n  - --filesystem=xdg-run/discord-ipc-4\n  - --filesystem=xdg-run/discord-ipc-5\n  - --filesystem=xdg-run/discord-ipc-6\n  - --filesystem=xdg-run/discord-ipc-7\n  - --filesystem=xdg-run/discord-ipc-8\n  - --filesystem=xdg-run/discord-ipc-9\n  - --talk-name=org.freedesktop.Flatpak\n  - --talk-name=org.freedesktop.secrets\nmodules:\n  - name: voidstrap\n    buildsystem: simple\n    build-commands:\n      - install -Dm755 Voidstrap /app/bin/voidstrap\n      - cp -a share/. /app/share/\n    sources:\n      - type: dir\n        path: payload\n' "$APPLICATION_ID" > "$MANIFEST"

BUILD_DIRECTORY="$STAGE/build"
REPOSITORY="$STAGE/repository"
if command -v flatpak-builder >/dev/null 2>&1; then
  BUILDER=(flatpak-builder)
elif flatpak info org.flatpak.Builder >/dev/null 2>&1; then
  BUILDER=(flatpak run --filesystem="$STAGE" org.flatpak.Builder)
else
  echo "Flatpak Builder is unavailable"
  exit 1
fi

"${BUILDER[@]}" --arch="$ARCH" --force-clean --disable-rofiles-fuse --user --install-deps-from=flathub --default-branch=stable --state-dir="$STAGE/state" --repo="$REPOSITORY" "$BUILD_DIRECTORY" "$MANIFEST"
TARGET="$STAGE/Voidstrap_${VERSION}_${ARCH}.flatpak"
flatpak build-bundle --arch="$ARCH" "$REPOSITORY" "$TARGET" "$APPLICATION_ID" stable
mv -n "$TARGET" "$FINAL_TARGET"
