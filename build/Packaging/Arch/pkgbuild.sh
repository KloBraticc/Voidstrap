set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
VERSION="${1:-}"
OUTPUT="${2:?An output directory is required}"
X64_CHECKSUM_SOURCE="${3:?An x86_64 archive or SHA256 checksum is required}"
ARM64_CHECKSUM_SOURCE="${4:?An aarch64 archive or SHA256 checksum is required}"
PROJECT_URL="https://github.com/KloBraticc/Voidstrap"

if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:^[[:space:]]*<VoidstrapVersion>\(.*\)</VoidstrapVersion>[[:space:]]*$:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
fi

if [[ ! "$VERSION" =~ ^[0-9]+([.][0-9]+){1,3}$ ]]; then
  echo "The package version is invalid"
  exit 1
fi

resolve_checksum() {
  local source="$1"
  local checksum
  if [ -f "$source" ]; then
    checksum="$(sha256sum "$source" | awk '{print $1}')"
  else
    checksum="${source,,}"
  fi
  if [[ ! "$checksum" =~ ^[0-9a-f]{64}$ ]]; then
    echo "The archive checksum is invalid" >&2
    return 1
  fi
  printf '%s' "$checksum"
}

X64_CHECKSUM="$(resolve_checksum "$X64_CHECKSUM_SOURCE")"
ARM64_CHECKSUM="$(resolve_checksum "$ARM64_CHECKSUM_SOURCE")"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
PKGBUILD_TARGET="$OUTPUT/PKGBUILD"
SRCINFO_TARGET="$OUTPUT/.SRCINFO"
if [ -e "$PKGBUILD_TARGET" ] || [ -e "$SRCINFO_TARGET" ]; then
  echo "AUR metadata already exists in the output directory"
  exit 1
fi

cat > "$PKGBUILD_TARGET" <<PKGBUILD
pkgname=voidstrap-bin
pkgver=$VERSION
pkgrel=1
pkgdesc='Customize and launch Roblox through Sober on Linux'
arch=('x86_64' 'aarch64')
url='$PROJECT_URL'
license=('MIT')
depends=('glibc' 'gcc-libs' 'zlib' 'vulkan-icd-loader' 'vulkan-driver' 'libx11' 'libxext' 'libxrender' 'libxrandr' 'libxi' 'libxcursor' 'libxfixes' 'libice' 'libsm' 'fontconfig' 'freetype2' 'libxkbcommon' 'libxkbcommon-x11' 'wayland' 'dbus' 'libglvnd' 'openssl' 'ca-certificates' 'flatpak' 'hicolor-icon-theme' 'desktop-file-utils')
optdepends=('xdg-utils: desktop protocol registration tools'
            'libnotify: desktop notifications'
            'libsecret: credential storage'
            'gstreamer: image and video playback'
            'gst-plugins-base: common media codecs'
            'gst-plugins-good: additional media codecs'
            'gst-plugins-bad: additional media codecs'
            'gst-libav: FFmpeg backed media codecs'
            'webkit2gtk-4.1: embedded web views')
provides=('voidstrap')
conflicts=('voidstrap')
options=('!strip' '!debug')
source_x86_64=("\$url/releases/download/v\$pkgver/Voidstrap_\${pkgver}_linux-x64.tar.gz")
source_aarch64=("\$url/releases/download/v\$pkgver/Voidstrap_\${pkgver}_linux-arm64.tar.gz")
sha256sums_x86_64=('$X64_CHECKSUM')
sha256sums_aarch64=('$ARM64_CHECKSUM')

package() {
    install -Dm755 "\$srcdir/Voidstrap/Voidstrap" "\$pkgdir/usr/lib/voidstrap/Voidstrap"
    install -dm755 "\$pkgdir/usr/bin"
    ln -s /usr/lib/voidstrap/Voidstrap "\$pkgdir/usr/bin/voidstrap"
    install -dm755 "\$pkgdir/usr/share"
    cp -a "\$srcdir/Voidstrap/share/." "\$pkgdir/usr/share/"
}
PKGBUILD

if command -v makepkg >/dev/null 2>&1; then
  (cd "$OUTPUT" && makepkg --printsrcinfo) > "$SRCINFO_TARGET"
else
  cat > "$SRCINFO_TARGET" <<SRCINFO
pkgbase = voidstrap-bin
	pkgdesc = Customize and launch Roblox through Sober on Linux
	pkgver = $VERSION
	pkgrel = 1
	url = $PROJECT_URL
	arch = x86_64
	arch = aarch64
	license = MIT
	depends = glibc
	depends = gcc-libs
	depends = zlib
	depends = libx11
	depends = libxext
	depends = libxrender
	depends = libxrandr
	depends = libxi
	depends = libxcursor
	depends = libxfixes
	depends = libice
	depends = libsm
	depends = fontconfig
	depends = freetype2
	depends = libxkbcommon
	depends = libxkbcommon-x11
	depends = wayland
	depends = dbus
	depends = libglvnd
	depends = openssl
	depends = ca-certificates
	depends = flatpak
	depends = hicolor-icon-theme
	depends = desktop-file-utils
	optdepends = xdg-utils: desktop protocol registration tools
	optdepends = libnotify: desktop notifications
	optdepends = libsecret: credential storage
	optdepends = vulkan-icd-loader: Vulkan rendering backend
	optdepends = gstreamer: image and video playback
	optdepends = gst-plugins-base: common media codecs
	optdepends = gst-plugins-good: additional media codecs
	optdepends = gst-plugins-bad: additional media codecs
	optdepends = gst-libav: FFmpeg backed media codecs
	optdepends = webkit2gtk-4.1: embedded web views
	provides = voidstrap
	conflicts = voidstrap
	options = !strip
	options = !debug
	source_x86_64 = $PROJECT_URL/releases/download/v$VERSION/Voidstrap_${VERSION}_linux-x64.tar.gz
	sha256sums_x86_64 = $X64_CHECKSUM
	source_aarch64 = $PROJECT_URL/releases/download/v$VERSION/Voidstrap_${VERSION}_linux-arm64.tar.gz
	sha256sums_aarch64 = $ARM64_CHECKSUM

pkgname = voidstrap-bin
SRCINFO
fi

echo "Wrote $PKGBUILD_TARGET and $SRCINFO_TARGET"
