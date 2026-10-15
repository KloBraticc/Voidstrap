{
  lib,
  stdenv,
  buildDotnetModule,
  dotnetCorePackages,
  fetchFromGitHub,
  rustPlatform,
  autoPatchelfHook,
  nix-update-script,
  alsa-lib,
  dbus,
  fontconfig,
  freetype,
  glib,
  gst_all_1,
  icu,
  libGL,
  libICE,
  libSM,
  libX11,
  libXcomposite,
  libXcursor,
  libXdamage,
  libXext,
  libXfixes,
  libXi,
  libXrandr,
  libXrender,
  libxkbcommon,
  libdrm,
  mesa,
  openssl,
  vulkan-loader,
  wayland,
  zlib,
}:

let
  runtimeLibraries = [
    alsa-lib
    dbus
    fontconfig
    freetype
    glib
    gst_all_1.gstreamer
    gst_all_1.gst-plugins-base
    icu
    libGL
    libICE
    libSM
    libX11
    libXcomposite
    libXcursor
    libXdamage
    libXext
    libXfixes
    libXi
    libXrandr
    libXrender
    libxkbcommon
    libdrm
    mesa
    openssl
    vulkan-loader
    wayland
    zlib
  ];
in
buildDotnetModule (finalAttrs: {
  pname = "voidstrap";
  version = "1.1.3.5";

  src = fetchFromGitHub {
    owner = "KloBraticc";
    repo = "Voidstrap";
    tag = "v${finalAttrs.version}";
    fetchSubmodules = true;
    hash = "sha256-xLKSOLN0qXCjZWRNSILExvy4RdFxtlK+MOmJmxdf4o8=";
  };

  projectFile = "src/Voidstrap.Cross/Voidstrap.Cross.csproj";
  nugetDeps = ./deps.json;

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = null;
  selfContainedBuild = true;

  executables = [ "Voidstrap" ];

  nativeBuildInputs = [ autoPatchelfHook ];
  buildInputs = [ (lib.getLib stdenv.cc.cc) ] ++ runtimeLibraries;
  runtimeDeps = runtimeLibraries;
  autoPatchelfIgnoreMissingDeps = [ "*" ];

  preConfigure = ''
    dotnetFlags+=("-p:VoidstrapLinuxPackagingRoot=$PWD/.nix-artifacts/")
  '';

  dotnetBuildFlags = [ "-p:VoidImageLibrary=${finalAttrs.passthru.voidimage}/lib/libvoidimage.so" ];
  dotnetInstallFlags = [
    "-p:PublishSingleFile=false"
    "-p:IncludeAllContentForSelfExtract=false"
    "-p:DebugType=None"
    "-p:DebugSymbols=false"
    "-p:VoidImageLibrary=${finalAttrs.passthru.voidimage}/lib/libvoidimage.so"
  ];

  env = {
    VOIDSTRAP_VOIDIMAGE_NO_ZIG = "1";
    VOIDSTRAP_EDITOR_COMPILER = "${stdenv.cc}/bin/cc";
  };

  postInstall = ''
    for library in uc i18n data; do
      if [ -e $out/lib/voidstrap/libicu$library.so.72.1.0.3 ]; then
        ln -s libicu$library.so.72.1.0.3 $out/lib/voidstrap/libicu$library.so.72
      fi
    done
    install -Dm644 build/Packaging/Linux/voidstrap.desktop \
      $out/share/applications/io.github.KloBraticc.Voidstrap.desktop
    install -Dm644 src/Voidstrap.App/Voidstrap.png \
      $out/share/icons/hicolor/256x256/apps/io.github.KloBraticc.Voidstrap.png
    install -Dm644 build/Packaging/Linux/io.github.KloBraticc.Voidstrap.metainfo.xml \
      $out/share/metainfo/io.github.KloBraticc.Voidstrap.metainfo.xml
  '';

  postFixup = ''
    ln -s Voidstrap $out/bin/voidstrap
  '';

  passthru = {
    voidimage = rustPlatform.buildRustPackage {
      pname = "voidimage";
      inherit (finalAttrs) version src;
      cargoRoot = "native/voidimage";
      buildAndTestSubdir = "native/voidimage";
      cargoHash = "sha256-25ly3BBUjnx7nzhCbZi0GkHVuWYEtj+PG1Y1EefzZqY=";
      doCheck = false;
    };
    updateScript = nix-update-script { };
  };

  meta = {
    description = "Roblox bootstrapper that launches Roblox through Sober on Linux";
    homepage = "https://github.com/KloBraticc/Voidstrap";
    changelog = "https://github.com/KloBraticc/Voidstrap/releases/tag/v${finalAttrs.version}";
    license = lib.licenses.mit;
    mainProgram = "voidstrap";
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
    ];
  };
})
