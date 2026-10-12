set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="${1:?A runtime identifier is required}"
OUTPUT="${2:?An output library is required}"
TARGET_DIR="${3:-$ROOT/native/voidimage/target}"
GLIBC_FLOOR="${VOIDSTRAP_VOIDIMAGE_GLIBC:-2.17}"

case "$RID" in
  linux-x64) TRIPLE=x86_64-unknown-linux-gnu; FILE=libvoidimage.so ;;
  linux-arm64) TRIPLE=aarch64-unknown-linux-gnu; FILE=libvoidimage.so ;;
  linux-musl-x64) TRIPLE=x86_64-unknown-linux-musl; FILE=libvoidimage.so ;;
  linux-musl-arm64) TRIPLE=aarch64-unknown-linux-musl; FILE=libvoidimage.so ;;
  osx-x64) TRIPLE=x86_64-apple-darwin; FILE=libvoidimage.dylib ;;
  osx-arm64) TRIPLE=aarch64-apple-darwin; FILE=libvoidimage.dylib ;;
  *) echo "voidimage: no native build for $RID, the managed decoder is used"; exit 0 ;;
esac

case "$TRIPLE" in
  *-apple-darwin) [ "$(uname -s)" = "Darwin" ] || { echo "voidimage: $RID is only built on macOS, the managed decoder is used"; exit 0; } ;;
  *-linux-*) [ "$(uname -s)" = "Linux" ] || { echo "voidimage: $RID is only built on Linux, the managed decoder is used"; exit 0; } ;;
esac

PATH="$PATH:$HOME/.cargo/bin"
command -v cargo >/dev/null 2>&1 || { echo "voidimage: cargo is not installed, the managed decoder is used"; exit 0; }

if command -v rustup >/dev/null 2>&1; then
  rustup target list --installed 2>/dev/null | grep -qx "$TRIPLE" || rustup target add "$TRIPLE" >/dev/null 2>&1 || true
fi

case "$TRIPLE" in
  *-linux-musl) export RUSTFLAGS="${RUSTFLAGS:-} -C target-feature=-crt-static" ;;
esac

BUILD_TARGET="$TRIPLE"
ZIGBUILD_HOME="${XDG_CACHE_HOME:-$HOME/.cache}/voidstrap/zigbuild"
if [ "$(uname -s)" = "Linux" ] && [ ! -x "$ZIGBUILD_HOME/bin/cargo-zigbuild" ] && [ "${VOIDSTRAP_VOIDIMAGE_NO_ZIG:-0}" != 1 ]; then
  rm -rf "$ZIGBUILD_HOME"
  if python3 -m venv "$ZIGBUILD_HOME" >/dev/null 2>&1 && "$ZIGBUILD_HOME/bin/python" -m pip install --quiet --disable-pip-version-check ziglang==0.17.0 cargo-zigbuild==0.23.4 >/dev/null 2>&1; then
    echo "voidimage: prepared cargo-zigbuild for old glibc and musl builds"
  else
    rm -rf "$ZIGBUILD_HOME"
  fi
fi
[ -x "$ZIGBUILD_HOME/bin/cargo-zigbuild" ] && PATH="$ZIGBUILD_HOME/bin:$PATH"

if [ "$(uname -s)" = "Linux" ] && command -v cargo-zigbuild >/dev/null 2>&1 && { command -v zig >/dev/null 2>&1 || "$(dirname "$(command -v cargo-zigbuild)")/python" -c "import ziglang" >/dev/null 2>&1 || python3 -c "import ziglang" >/dev/null 2>&1; }; then
  case "$TRIPLE" in
    *-linux-gnu) BUILD_TARGET="$TRIPLE.$GLIBC_FLOOR" ;;
  esac
  COMMAND=(cargo zigbuild)
else
  case "$TRIPLE" in
    aarch64-unknown-linux-gnu)
      if [ "$(uname -m)" != "aarch64" ]; then
        command -v aarch64-linux-gnu-gcc >/dev/null 2>&1 || { echo "voidimage: no aarch64 linker is installed, the managed decoder is used"; exit 0; }
        export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc
      fi
      ;;
    *-linux-musl)
      echo "voidimage: musl builds need cargo-zigbuild, the managed decoder is used"
      exit 0
      ;;
  esac
  COMMAND=(cargo build)
fi

if ! "${COMMAND[@]}" --release --locked --quiet --target "$BUILD_TARGET" --manifest-path "$ROOT/native/voidimage/Cargo.toml" --target-dir "$TARGET_DIR"; then
  echo "voidimage: the native build failed for $RID, the managed decoder is used"
  rm -f "$OUTPUT"
  exit 0
fi

mkdir -p "$(dirname "$OUTPUT")"
cp "$TARGET_DIR/$TRIPLE/release/$FILE" "$OUTPUT"
chmod 644 "$OUTPUT"
echo "voidimage: built $FILE for $RID"
