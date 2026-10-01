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
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
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

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/voidstrap-flatpak.XXXXXX")"
ARCHIVE_DIRECTORY="$STAGE/archive"
mkdir -p "$ARCHIVE_DIRECTORY"
bash "$ROOT/build/Packaging/Linux/package.sh" "$RID" "$VERSION" tar "$ARCHIVE_DIRECTORY" "$PUBLISHED_EXECUTABLE"
tar -xzf "$ARCHIVE_DIRECTORY/Voidstrap_${VERSION}_${RID}.tar.gz" -C "$STAGE"
mv "$STAGE/Voidstrap" "$STAGE/payload"
sed -i -E "s#<release version=\"[^\"]+\" date=\"[^\"]+\" />#<release version=\"$VERSION\" date=\"$(date -u +%Y-%m-%d)\" />#" "$STAGE/payload/share/metainfo/$APPLICATION_ID.metainfo.xml"

MANIFEST="$STAGE/$APPLICATION_ID.yml"
FINISH_ARGS=(
  "--share=network"
  "--share=ipc"
  "--socket=x11"
  "--socket=wayland"
  "--socket=pulseaudio"
  "--device=all"
  "--persist=.local/share/fonts"
  "--filesystem=xdg-download"
  "--filesystem=xdg-desktop"
  "--filesystem=xdg-data/applications:create"
  "--filesystem=xdg-config/user-dirs.dirs:ro"
  "--filesystem=~/.var/app/org.vinegarhq.Sober:create"
  "--filesystem=~/.var/app/org.vinegarhq.Vinegar:create"
  "--filesystem=xdg-config/vinegar:create"
  "--filesystem=xdg-data/vinegar:create"
  "--filesystem=~/.steam"
  "--filesystem=~/.local/share/Steam"
  "--filesystem=~/.var/app/com.valvesoftware.Steam"
  "--filesystem=~/.local/share/icons/hicolor/256x256/apps:create"
  "--filesystem=xdg-run/app/com.discordapp.Discord:create"
  "--filesystem=xdg-run/app/com.discordapp.DiscordCanary:create"
  "--filesystem=xdg-run/app/dev.vencord.Vesktop:create"
  "--filesystem=xdg-run/discord-ipc-0"
  "--filesystem=xdg-run/discord-ipc-1"
  "--filesystem=xdg-run/discord-ipc-2"
  "--filesystem=xdg-run/discord-ipc-3"
  "--filesystem=xdg-run/discord-ipc-4"
  "--filesystem=xdg-run/discord-ipc-5"
  "--filesystem=xdg-run/discord-ipc-6"
  "--filesystem=xdg-run/discord-ipc-7"
  "--filesystem=xdg-run/discord-ipc-8"
  "--filesystem=xdg-run/discord-ipc-9"
  "--filesystem=xdg-run/at-spi:ro"
  "--talk-name=org.freedesktop.Flatpak"
  "--talk-name=org.freedesktop.secrets"
  "--talk-name=org.freedesktop.Notifications"
  "--talk-name=org.kde.StatusNotifierWatcher"
)

printf 'id: %s\nruntime: org.gnome.Platform\nruntime-version: "50"\nsdk: org.gnome.Sdk\ncommand: voidstrap\nseparate-locales: false\nfinish-args:\n' "$APPLICATION_ID" > "$MANIFEST"
printf '  - %s\n' "${FINISH_ARGS[@]}" >> "$MANIFEST"
printf 'modules:\n  - name: voidstrap\n    buildsystem: simple\n    build-commands:\n      - install -Dm755 Voidstrap /app/bin/voidstrap\n      - cp -a share/. /app/share/\n    sources:\n      - type: dir\n        path: payload\n' >> "$MANIFEST"

BUILD_DIRECTORY="$STAGE/build"
REPOSITORY="$STAGE/repository"
if [ "$ARCH" != "$(uname -m)" ]; then
  mkdir -p "$BUILD_DIRECTORY/files/bin" "$BUILD_DIRECTORY/files/share"
  install -Dm755 "$STAGE/payload/Voidstrap" "$BUILD_DIRECTORY/files/bin/voidstrap"
  cp -a "$STAGE/payload/share/." "$BUILD_DIRECTORY/files/share/"
  command -v appstreamcli >/dev/null 2>&1 || { echo "AppStream is unavailable"; exit 1; }
  appstreamcli compose --no-net --prefix=/ --origin="$APPLICATION_ID" --components="$APPLICATION_ID,$APPLICATION_ID.desktop" --result-root="$BUILD_DIRECTORY/files" --data-dir="$BUILD_DIRECTORY/files/share/app-info/xmls" --icons-dir="$BUILD_DIRECTORY/files/share/app-info/icons/flatpak" "$BUILD_DIRECTORY/files"
  printf '[Application]\nname=%s\nruntime=org.gnome.Platform/%s/50\nsdk=org.gnome.Sdk/%s/50\n' "$APPLICATION_ID" "$ARCH" "$ARCH" > "$BUILD_DIRECTORY/metadata"
  flatpak build-finish --command=voidstrap "${FINISH_ARGS[@]}" "$BUILD_DIRECTORY"
  flatpak build-export --arch="$ARCH" --disable-sandbox "$REPOSITORY" "$BUILD_DIRECTORY" stable
else
  if command -v flatpak-builder >/dev/null 2>&1; then
    BUILDER=(flatpak-builder)
  elif flatpak info org.flatpak.Builder >/dev/null 2>&1; then
    BUILDER=(flatpak run --filesystem="$STAGE" org.flatpak.Builder)
  else
    echo "Flatpak Builder is unavailable"
    exit 1
  fi

  "${BUILDER[@]}" --arch="$ARCH" --force-clean --disable-rofiles-fuse --user --install-deps-from=flathub --default-branch=stable --state-dir="$STAGE/state" --repo="$REPOSITORY" "$BUILD_DIRECTORY" "$MANIFEST"
fi
python3 - "$BUILD_DIRECTORY/files/share/metainfo/$APPLICATION_ID.metainfo.xml" "$BUILD_DIRECTORY/files/share/app-info/xmls" "$APPLICATION_ID" <<'PYTHON'
from pathlib import Path
import copy
import gzip
import sys
import xml.etree.ElementTree as ET

screenshots = ET.parse(sys.argv[1]).getroot().find("screenshots")
if screenshots is not None:
    updated = 0
    for catalog in Path(sys.argv[2]).glob("*.xml*"):
        compressed = catalog.name.endswith(".gz")
        data = catalog.read_bytes()
        root = ET.fromstring(gzip.decompress(data) if compressed else data)
        changed = False
        for component in root.findall("component"):
            if component.findtext("id") != sys.argv[3]:
                continue
            previous = component.find("screenshots")
            if previous is not None:
                component.remove(previous)
            component.append(copy.deepcopy(screenshots))
            changed = True
        if changed:
            data = ET.tostring(root, encoding="utf-8", xml_declaration=True)
            catalog.write_bytes(gzip.compress(data, mtime=0) if compressed else data)
            updated += 1
    if updated == 0:
        raise SystemExit("The Flatpak screenshot catalog could not be updated")
PYTHON
flatpak build-export --arch="$ARCH" --disable-sandbox "$REPOSITORY" "$BUILD_DIRECTORY" stable
TARGET="$STAGE/Voidstrap_${VERSION}_${ARCH}.flatpak"
flatpak build-bundle --arch="$ARCH" --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo "$REPOSITORY" "$TARGET" "$APPLICATION_ID" stable
mv -n "$TARGET" "$FINAL_TARGET"
