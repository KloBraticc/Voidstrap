set -euo pipefail

OUTPUT="${1:?Usage: bash build-glfw-native.sh <output directory> [runtime]}"
RID="${2:-linux-musl-x64}"
REVISION=7b6aead9fb88b3623e3b3725ebb42670cbe4c579
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT INT TERM

git clone --quiet https://github.com/glfw/glfw.git "$WORK/glfw"
git -C "$WORK/glfw" checkout --quiet "$REVISION"
CMAKE_ARGS=(-DBUILD_SHARED_LIBS=ON -DGLFW_BUILD_EXAMPLES=OFF -DGLFW_BUILD_TESTS=OFF -DGLFW_BUILD_DOCS=OFF -DCMAKE_BUILD_TYPE=Release)
case "$RID" in
    linux-musl-x64) PROCESSOR=x86_64 ;;
    linux-musl-arm64) PROCESSOR=aarch64 ;;
    *) echo "Unsupported runtime: $RID" >&2; exit 1 ;;
esac
if [[ -n "${VOIDSTRAP_NATIVE_COMPILER:-}" ]]; then
    CMAKE_ARGS+=(-DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR="$PROCESSOR" -DCMAKE_C_COMPILER="$VOIDSTRAP_NATIVE_COMPILER" -DMATH_LIBRARY=m -DRT_LIBRARY=rt)
fi
cmake -S "$WORK/glfw" -B "$WORK/build" "${CMAKE_ARGS[@]}"
cmake --build "$WORK/build" --parallel "${VOIDSTRAP_NATIVE_JOBS:-4}"
mkdir -p "$OUTPUT"
cp "$WORK/build/src/libglfw.so.3.4" "$OUTPUT/libglfw.so.3"
"${VOIDSTRAP_NATIVE_STRIP:-strip}" --strip-all "$OUTPUT/libglfw.so.3"
if readelf --version-info "$OUTPUT/libglfw.so.3" | grep 'GLIBC_' > /dev/null; then
    echo "The window library still links against glibc" >&2
    exit 1
fi
cp "$WORK/glfw/LICENSE.md" "$OUTPUT/GLFW-LICENSE.txt"
if strings "$OUTPUT/libglfw.so.3" | grep -F "$WORK" > /dev/null; then
    echo "The window library still contains a build path" >&2
    exit 1
fi
