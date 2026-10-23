set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="${1:?A runtime identifier is required}"
OUTPUT="${2:?An output library is required}"

case "$RID" in
  linux-x64|linux-arm64) ;;
  *) echo "voidlayer: no native build for $RID"; exit 0 ;;
esac
[ "$(uname -s)" = "Linux" ] || { echo "voidlayer: $RID is only built on Linux"; exit 0; }

COMPILER="${CC:-cc}"
case "$RID" in
  linux-arm64) [ "$(uname -m)" = "aarch64" ] || COMPILER="${VOIDSTRAP_ARM64_CC:-aarch64-linux-gnu-gcc}" ;;
  linux-x64) [ "$(uname -m)" = "x86_64" ] || COMPILER="${VOIDSTRAP_X64_CC:-x86_64-linux-gnu-gcc}" ;;
esac
command -v "$COMPILER" >/dev/null 2>&1 || { echo "voidlayer: $COMPILER is not installed"; exit 0; }
printf '#include <vulkan/vk_layer.h>\n' | "$COMPILER" -E -x c - >/dev/null 2>&1 || { echo "voidlayer: the Vulkan headers are not installed"; exit 0; }

mkdir -p "$(dirname "$OUTPUT")"
"$COMPILER" -O2 -fPIC -shared -fvisibility=hidden -Wall -Wextra -Wl,--build-id=none -ffile-prefix-map="$ROOT"=. \
  -o "$OUTPUT.tmp" "$ROOT/native/voidlayer/voidstrap_overlay.c" -lpthread || { echo "voidlayer: the layer could not be compiled"; rm -f "$OUTPUT.tmp"; exit 0; }
mv -f "$OUTPUT.tmp" "$OUTPUT"
echo "voidlayer: built $(basename "$OUTPUT") for $RID"
