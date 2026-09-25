set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RID="${1:?A macOS runtime identifier is required}"
VERSION="${2:?A version is required}"
OUTPUT="${3:?An output directory is required}"
PUBLISHED_EXECUTABLE="${4:-}"

case "$RID" in
  osx-x64|osx-arm64) ;;
  *) echo "Unsupported macOS runtime identifier"; exit 1 ;;
esac

if [[ ! "$VERSION" =~ ^[0-9]+([.][0-9]+){1,3}$ ]]; then
  echo "The package version is invalid"
  exit 1
fi

mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
DMG_TARGET="$OUTPUT/Voidstrap-$RID.dmg"
ARCHIVE_TARGET="$OUTPUT/Voidstrap-$RID.zip"
TARBALL_TARGET="$OUTPUT/Voidstrap-$RID.tar.gz"

if [ -e "$DMG_TARGET" ] || [ -e "$ARCHIVE_TARGET" ] || [ -e "$TARBALL_TARGET" ]; then
  echo "The requested output already exists"
  exit 1
fi

LOCK="$OUTPUT/.voidstrap-macos-$RID.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "Another package operation is already running"
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
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/voidstrap-macos.XXXXXX")"
PUBLISH="$STAGE/publish"
APPLICATION="$STAGE/Voidstrap.app"
DMG="$STAGE/Voidstrap-$RID.dmg"
ARCHIVE="$STAGE/Voidstrap-$RID.zip"

commit_artifact() {
  mv -n "$1" "$2"
  if [ -e "$1" ]; then
    echo "The requested output already exists"
    exit 1
  fi
}

mkdir -p "$PUBLISH"
if [ -n "$PUBLISHED_EXECUTABLE" ]; then
  [ -f "$PUBLISHED_EXECUTABLE" ] && [ -s "$PUBLISHED_EXECUTABLE" ] || { echo "The published Voidstrap executable is unavailable"; exit 1; }
  cp "$PUBLISHED_EXECUTABLE" "$PUBLISH/Voidstrap"
else
  dotnet publish "$ROOT/src/Voidstrap.Cross/Voidstrap.Cross.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH" -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false
fi
mkdir -p "$APPLICATION/Contents/MacOS" "$APPLICATION/Contents/Resources"
cp -R "$PUBLISH/." "$APPLICATION/Contents/MacOS/"
NOTICES="$APPLICATION/Contents/MacOS/LibreWPF/Notices"
if [ -d "$NOTICES" ]; then
  while IFS= read -r directory; do
    base="$(basename "$directory")"
    case "$base" in
      *.*) mv "$directory" "$(dirname "$directory")/${base//./-}" ;;
    esac
  done < <(find "$NOTICES" -depth -type d)
fi
cp "$ROOT/build/Packaging/MacOS/Info.plist" "$APPLICATION/Contents/Info.plist"

if [ "$(uname -s)" != "Darwin" ]; then
  sed -i -e "/<key>CFBundleShortVersionString<\/key>/{n;s|<string>[^<]*</string>|<string>$VERSION</string>|}" -e "/<key>CFBundleVersion<\/key>/{n;s|<string>[^<]*</string>|<string>$VERSION</string>|}" "$APPLICATION/Contents/Info.plist"
  chmod 644 "$APPLICATION/Contents/Info.plist"
  chmod -R go-w "$APPLICATION"
  TARBALL="$STAGE/Voidstrap-$RID.tar"
  tar --sort=name --mtime="@${SOURCE_DATE_EPOCH:-0}" --owner=0 --group=0 --numeric-owner --exclude="Voidstrap.app/Contents/MacOS/Voidstrap" -C "$STAGE" -cf "$TARBALL" Voidstrap.app
  tar --mtime="@${SOURCE_DATE_EPOCH:-0}" --owner=0 --group=0 --numeric-owner --mode=0755 -C "$STAGE" -rf "$TARBALL" Voidstrap.app/Contents/MacOS/Voidstrap
  gzip -n -f "$TARBALL"
  commit_artifact "$TARBALL.gz" "$TARBALL_TARGET"
  exit 0
fi

/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APPLICATION/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APPLICATION/Contents/Info.plist"

if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  codesign --force --deep --options runtime --entitlements "$ROOT/build/Packaging/MacOS/Entitlements.plist" --sign "$MACOS_SIGN_IDENTITY" "$APPLICATION"
else
  codesign --force --deep --sign - "$APPLICATION"
fi

codesign --verify --deep --strict --verbose=2 "$APPLICATION"
ditto -c -k --keepParent "$APPLICATION" "$ARCHIVE"

if [ -n "${MACOS_NOTARY_PROFILE:-}" ] && [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  xcrun notarytool submit "$ARCHIVE" --keychain-profile "$MACOS_NOTARY_PROFILE" --wait
  xcrun stapler staple "$APPLICATION"
  rm "$ARCHIVE"
  ditto -c -k --keepParent "$APPLICATION" "$ARCHIVE"
fi

create_disk_image() {
  local attempt
  for attempt in 1 2 3 4 5 6; do
    if hdiutil create -volname Voidstrap -srcfolder "$APPLICATION" -ov -format UDZO "$DMG"; then
      return 0
    fi
    echo "hdiutil create failed on attempt $attempt, retrying"
    rm -f "$DMG"
    sleep $((attempt * 5))
  done
  echo "The disk image could not be created"
  return 1
}

create_disk_image

if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  codesign --force --sign "$MACOS_SIGN_IDENTITY" "$DMG"
fi

if [ -n "${MACOS_NOTARY_PROFILE:-}" ] && [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  xcrun notarytool submit "$DMG" --keychain-profile "$MACOS_NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$APPLICATION"
  xcrun stapler validate "$DMG"
fi

commit_artifact "$ARCHIVE" "$ARCHIVE_TARGET"
commit_artifact "$DMG" "$DMG_TARGET"
