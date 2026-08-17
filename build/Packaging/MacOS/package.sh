set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RID="${1:?A macOS runtime identifier is required}"
VERSION="${2:?A version is required}"
OUTPUT="${3:?An output directory is required}"

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
APPLICATION_TARGET="$OUTPUT/Voidstrap.app"
DMG_TARGET="$OUTPUT/Voidstrap-$RID.dmg"
ARCHIVE_TARGET="$OUTPUT/Voidstrap-$RID.zip"

if [ -e "$APPLICATION_TARGET" ] || [ -e "$DMG_TARGET" ] || [ -e "$ARCHIVE_TARGET" ]; then
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
STAGE="$(mktemp -d "$OUTPUT/.voidstrap-macos.XXXXXX")"
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
dotnet publish "$ROOT/src/Voidstrap.Cross/Voidstrap.Cross.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH" -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false
mkdir -p "$APPLICATION/Contents/MacOS" "$APPLICATION/Contents/Resources"
cp -R "$PUBLISH/." "$APPLICATION/Contents/MacOS/"
cp "$ROOT/build/Packaging/MacOS/Info.plist" "$APPLICATION/Contents/Info.plist"
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

hdiutil create -volname Voidstrap -srcfolder "$APPLICATION" -ov -format UDZO "$DMG"

if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  codesign --force --sign "$MACOS_SIGN_IDENTITY" "$DMG"
fi

if [ -n "${MACOS_NOTARY_PROFILE:-}" ] && [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  xcrun notarytool submit "$DMG" --keychain-profile "$MACOS_NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$APPLICATION"
  xcrun stapler validate "$DMG"
fi

commit_artifact "$APPLICATION" "$APPLICATION_TARGET"
commit_artifact "$ARCHIVE" "$ARCHIVE_TARGET"
commit_artifact "$DMG" "$DMG_TARGET"
