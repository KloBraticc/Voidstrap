set -euo pipefail

OUTPUT="${1:?Usage: bash build-wgpu-native.sh <output directory> [runtime]}"
RID="${2:-linux-x64}"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
REVISION=33133da4ec5a0174cb21539ef2d3346f75200411
EXPECTED_EXPORTS=250
CARGO_ROOT="${CARGO_HOME:-$HOME/.cargo}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT INT TERM

git clone --quiet https://github.com/gfx-rs/wgpu-native.git "$WORK/wgpu-native"
cd "$WORK/wgpu-native"
git checkout --quiet "$REVISION"
git submodule update --init --quiet ffi/webgpu-headers

if [[ "$RID" != linux-musl-* ]]; then
    sed -i '/^# We want the wgpu-core GLES backend on Unix/,/^features = \["gles"\]/d' Cargo.toml
    if grep -q 'cfg(all(unix, not(target_os = "ios"), not(target_os = "macos")))' Cargo.toml; then
        echo "The Unix OpenGL backend could not be removed from Cargo.toml" >&2
        exit 1
    fi

    if [ "$(grep -c '#\[cfg(gles)\]' src/conv.rs)" != "1" ]; then
        echo "src/conv.rs changed, the OpenGL report guard was not found exactly once" >&2
        exit 1
    fi
    sed -i 's/#\[cfg(gles)\]/#[cfg(any(feature = "angle", windows))]/' src/conv.rs

fi

TARGET_ARGS=()
TARGET_ROOT="${CARGO_TARGET_DIR:-target}"
LIBRARY="$TARGET_ROOT/release/libwgpu_native.so"
COMPILER="${VOIDSTRAP_NATIVE_COMPILER:-}"
case "$RID" in
    linux-x64) COMPILER="${COMPILER:-cc}" ;;
    linux-arm64) TARGET=aarch64-unknown-linux-gnu; COMPILER="${COMPILER:-aarch64-linux-gnu-gcc}" ;;
    linux-musl-x64) TARGET=x86_64-unknown-linux-musl; COMPILER="${COMPILER:-musl-gcc}" ;;
    linux-musl-arm64) TARGET=aarch64-unknown-linux-musl; COMPILER="${COMPILER:-aarch64-linux-musl-gcc}" ;;
    *) echo "Unsupported runtime: $RID" >&2; exit 1 ;;
esac
if ! command -v "$COMPILER" > /dev/null; then
    echo "Compiler not found: $COMPILER. Set VOIDSTRAP_NATIVE_COMPILER to a compiler for $RID." >&2
    exit 1
fi
if [[ -n "${TARGET:-}" ]]; then
    TARGET_ARGS=(--target "$TARGET")
    LINKER_KEY="CARGO_TARGET_${TARGET^^}_LINKER"
    LINKER_KEY="${LINKER_KEY//-/_}"
    export "$LINKER_KEY=$COMPILER"
    LIBRARY="$TARGET_ROOT/$TARGET/release/libwgpu_native.so"
fi

export RUSTFLAGS="--remap-path-prefix=$HOME=/home --remap-path-prefix=$CARGO_ROOT=/cargo --remap-path-prefix=$WORK=/build"
if [[ "$RID" == linux-musl-* ]]; then
    RUSTFLAGS="$RUSTFLAGS -C target-feature=-crt-static"
fi
export BINDGEN_EXTRA_CLANG_ARGS="${BINDGEN_EXTRA_CLANG_ARGS:--I$($COMPILER -print-file-name=include)}"
cargo build --release --locked "${TARGET_ARGS[@]}"
if [[ "$RID" != linux-musl-* ]] && strings "$LIBRARY" | grep 'libEGL' > /dev/null; then
    echo "The OpenGL backend is still linked into $LIBRARY" >&2
    exit 1
fi
if strings "$LIBRARY" | grep -F "$HOME" > /dev/null; then
    echo "$LIBRARY still contains a local home path" >&2
    exit 1
fi

EXPORTS="$(nm -D --defined-only "$LIBRARY" | awk '$3 ~ /^wgpu/' | wc -l)"
if [ "$EXPORTS" != "$EXPECTED_EXPORTS" ]; then
    echo "$LIBRARY exports $EXPORTS functions, expected $EXPECTED_EXPORTS" >&2
    exit 1
fi

if [[ "$RID" == linux-musl-* ]] && readelf --version-info "$LIBRARY" | grep 'GLIBC_' > /dev/null; then
    echo "The graphics library still links against glibc" >&2
    exit 1
fi

mkdir -p "$OUTPUT"
cp "$LIBRARY" "$OUTPUT/libwgpu_native.so"
echo "Built $OUTPUT/libwgpu_native.so for $RID from wgpu-native $REVISION ($EXPORTS exports)"
