EAPI=8

inherit desktop git-r3 xdg

DESCRIPTION="Customize and launch Roblox through Sober on Linux"
HOMEPAGE="https://github.com/KloBraticc/Voidstrap"
EGIT_REPO_URI="https://github.com/KloBraticc/Voidstrap.git"
EGIT_BRANCH="main"
EGIT_SUBMODULES=()

LICENSE="MIT"
SLOT="0"
KEYWORDS=""
RESTRICT="mirror network-sandbox strip"

BDEPEND=">=dev-dotnet/dotnet-sdk-bin-10.0.300:10.0"
RDEPEND="
	app-misc/ca-certificates
	dev-libs/glib
	app-crypt/libsecret
	dev-libs/openssl:0
	dev-libs/wayland
	media-libs/fontconfig
	media-libs/freetype
	media-libs/gstreamer:1.0
	media-libs/gst-plugins-base:1.0
	media-libs/gst-plugins-good:1.0
	media-libs/gst-plugins-bad:1.0
	media-plugins/gst-plugins-meta:1.0[ffmpeg,flac,mp3,opus,pulseaudio,vorbis,vpx]
	net-libs/webkit-gtk:4.1
	sys-auth/polkit
	media-libs/vulkan-loader
	sys-apps/dbus
	sys-apps/flatpak
	sys-libs/zlib
	virtual/opengl
	x11-misc/xdg-utils
	x11-misc/xdg-user-dirs
	dev-util/desktop-file-utils
	x11-libs/libnotify
	x11-libs/libICE
	x11-libs/libSM
	x11-libs/libX11
	x11-libs/libXcomposite
	x11-libs/libXcursor
	x11-libs/libXdamage
	x11-libs/libXext
	x11-libs/libXfixes
	x11-libs/libXi
	x11-libs/libXrandr
	x11-libs/libXrender
	x11-libs/libxkbcommon[X]
"

QA_PREBUILT="usr/lib/voidstrap/*"

pkg_setup() {
	export DOTNET_CLI_TELEMETRY_OPTOUT=1
	export DOTNET_NOLOGO=1
	export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
	export MSBUILDDISABLENODEREUSE=1
	export DOTNET_CLI_HOME="${T}/dotnet-home"
	export NUGET_PACKAGES="${T}/nuget"
}

src_compile() {
	local rid
	case ${ARCH} in
		amd64) rid=x64 ;;
		arm64) rid=arm64 ;;
		*) die "Voidstrap does not support ${ARCH}" ;;
	esac
	use elibc_musl && rid="musl-${rid}"

	local dotnet
	dotnet=$(type -P dotnet-bin-10.0 || type -P dotnet) || die "The .NET 10 SDK was not found"
	mkdir -p "${DOTNET_CLI_HOME}" "${T}/publish" || die

	"${dotnet}" publish src/Voidstrap.Cross/Voidstrap.Cross.csproj \
		-c Release \
		-r "linux-${rid}" \
		--self-contained true \
		-o "${T}/publish" \
		-p:VoidstrapLinuxPackagingRoot="${T}/build/" \
		-p:DebugType=none \
		-p:DebugSymbols=false \
		-p:ContinuousIntegrationBuild=true \
		|| die "dotnet publish failed"
}

src_install() {
	dodir /usr/lib/voidstrap
	cp -R "${T}/publish/." "${ED}/usr/lib/voidstrap/" || die
	fperms 0755 /usr/lib/voidstrap/Voidstrap
	dosym -r /usr/lib/voidstrap/Voidstrap /usr/bin/voidstrap

	newmenu build/Packaging/Linux/voidstrap.desktop io.github.KloBraticc.Voidstrap.desktop
	newicon -s 256 src/Voidstrap.App/Voidstrap.png io.github.KloBraticc.Voidstrap.png
	insinto /usr/share/metainfo
	doins build/Packaging/Linux/io.github.KloBraticc.Voidstrap.metainfo.xml

	dodoc README.md LICENSE.VOIDSTRAP LICENSE.BLOXSTRAP LICENSE.FISHSTRAP
}
