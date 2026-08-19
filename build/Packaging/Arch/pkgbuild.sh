set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
VERSION="${1:-}"
OUTPUT="${2:?An output directory is required}"
PROJECT_URL="https://github.com/KloBraticc/Voidstrap"

if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:^[[:space:]]*<VoidstrapVersion>\(.*\)</VoidstrapVersion>[[:space:]]*$:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
fi

if [[ ! "$VERSION" =~ ^[0-9]+([.][0-9]+){1,3}$ ]]; then
  echo "The package version is invalid"
  exit 1
fi

mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
TARGET="$OUTPUT/PKGBUILD"

cat > "$TARGET" <<PKGBUILD
pkgname=voidstrap-bin
pkgver=$VERSION
pkgrel=1
pkgdesc='Voidstrap Roblox desktop launcher'
arch=('x86_64' 'aarch64')
url='$PROJECT_URL'
license=('LicenseRef-Voidstrap')
depends=('glibc' 'gcc-libs' 'zlib' 'libx11' 'libice' 'libsm' 'fontconfig' 'freetype2' 'libglvnd' 'openssl' 'ca-certificates' 'hicolor-icon-theme' 'desktop-file-utils')
optdepends=('flatpak: required to install and run the Sober Roblox runtime'
            'xdg-utils: protocol handler registration'
            'libnotify: desktop notifications'
            'libsecret: credential storage'
            'vulkan-icd-loader: Vulkan rendering backend'
            'wayland: Wayland rendering backend'
            'webkit2gtk-4.1: embedded web views')
provides=('voidstrap')
conflicts=('voidstrap')
options=('!strip' '!debug')
source=("voidstrap-\$pkgver.desktop::\$url/raw/v\$pkgver/build/Packaging/Linux/voidstrap.desktop"
        "voidstrap-\$pkgver.png::\$url/raw/v\$pkgver/src/Voidstrap.App/Voidstrap.png"
        "voidstrap-\$pkgver.license::\$url/raw/v\$pkgver/LICENSE.VOIDSTRAP")
source_x86_64=("\$url/releases/download/v\$pkgver/Voidstrap_\${pkgver}_linux-x64.tar.gz")
source_aarch64=("\$url/releases/download/v\$pkgver/Voidstrap_\${pkgver}_linux-arm64.tar.gz")
sha256sums=('SKIP' 'SKIP' 'SKIP')
sha256sums_x86_64=('SKIP')
sha256sums_aarch64=('SKIP')

package() {
    install -Dm755 "\$srcdir/Voidstrap/Voidstrap" "\$pkgdir/usr/lib/voidstrap/Voidstrap"
    install -dm755 "\$pkgdir/usr/bin"
    ln -s /usr/lib/voidstrap/Voidstrap "\$pkgdir/usr/bin/voidstrap"
    install -Dm644 "\$srcdir/voidstrap-\$pkgver.desktop" "\$pkgdir/usr/share/applications/voidstrap.desktop"
    install -Dm644 "\$srcdir/voidstrap-\$pkgver.png" "\$pkgdir/usr/share/icons/hicolor/256x256/apps/voidstrap.png"
    install -Dm644 "\$srcdir/voidstrap-\$pkgver.license" "\$pkgdir/usr/share/licenses/\$pkgname/LICENSE"
}
PKGBUILD

echo "Wrote $TARGET"
