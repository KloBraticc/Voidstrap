<p align="center">
<a href="https://voidstrapp.pages.dev/">
<img src="https://github.com/KloBraticc/Voidstrap/blob/main/src/Voidstrap.App/Voidstrap.png" alt="preview" width="100px"/>
</a>
</p>

<h1 align="center"><b>Voidstrap</b></h1>

<p align="center">
  <img src="https://raw.githubusercontent.com/KloBraticc/Voidstrap/main/assets/save.png" alt="preview" width="85%"/>
</p>

<p align="center">
  <a href="https://github.com/KloBraticc/Voidstrap/releases/latest">Latest release</a> |
  <a href="https://voidstrapp.pages.dev/">Website</a> |
  <a href="https://discord.gg/5tJBqBH8ck">Discord</a>
</p>

<div align="center">

[![Total Downloads][shield-repo-total]][repo-releases]
[![Latest Downloads][shield-repo-downloads]][repo-latest]
[![Latest Release][shield-repo-latest]][repo-latest]
[![Discord][shield-discord-server]][discord-invite]
[![Stars][shield-repo-stars]][repo-stargazers]
[![Localized][shield-crowdin-localized]][crowdin-project]
[![Builds][shield-actions-build]][repo-actions]

</div>

<h5 align="center">
  Leave a star if you like the project! ⭐️
</h5>

<p align="center">
  <picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/windows-tile-dark.svg"><img src="assets/readme/windows-tile-light.svg" alt="Windows" width="32" height="32"/></picture>
  &nbsp;&nbsp;
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/android/android-original.svg" alt="Android" width="32" height="32"/>
  &nbsp;&nbsp;
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/linux/linux-original.svg" alt="Linux" width="32" height="32"/>
  &nbsp;&nbsp;
  <picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/macos-dark.svg"><img src="assets/readme/macos-light.svg" alt="macOS" width="32" height="32"/></picture>
</p>

**Windows** (PowerShell)

```powershell
irm https://voidstrapp.pages.dev/quick-install | iex
```

**Linux and macOS**

```bash
curl -fsSL https://voidstrapp.pages.dev/install.sh | sh
```

---

> [!IMPORTANT]
> Voidstrap is cross platform and supports **Windows 10 and above**, **macOS**, **Linux**, and **Android**.

> [!WARNING]
> Voidstrap is not an exploit and never will be. We are not considered an exploit. We are here to give users more freedom, features, and support for Roblox.
>
> As of Voidstrap Version `1.1.2.3`, Multi-Instance Launching has been removed from the app and will not be added back in the future.

## Contents

