set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="${1:?A runtime identifier is required}"
OUTPUT="${2:?An output library is required}"
TARGET_DIR="${3:-$ROOT/native/voidimage/target}"

case "$RID" in
  linux-x64) TRIPLE=x86_64-unknown-linux-gnu; FILE=libvoidimage.so ;;
  linux-arm64) TRIPLE=aarch64-unknown-linux-gnu; FILE=libvoidimage.so ;;
  osx-x64) TRIPLE=x86_64-apple-darwin; FILE=libvoidimage.dylib ;;
  osx-arm64) TRIPLE=aarch64-apple-darwin; FILE=libvoidimage.dylib ;;
  *) echo "voidimage: no native build for $RID, the managed decoder is used"; exit 0 ;;
esac

case "$TRIPLE" in
  *-apple-darwin) [ "$(uname -s)" = "Darwin" ] || { echo "voidimage: $RID is only built on macOS, the managed decoder is used"; exit 0; } ;;
  *-linux-gnu) [ "$(uname -s)" = "Linux" ] || { echo "voidimage: $RID is only built on Linux, the managed decoder is used"; exit 0; } ;;
esac

CARGO="$(command -v cargo || true)"
[ -n "$CARGO" ] || [ ! -x "$HOME/.cargo/bin/cargo" ] || CARGO="$HOME/.cargo/bin/cargo"
[ -n "$CARGO" ] || { echo "voidimage: cargo is not installed, the managed decoder is used"; exit 0; }

if command -v rustup >/dev/null 2>&1; then
  rustup target list --installed 2>/dev/null | grep -qx "$TRIPLE" || rustup target add "$TRIPLE" >/dev/null 2>&1 || true
elif [ -x "$HOME/.cargo/bin/rustup" ]; then
  "$HOME/.cargo/bin/rustup" target list --installed 2>/dev/null | grep -qx "$TRIPLE" || "$HOME/.cargo/bin/rustup" target add "$TRIPLE" >/dev/null 2>&1 || true
fi

if [ "$TRIPLE" = "aarch64-unknown-linux-gnu" ] && [ "$(uname -m)" != "aarch64" ]; then
  command -v aarch64-linux-gnu-gcc >/dev/null 2>&1 || { echo "voidimage: aarch64-linux-gnu-gcc is not installed, the managed decoder is used"; exit 0; }
  export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc
fi

if ! "$CARGO" build --release --locked --quiet --target "$TRIPLE" --manifest-path "$ROOT/native/voidimage/Cargo.toml" --target-dir "$TARGET_DIR"; then
  echo "voidimage: the native build failed for $RID, the managed decoder is used"
  rm -f "$OUTPUT"
  exit 0
fi

mkdir -p "$(dirname "$OUTPUT")"
cp "$TARGET_DIR/$TRIPLE/release/$FILE" "$OUTPUT"
chmod 644 "$OUTPUT"
