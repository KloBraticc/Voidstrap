set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RID="${1:?A Linux runtime identifier is required}"
OUTPUT="${2:?An output library is required}"
SOURCE="${3:-$ROOT/build/Packaging/Linux/editor-compat.c}"

case "$RID" in
  linux-x64|linux-musl-x64) ARCH=x86_64 ;;
  linux-arm64|linux-musl-arm64) ARCH=aarch64 ;;
  *) echo "Unsupported Linux runtime identifier" >&2; exit 1 ;;
esac

if [ "$ARCH" = "$(uname -m)" ]; then
  COMPILER="${VOIDSTRAP_EDITOR_COMPILER:-cc}"
elif [ "$ARCH" = "aarch64" ]; then
  COMPILER="${VOIDSTRAP_EDITOR_COMPILER:-aarch64-linux-gnu-gcc}"
else
  COMPILER="${VOIDSTRAP_EDITOR_COMPILER:-x86_64-linux-gnu-gcc}"
fi
command -v "$COMPILER" >/dev/null 2>&1 || { echo "The $ARCH C compiler is unavailable" >&2; exit 1; }
mkdir -p "$(dirname "$OUTPUT")"
"$COMPILER" -shared -nostdlib -fPIC -O2 -fno-ident -fno-stack-protector -Wl,--build-id=sha1 -o "$OUTPUT" "$SOURCE"
chmod 644 "$OUTPUT"