* [Installation](#installation)
* [FAQ](#faq)
* [Building](#building)
* [MCP](#mcp)
* [Inspiration and credits](#inspiration--credits)
* [License](#license)

## Installation

1. Download the [latest version](https://github.com/KloBraticc/Voidstrap/releases/latest).
2. Run the `.exe` and complete the setup.
3. Launch Voidstrap.
4. Enjoy a simpler Roblox!

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-setup-dark.svg"><img src="assets/readme/android-setup-light.svg" alt="Android setup" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

Voidstrap for Android needs Android 7 or newer. Download `Voidstrap-Android-direct-<version>.apk` from the [latest release](https://github.com/KloBraticc/Voidstrap/releases/latest) and install it.

Android restricts access to Roblox's files, so Voidstrap needs extra permission to apply your FastFlags. Choose one of the three methods below in **Settings > Voidstrap helper**. The app also shows your exact command with a copy button.

| Method | Needs a computer | After your phone restarts |
| --- | --- | --- |
| Shizuku (easiest) | No | Start Shizuku again, and Voidstrap takes care of the rest |
| ADB | Yes | Run the start command again |
| Root | No | Nothing, it keeps working |

Mods only work with root.

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-shizuku-dark.svg"><img src="assets/readme/android-shizuku-light.svg" alt="Shizuku" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

1. Install [Shizuku](https://shizuku.rikka.app/) and follow its setup guide.
2. In Voidstrap, open **Settings > Voidstrap helper** and tap **Start with Shizuku**.
3. Allow Voidstrap when Shizuku asks.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-adb-usb-dark.svg"><img src="assets/readme/android-adb-usb-light.svg" alt="ADB with a USB cable" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

1. Turn on Developer options: open **Settings > About phone** and tap **Build number** seven times. On some phones, Build number is inside **Software information**, or is called **OS version** instead.
2. Open **Developer options** (usually under **Settings > System**) and turn on **USB debugging**.
3. On your computer, download Google's [SDK Platform Tools](https://developer.android.com/tools/releases/platform-tools) and extract the archive.
4. Open a terminal inside the unzipped `platform-tools` folder.
5. Plug your phone into the computer with a USB cable, then run:

   ```bash
   adb devices
   ```

6. Unlock your phone and tap **Allow** on the USB debugging prompt.
7. Run the start command:

   ```bash
   adb shell "content read --uri content://com.voidstrap.android.direct.start/start.sh | sh"
   ```

In PowerShell, use `.\adb` instead of `adb`. If `adb devices` shows nothing, try another cable. Some cables only support charging.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-adb-wifi-dark.svg"><img src="assets/readme/android-adb-wifi-light.svg" alt="ADB over WiFi (Android 11 or newer)" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

1. Follow steps 1 to 4 of the USB cable guide.
2. Connect your phone and computer to the same WiFi network.
3. In **Developer options**, turn on **Wireless debugging**, open it, and tap **Pair device with pairing code**.
4. Pair using the IP address, port and code shown in that popup:

   ```bash
   adb pair IP:PORT CODE
   ```

5. Connect using the IP address and port shown on the main Wireless debugging screen. This port is different from the pairing port.

   ```bash
   adb connect IP:PORT
   ```

6. Run the start command from step 7 of the USB cable guide.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-root-dark.svg"><img src="assets/readme/android-root-light.svg" alt="Root" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

In Voidstrap, open **Settings**, turn on **Use root**, and allow Voidstrap in your root manager.

</details>

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/gentoo-setup-dark.svg"><img src="assets/readme/gentoo-setup-light.svg" alt="Gentoo Linux" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

Voidstrap is packaged for Gentoo in the [Voidstrap overlay](https://github.com/Pedrodroks34/voidstrap-overlay), maintained by [@Pedrodroks34](https://github.com/Pedrodroks34).

The overlay provides two ebuilds:

- `games-action/voidstrap`: builds the latest stable release from source.
- `games-action/voidstrap-9999`: builds the latest code from the upstream `main` branch.

Use the versioned package for normal installations. The `9999` live ebuild is only for testing the latest development code and is **not required** to install Voidstrap.

Run these commands as root:

1. Add the overlay:

   ```bash
   emerge --ask app-eselect/eselect-repository dev-vcs/git
   eselect repository add Voidstrap-Overlay git https://github.com/Pedrodroks34/voidstrap-overlay.git
   emaint sync --repo Voidstrap-Overlay
   ```

2. Allow Voidstrap and its .NET SDK dependency:

   ```bash
   mkdir -p /etc/portage/package.accept_keywords
   echo 'games-action/voidstrap ~amd64' > /etc/portage/package.accept_keywords/voidstrap
   echo 'dev-dotnet/dotnet-sdk-bin ~amd64' > /etc/portage/package.accept_keywords/dotnet-sdk-bin
   ```

   If `/etc/portage/package.accept_keywords` is a file on your system, add both lines to that file instead.

3. Build and install Voidstrap:

   ```bash
   emerge --ask games-action/voidstrap
   ```

To update to a newer stable release, sync the overlay and run step 3 again:

```bash
emaint sync --repo Voidstrap-Overlay
emerge --ask games-action/voidstrap
```

#### Live version

The `games-action/voidstrap-9999` ebuild tracks the upstream `main` branch for development testing. It is **not needed for normal installations**.

To use the live version:

```bash
echo 'games-action/voidstrap **' > /etc/portage/package.accept_keywords/voidstrap
emerge --ask =games-action/voidstrap-9999
```

The live ebuild may contain unreleased changes and can require newer dependencies than the latest stable release.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/nixos-setup-dark.svg"><img src="assets/readme/nixos-setup-light.svg" alt="NixOS and Nix" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

Voidstrap includes a Nix flake for `x86_64-linux` and `aarch64-linux`. Nix builds it from source on your computer, so the first run takes a few minutes.

To try it without installing:

```bash
nix run "git+https://github.com/KloBraticc/Voidstrap?submodules=1"
```

This only runs it once. It does not add the `voidstrap` command or an app launcher entry, so follow the steps below to keep it installed.

To install it on NixOS, add the flake to your system flake and enable the module:

```nix
{
  inputs.voidstrap.url = "git+https://github.com/KloBraticc/Voidstrap?submodules=1";

  outputs = { nixpkgs, voidstrap, ... }: {
    nixosConfigurations.your-hostname = nixpkgs.lib.nixosSystem {
      modules = [
        voidstrap.nixosModules.default
        { programs.voidstrap.enable = true; }
      ];
    };
  };
}
```

`programs.voidstrap.enable` installs the `voidstrap` command and the app launcher entry, turns on `hardware.graphics` for the graphics drivers Voidstrap renders with, turns on `services.flatpak` and a desktop portal so Voidstrap can install and run Sober, and makes Voidstrap open `roblox://` links. Set `programs.voidstrap.openRobloxLinks = false` to leave link handling alone, or `programs.voidstrap.package` to use another build such as `voidstrap.packages.x86_64-linux.voidstrap-git`.

With Home Manager, import `voidstrap.homeModules.default` and set `programs.voidstrap.enable = true`. Home Manager cannot turn on system services, so Flatpak still has to be enabled with `services.flatpak.enable = true` in your NixOS configuration.

Without the modules, add `voidstrap.packages.${pkgs.stdenv.hostPlatform.system}.default` to `environment.systemPackages` (or `home.packages`) and enable `hardware.graphics` and `services.flatpak` yourself. The flake also provides `overlays.default`, which adds `pkgs.voidstrap`.

Use the `git+https` URL with `?submodules=1` rather than `github:KloBraticc/Voidstrap`. The build needs the bundled WPF UI submodule, and the `github:` scheme cannot fetch submodules, so `nix flake update` fails with `input attribute 'submodules' not supported by scheme 'github'`.

Adding the flake as an input is not enough on its own: enable the module or add the package before `voidstrap` works in a terminal or the app launcher. The flake installs the latest release. To update, run `nix flake update voidstrap` in your configuration and rebuild.

</details>

## FAQ

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/faq-banned-dark.svg"><img src="assets/readme/faq-banned-light.svg" alt="Can Voidstrap get me banned?" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

  Voidstrap does not inject cheats, exploit Roblox, or bypass its security. It manages launching and configuration.
</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/faq-virus-dark.svg"><img src="assets/readme/faq-virus-light.svg" alt="Is Voidstrap a virus?" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

  Voidstrap is open source, so anyone can review its code.
  Security software can flag unsigned or newly released applications, but do not assume every warning is a false positive. Download Voidstrap from its official releases and check the file's source before running it.

  You can review the complete source code [here](https://github.com/KloBraticc/Voidstrap).
</details>

## Building

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/build-setup-dark.svg"><img src="assets/readme/build-setup-light.svg" alt="Before building" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

| App | Build hosts | Targets |
| --- | --- | --- |
| Windows | Windows | x64 |
| Linux | Linux or Windows. macOS can cross publish the executable. | x64 and ARM64, glibc or musl |
| macOS | macOS, Windows or Linux. Windows disk-image packaging uses WSL 2. | Intel and Apple silicon |
| Android | Windows, Linux or macOS with the Android NDK host tools | ARM64, ARMv7 and x64 in each APK |

Clone with [Git](https://git-scm.com/downloads):

```bash
git clone --recurse-submodules https://github.com/KloBraticc/Voidstrap.git
cd Voidstrap
```

> [!TIP]
> On Windows, use a short path like `C:\src\Voidstrap` to avoid path length errors. One less thing to debug.

If you've already cloned the repository, run `git submodule update --init --recursive`. Desktop builds need `external/wpfui` and the bundled files in `external/LibreWPF`.

Desktop builds need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), version **10.0.300 or a newer stable 10.0 release**, as set in `global.json`. Check your version with `dotnet --version`. The runtime alone cannot build the app. The first build downloads NuGet packages and may take a while.

The Windows release installer is built from the Rust [VoidstrapInstaller](https://github.com/KloBraticc/VoidstrapInstaller) submodule. It offers .NET 10 by default and an optional .NET 11 RC3 build on supported CPUs. The runtime picker appears once, including after upgrading an existing installation, and keeps existing settings and files. The installer prepares .NET 10 as a fallback before starting .NET 11; a crash switches back to .NET 10 for the current and future launches. Building both Windows payloads also requires SDK `11.0.100-rc.3.26522.148`, pinned in `build/Runtime11/global.json`. Start the installed executable with `-runtime` to change the selection. macOS and Linux stay on .NET 10 and open directly without setup dialogs.

Run commands from the repository root unless noted otherwise. Linux and macOS need [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell) for `publish-all.ps1`. Windows supports both Windows PowerShell and PowerShell 7.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/build-windows-dark.svg"><img src="assets/readme/build-windows-light.svg" alt="Windows build" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

**You need:** Windows 10 or 11 (64 bit), the .NET SDK described above, [Rust](https://rustup.rs) with the MSVC toolchain, the Visual Studio C++ build tools and Windows SDK, and the initialized submodule. Rust builds the native libraries used by the application, including frame pacing, image handling, and package extraction.

```powershell
dotnet build Voidstrap.sln -c Release
```

The app is saved to `src\Voidstrap.App\bin\Release\net10.0-windows\win-x64\Voidstrap.exe`.

To publish a single `Voidstrap.exe`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only windows -NoPause
```

Output: `PublishedBuilds\Windows\Voidstrap.exe`. The native installer prepares the selected Microsoft Desktop Runtime.

**Code signing with SignPath (optional)**

If you have [SignPath](https://signpath.io/) set up for your repository, running `publish-all.ps1` with no arguments builds all release targets and signs the Release `Voidstrap.exe` through the trusted GitHub Actions build. If you do not use SignPath, use the `-Only windows` command above instead.

To build and sign with SignPath:

1. Add `SIGNPATH_API_TOKEN` as a repository Actions secret.
2. Install and sign in to [GitHub CLI](https://cli.github.com/) (`gh auth login`).
3. From a clean checkout of the latest `main` commit, run:

```powershell
.\publish-all.ps1
```

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/build-linux-dark.svg"><img src="assets/readme/build-linux-light.svg" alt="Linux build" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

**You need:** the .NET SDK, Bash, a C compiler and [Rust](https://rustup.rs) with Cargo. On Ubuntu or Debian:

```bash
sudo apt-get update
sudo apt-get install build-essential python3-venv
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
```

Rust builds `libvoidimage.so`, the image library Voidstrap needs to show any picture on Linux. A Linux build stops with an error if it cannot build it.

To publish an x64 executable on an x64 Linux host:

```bash
dotnet publish src/Voidstrap.Cross/Voidstrap.Cross.csproj \
  -c Release -r linux-x64 --self-contained true \
  -o PublishedBuilds/Linux-x64 \
  -p:EnableWindowsTargeting=true \
  -p:DebugType=none -p:DebugSymbols=false
```

Output: `PublishedBuilds/Linux-x64/Voidstrap`. The app includes .NET but needs compatible graphics and system libraries. Keep the published files together. On Linux or macOS, build this project only. The full solution requires Windows.

For other systems, change `linux-x64` (and the output folder) to:

* `linux-arm64` for ARM64
* `linux-musl-x64` or `linux-musl-arm64` for distros using musl, such as Alpine

Cross compiling on Linux needs a C compiler for the target architecture. On Ubuntu or Debian, use `gcc-aarch64-linux-gnu` for ARM64 or `gcc-x86-64-linux-gnu` for x64. Set `VOIDSTRAP_EDITOR_COMPILER` to use another compiler.

The image library uses `cargo-zigbuild` for musl targets, cross compiling and older glibc versions. The build installs it into `~/.cache/voidstrap/zigbuild` by itself when Python 3 with `venv` is available.

**Release packages (optional)**

To make `.tar.gz`, `.deb`, `.rpm`, `.AppImage` and `.flatpak` files, install [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/linux-overview) and the packaging tools. On Ubuntu or Debian:

```bash
sudo apt-get install build-essential curl ca-certificates binutils dpkg rpm flatpak flatpak-builder
```

Then run:

```bash
pwsh -NoProfile -File ./publish-all.ps1 -Only linux-x64 -LinuxPackages -NoPause
```

Output: `PublishedBuilds/Linux`. The first run downloads AppImage tools, supporting libraries, and the Flatpak runtime and SDK. Flatpak packaging needs a working Flatpak environment. ARM64 packaging on x64 may need emulation or an ARM64 host.

* To build only an AppImage, replace `-LinuxPackages` with `-AppImage`. Keep `curl`, `ca-certificates`, `binutils` and `dpkg`. RPM and Flatpak tools are unnecessary.
* Add `-SkipAppImage` to build the other packages without AppImage.
* Select `linux-arm64`, `linux-musl-x64` or `linux-musl-arm64` with `-Only` for those targets. Musl targets produce only a `.tar.gz`.
* Full packaging is attempted by default. `-LinuxPackages` fails if tools are missing. Otherwise, the script may fall back to a plain executable. Check the final notes.

**Building Linux releases from Windows**

Install WSL and the packaging tools above inside its Linux distribution, along with Rust, which builds the image library there. The build uses the WSL distribution named `Ubuntu`. Add `-p:VoidstrapWslDistro=<name>` to use another one. Install .NET on Windows, then run in Windows PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only linux-x64 -LinuxPackages -NoPause
```

The script publishes with Windows .NET and packages through WSL. Without WSL and its tools, omit `-LinuxPackages` to allow a plain executable in `PublishedBuilds\Linux`. Git Bash alone cannot create the full glibc Linux package set.

**Building with Nix**

With [Nix](https://nixos.org/download) and flakes enabled, build from the repository root:

```bash
nix build .#voidstrap-git
```

This builds the current checkout, while `.#voidstrap` builds the latest release. Output: `result/bin/voidstrap`. Run `nix develop` for a shell with the .NET SDK, Rust and every library the build needs. The NuGet packages are locked in `nix/deps.json`. After changing package references, update the lock with `nix build .#voidstrap.fetch-deps && ./result nix/deps.json`.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/build-macos-dark.svg"><img src="assets/readme/build-macos-light.svg" alt="macOS build" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

**You need:** the .NET SDK described above, PowerShell 7 and Bash. On macOS, install Rust with [rustup](https://rustup.rs) and the Xcode Command Line Tools with `xcode-select --install`. Packaging also builds and signs the Rust virtual display helper.

To build both macOS architectures on a Mac:

```bash
pwsh -NoProfile -File ./publish-all.ps1 -Only osx-arm64,osx-x64 -NoPause
```

This creates a `.dmg` and `.zip` for each architecture in `PublishedBuilds/macOS`: `Voidstrap-osx-arm64` for Apple silicon and `Voidstrap-osx-x64` for Intel. Each package contains `Voidstrap.app` with .NET included.

Local packages use ad hoc signing without notarization. For Developer ID signing, set `MACOS_SIGN_IDENTITY`. To notarize, also set `MACOS_NOTARY_PROFILE` to your notarytool keychain profile.

Windows can create macOS `.dmg` files through WSL 2. Install `genisoimage` in your WSL distribution, then run the PowerShell command with `-Only osx-arm64,osx-x64`. It produces a `.dmg` and `.tar.gz` for each architecture in `PublishedBuilds/macOS`. Linux can use the same command with `genisoimage` installed. These are uncompressed ISO 9660 disk images. Native macOS builds create compressed UDIF images. Git Bash alone can create the `.tar.gz` app bundles, but not `.dmg` files.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/build-android-dark.svg"><img src="assets/readme/build-android-light.svg" alt="Android build" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

**You need:**

* [JDK 17](https://adoptium.net/temurin/releases/?version=17)
* The [Android SDK](https://developer.android.com/studio) with SDK Platform 37, Build Tools, Platform Tools and **NDK 29.0.14206865**, plus accepted SDK licenses. Install these from the SDK Manager. The project compiles against API 37, targets API 36 and supports devices running API 24 or newer.
* A current stable [Rust toolchain](https://rustup.rs/) with Cargo and support for the Rust 2024 edition. The Gradle build compiles the Rust code automatically.

Use the included Gradle wrapper. Only `publish-all.ps1` requires .NET for Android builds, even with `-Only android`.

Set `JAVA_HOME` to your JDK 17 folder and `ANDROID_HOME` to your Android SDK folder. Gradle can also locate the SDK through `sdk.dir` in your local `android/local.properties`. For the publish script, set `ANDROID_HOME` explicitly, especially on macOS where the usual SDK folder is `~/Library/Android/sdk`. Then add the Rust targets (only needed once):

```bash
rustup target add aarch64-linux-android armv7-linux-androideabi x86_64-linux-android
```

Build the debug APKs:

```bash
cd android
./gradlew assembleDebug
```

On Windows, use `.\gradlew.bat assembleDebug` instead. If the wrapper is not executable on Linux or macOS, use `bash ./gradlew assembleDebug`. This produces:

| Flavor | Debug APK, relative to the repository root | Behavior |
| --- | --- | --- |
| `play` | `android/app/build/outputs/apk/play/debug/app-play-debug.apk` | Google Play channel, without the direct updater or server matchmaker |
| `direct` | `android/app/build/outputs/apk/direct/debug/app-direct-debug.apk` | GitHub channel, with the direct updater and server matchmaker |

Each APK includes ARM64, ARMv7 and x64 libraries. Debug APKs cannot replace releases signed with a different certificate.

**Signed release APKs**

Release APKs need a keystore. Create one with `keytool`, using your own path and passwords:

```bash
keytool -genkeypair -keystore /path/to/voidstrap-release.jks -alias voidstrap -keyalg RSA -keysize 2048 -validity 10000
```

Keep the keystore outside the repository. Add these properties to your user Gradle file, `~/.gradle/gradle.properties` (`%USERPROFILE%\.gradle\gradle.properties` on Windows), or `$GRADLE_USER_HOME/gradle.properties` if you override that directory:

```properties
voidstrap.storeFile=/path/to/your.keystore
voidstrap.storePassword=your_store_password
voidstrap.keyAlias=your_key_alias
voidstrap.keyPassword=your_key_password
```

Use an absolute keystore path. On Windows, use forward slashes, as in `C:/keys/voidstrap-release.jks`. Keep the same signing key for future APK updates.

From the `android` directory, build both signed releases directly:

```bash
./gradlew assemblePlayRelease assembleDirectRelease
```

On Windows, use `.\gradlew.bat assemblePlayRelease assembleDirectRelease`. Signed APKs are written to `android/app/build/outputs/apk/play/release/app-play-release.apk` and `android/app/build/outputs/apk/direct/release/app-direct-release.apk`. Without all four signing properties, Gradle produces unsigned release APKs.

To build, verify and copy both release APKs, install the .NET SDK and run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only android -NoPause
```

On Linux or macOS, use `pwsh -NoProfile -File ./publish-all.ps1 -Only android -NoPause`. The signed APKs are saved as `PublishedBuilds/Android/Voidstrap-Android-play-<version>.apk` and `Voidstrap-Android-direct-<version>.apk`, using the version in `Directory.Build.props`. The script rejects unsigned APKs and APKs signed with the debug certificate.

</details>

## MCP

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/mcp-setup-dark.svg"><img src="assets/readme/mcp-setup-light.svg" alt="MCP setup and reference" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

MCP lets a compatible client manage Voidstrap settings, themes, mods, and diagnostics. **Windows only.** Keep Voidstrap open while connected.

1. Open the connection dialog from the app menu or the plug button in an editor.
2. Select your client and click **Connect**. For another client, copy the configuration into its MCP settings.
3. Restart the client and ask it to call `get_voidstrap_overview` to check the connection.

For manual setup, use this configuration:

```json
{
  "mcpServers": {
    "voidstrap": {
      "command": "C:\\path\\to\\Voidstrap.exe",
      "args": ["mcp", "stdio"]
    }
  }
}
```

Replace the example path with your installed executable and merge the entry into your existing configuration. The client must support local `stdio` servers.

For detailed instructions, ask the client to call `get_guide` with `{"topic":"workflow"}`. It explains the tools and links to the theme, mod, and troubleshooting guides.

Connected clients can change app settings and files. Review their actions and privacy settings before sharing logs. To disconnect, disable or remove the `voidstrap` server in the client.

</details>

## Inspiration & Credits

Voidstrap would not exist without the projects and people below. Thank you all.

### Inspiration

- **[Froststrap](https://github.com/Froststrap)**, the inspiration for the title bar and many UI elements, the mod generator, the virtual display, and some of our custom cursor features.
- **[Chevstrap](https://github.com/FrosSky/Chevstrap)**, the inspiration for the Android app, with attributed Android adaptations.
- **[Helium](https://github.com/imputnet/helium)**, the inspiration for the title bar UI.
- **[Claude Code](https://claude.com/claude-code)** by Anthropic, the inspiration for the title bar UI.
- **[Codex](https://github.com/openai/codex)** by OpenAI, the inspiration for the title bar UI.

### Built on

- **[Bloxstrap](https://github.com/bloxstraplabs/bloxstrap)** by pizzaboxer, the base Voidstrap is built on.
- **[Fishstrap](https://github.com/fishstrap/fishstrap)** by returnrqt, whose license is preserved below.
- **[WPF UI](https://github.com/lepoco/wpfui)** by lepoco, the UI library behind every Voidstrap window (used through our own fork).

### RiShade

- **[Unishade](https://github.com/OMouta/Unishade)** by OMouta, the base of RiShade's in-process ReShade architecture (huge credit to him for his work in C++).
- **[ReShade](https://github.com/crosire/reshade)** by crosire, the shader runtime RiShade runs.
- **[Depth Anything V2](https://github.com/DepthAnything/Depth-Anything-V2)**, the model RiDepth is fine tuned from.
- **[RenoDX](https://github.com/clshortfuse/renodx)**, whose add-on powers NVIDIA DLSS in RiShade.

### Platforms and features

- **[Sober](https://sober.vinegarhq.org)** and **[Vinegar](https://github.com/vinegarhq/vinegar)** by VinegarHQ, which run Roblox and Roblox Studio on Linux.
- **[Bibata Cursor](https://github.com/ful1e5/Bibata_Cursor)** by ful1e5, used for custom cursors.

## License

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/project-licenses-dark.svg"><img src="assets/readme/project-licenses-light.svg" alt="Project licenses" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

Voidstrap's own code is released under the [MIT License](LICENSE.VOIDSTRAP). Third party code, fonts, artwork and packages keep their own licenses. The Bloxstrap and Fishstrap notices are also preserved:

| Project | License | Copyright | File |
| --- | --- | --- | --- |
| Voidstrap | MIT | © 2026 Bratic | [LICENSE.VOIDSTRAP](LICENSE.VOIDSTRAP) |
| Bloxstrap | MIT | © 2022 pizzaboxer | [LICENSE.BLOXSTRAP](LICENSE.BLOXSTRAP) |
| Fishstrap | MIT | © 2025 returnrqt | [LICENSE.FISHSTRAP](LICENSE.FISHSTRAP) |

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/third-party-licenses-dark.svg"><img src="assets/readme/third-party-licenses-light.svg" alt="Third party licenses" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

Components included in this repository keep their own licenses:

| Component | Used in | License | File |
| --- | --- | --- | --- |
| [WPF UI](https://github.com/lepoco/wpfui) | Desktop apps | MIT | [License](external/wpfui/LICENSE) |
| WPF UI docs template by Singulink | WPF UI docs | MIT | [License](external/wpfui/docs/templates/LICENSE) |
| Fluent System Icons by Microsoft | Desktop apps | MIT | [License](external/wpfui/src/Wpf.Ui/License%20-%20Fluent%20System%20Icons.txt) |
| VirtualizingWrapPanel | WPF UI | MIT | [License](external/wpfui/src/Wpf.Ui/License%20-%20VirtualizingWrapPanel.txt) |
| Fira Code font | WPF UI font assets | SIL Open Font License 1.1 | [License](external/wpfui/src/Wpf.Ui/License%20-%20Fira%20Code.txt) |
| Inter font | Repository font assets | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Inter.txt) |
| Selawik font by Microsoft | Repository font assets | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Selawik.txt) |
| Montserrat font | Repository font assets | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Montserrat.txt) |
| Noto Sans Thai font | Desktop apps | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-NotoSansThai.txt) |
| Rubik font | Desktop apps | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Rubik.txt) |
| Voidstrap Emoji font, adapted from [Twemoji Mozilla](https://github.com/mozilla/twemoji-colr/tree/v0.7.0) 0.7.0 using `build/Packaging/Linux/build-emoji-font.py`, art by [Twemoji](https://github.com/twitter/twemoji) | Linux app | CC BY 4.0 artwork, Apache 2.0 upstream tooling | [License](src/Voidstrap.App/Resources/Fonts/LICENSE-TwemojiMozilla.txt) |
| [Bibata Modern Ice cursors](https://github.com/ful1e5/Bibata_Cursor/tree/v2.0.7) | Desktop and Android apps | GPL version 3 | [Desktop license](src/Voidstrap.App/Resources/Mods/Cursor/BibataModernIce/LICENSE.txt), [Android license](android/app/src/main/assets/mods/Cursor/BibataModernIce/LICENSE.txt) |
| ppmd-rust | Android app | CC0 1.0 or MIT No Attribution | [CC0](android/rust/vendor/ppmd-rust/LICENSE-CC0.md), [MIT-0](android/rust/vendor/ppmd-rust/LICENSE-MIT-0.md) |
| GLFW | Linux runtime | zlib/libpng license | [License](external/LibreWPF/GLFW-LICENSE.txt) |
| [Devicon](https://github.com/devicons/devicon) platform icons, copyright 2015 konpa | README | MIT | [License](assets/readme/LICENSE-Devicon.txt) |

The Windows and macOS README icons adapt Devicon artwork with background tiles, spacing and color changes. Brand names and logos remain the property of their owners. See [Devicon's brand guidance](https://github.com/devicons/devicon/wiki/Legal).

Segoe Fluent Icons is not bundled. Its [upstream license](external/wpfui/src/Wpf.Ui/License%20-%20Segoe%20Fluent%20Icons.txt) is retained as a reference and does not grant redistribution rights.

### Dependency versions and licenses

The build manifests are the source of truth for dependency versions. Use the files for the component you are changing rather than a copied version table:

| Component | Version references |
| --- | --- |
| Windows desktop application | [Application project](src/Voidstrap.App/Voidstrap.csproj) |
| Shared Linux and macOS application | [Cross platform project](src/Voidstrap.Cross/Voidstrap.Cross.csproj) |
| Desktop hosts and platform libraries | Project files under [src](src) |
| .NET SDK and cross platform SDK | [global.json](global.json) |
| Android libraries and build tools | [Version catalog](android/gradle/libs.versions.toml), [Gradle wrapper](android/gradle/wrapper/gradle-wrapper.properties), and [app build](android/app/build.gradle.kts) |
| Native package extraction | [Manifest](native/voidpkg/Cargo.toml) and [lockfile](native/voidpkg/Cargo.lock) |
| RiShade and its add-on | [Workspace manifest](native/rishade/Cargo.toml) and [lockfile](native/rishade/Cargo.lock) |
| Other native desktop libraries | Manifests and lockfiles under [native](native) |
| Android native helper | [Manifest](android/rust/Cargo.toml) and [lockfile](android/rust/Cargo.lock) |
| macOS virtual display helper | [Manifest](src/VirtualDisplay/Cargo.toml) and [lockfile](src/VirtualDisplay/Cargo.lock) |

A manifest can allow a version range; a Cargo lockfile records the resolved versions used by a locked build. Check restored NuGet metadata and resolved Maven dependencies when auditing a particular build.

The [license inventory](legal/components.json) records publisher terms and notice origins, and [legal/texts](legal/texts) retains license texts. Preserve the required notices when redistributing packages. A dependency does not inherit Voidstrap's MIT license simply because it is used by this project.

The [LibreWPF SDK](https://www.nuget.org/packages/LibreWPF.Sdk/) uses MIT. Cross platform builds also include runtime dependency notices under LibreWPF/Notices; preserve their individual terms.

Full notices, including transitive dependencies and native libraries, are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Release packages include these notices and the [Bibata cursor source](legal/sources/Bibata-Cursor-v2.0.7.tar.gz). See [legal/README.md](legal/README.md) for how to update and check them.

Dependencies brought in by these packages keep their own license terms. The links above identify version sources; they are not a complete license audit of every transitive package or system component. Preserve all required notices and source when redistributing them. A README entry does not replace those requirements.

The macOS virtual display helper implements the [AppleBlox VirtualDisplay approach](https://github.com/AppleBlox/virtualdisplay) in Rust. Its [bundled notices](src/VirtualDisplay/THIRD-PARTY-NOTICES.txt) cover libc, objc2 and objc2-encode.

</details>

---

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/KloBraticc/Voidstrap/output/github-contribution-grid-snake-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/KloBraticc/Voidstrap/output/github-contribution-grid-snake.svg">
    <img src="https://raw.githubusercontent.com/KloBraticc/Voidstrap/output/github-contribution-grid-snake.svg" alt="GitHub contribution graph">
  </picture>
</p>

<p align="center">
  <a href="https://discord.gg/5tJBqBH8ck">
    <img src="https://discord.com/api/guilds/1327967202015580223/widget.png?style=banner2" alt="Join the Voidstrap Discord Bro">
  </a>
</p>

[shield-repo-downloads]:  https://img.shields.io/github/downloads/KloBraticc/Voidstrap/latest/total?color=981bfe
[shield-repo-total]:      https://img.shields.io/github/downloads/KloBraticc/Voidstrap/total?color=8a2be2
[shield-repo-latest]:     https://img.shields.io/github/v/release/KloBraticc/Voidstrap?color=7a39fb
[shield-repo-stars]:      https://img.shields.io/github/stars/KloBraticc/Voidstrap?color=ffd700
[shield-discord-server]:  https://img.shields.io/discord/1327967202015580223?logo=discord&logoColor=white&label=Discord&color=4d3dff
[shield-crowdin-localized]: https://badges.crowdin.net/voidstrap/localized.svg
[shield-actions-build]:   https://img.shields.io/github/actions/workflow/status/KloBraticc/Voidstrap/desktop.yml?branch=main&label=builds

[repo-releases]:          https://github.com/KloBraticc/Voidstrap/releases
[repo-latest]:            https://github.com/KloBraticc/Voidstrap/releases/latest
[repo-stargazers]:        https://github.com/KloBraticc/Voidstrap/stargazers
[repo-actions]:           https://github.com/KloBraticc/Voidstrap/actions/workflows/desktop.yml
[crowdin-project]:        https://crowdin.com/project/voidstrap
[discord-invite]:         https://discord.gg/dfA9PdWgcV




