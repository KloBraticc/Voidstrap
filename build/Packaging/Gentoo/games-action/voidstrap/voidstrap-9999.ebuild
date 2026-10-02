EAPI=8

inherit desktop git-r3

DESCRIPTION="Roblox bootstrapper for Linux, fork of Bloxstrap (launches Roblox through Sober)"
HOMEPAGE="https://github.com/KloBraticc/Voidstrap"

EGIT_REPO_URI="https://github.com/KloBraticc/Voidstrap.git"
EGIT_BRANCH="main"
EGIT_SUBMODULES=()

SRC_URI="
	https://github.com/bloxstraplabs/wpfui/archive/f710123e72d9dcc8d09fccc4e2a783cc5cf5e652.tar.gz
"

LICENSE="MIT"
SLOT="0"
KEYWORDS=""

RESTRICT="network-sandbox mirror strip"

BDEPEND="
	>=dev-dotnet/dotnet-sdk-bin-10.0.300
"

RDEPEND="
	media-libs/vulkan-loader
	media-libs/mesa[vulkan]
	x11-libs/libX11
	x11-libs/libXext
	x11-libs/libXrender
	x11-libs/libXrandr
	x11-libs/libXi
	x11-libs/libXcursor
	x11-libs/libXfixes
	x11-libs/libXcomposite
	x11-libs/libXdamage
	x11-libs/libICE
	x11-libs/libSM
	media-libs/fontconfig
	media-libs/freetype
	x11-libs/libxkbcommon[X]
	dev-libs/wayland
	sys-apps/dbus
	virtual/opengl
	dev-libs/openssl:0
	sys-apps/flatpak
	app-misc/ca-certificates
	sys-libs/zlib
"

pkg_setup() {
	export DOTNET_CLI_TELEMETRY_OPTOUT=1
	export DOTNET_NOLOGO=1
	export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
	export MSBUILDDISABLENODEREUSE=1
	export DOTNET_CLI_HOME="${T}/dotnet-home"

	mkdir -p "${DOTNET_CLI_HOME}" || die

	case "${CHOST}:${ARCH}" in
		*-linux-gnu*:amd64)
			VOIDSTRAP_RID="linux-x64"
			;;

		*-linux-gnu*:arm64)
			VOIDSTRAP_RID="linux-arm64"
			;;

		*-linux-musl*:amd64)
			VOIDSTRAP_RID="linux-musl-x64"
			;;

		*-linux-musl*:arm64)
			VOIDSTRAP_RID="linux-musl-arm64"
			;;

		*)
			die "Unsupported platform: CHOST=${CHOST}, ARCH=${ARCH}"
			;;
	esac

	einfo "CHOST: ${CHOST}"
	einfo "ARCH: ${ARCH}"
	einfo "Voidstrap RID: ${VOIDSTRAP_RID}"
}

src_compile() {
	dotnet publish \
		src/Voidstrap.Cross/Voidstrap.Cross.csproj \
		-c Release \
		-r "${VOIDSTRAP_RID}" \
		--self-contained true \
		-o "${S}/build/publish" \
		-p:DebugType=None \
		-p:DebugSymbols=false \
		|| die "dotnet publish failed"
}

src_install() {
	dodir /usr/lib/voidstrap
	cp -R build/publish/. "${D}/usr/lib/voidstrap/" || die

	fperms 755 /usr/lib/voidstrap/Voidstrap

	dosym /usr/lib/voidstrap/Voidstrap /usr/bin/voidstrap

	domenu build/Packaging/Linux/voidstrap.desktop

	newicon -s 256 \
		src/Voidstrap.App/Voidstrap.png \
		io.github.KloBraticc.Voidstrap.png

	insinto /usr/share/metainfo
	doins build/Packaging/Linux/io.github.KloBraticc.Voidstrap.metainfo.xml

	dodoc \
		LICENSE.VOIDSTRAP \
		LICENSE.BLOXSTRAP \
		LICENSE.FISHSTRAP \
		README.md
}
