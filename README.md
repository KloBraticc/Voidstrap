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

</div>

<h5 align="center">
  Leave a star if you like the project! ⭐️
</h5>

<p align="center">
  <picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/windows-tile-dark.svg"><img src="assets/readme/windows-tile-light.svg" alt="Windows" width="32" height="32"/></picture>
    
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/android/android-original.svg" alt="Android" width="32" height="32"/>
    
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/linux/linux-original.svg" alt="Linux" width="32" height="32"/>
    
  <picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/macos-dark.svg"><img src="assets/readme/macos-light.svg" alt="macOS" width="32" height="32"/></picture>
</p>

**Windows** (PowerShell)

```powershell
irm https://voidstrapp.pages.dev/quick-install | iex
```

**Linux**

```bash
curl -fsSL https://voidstrapp.pages.dev/install.sh | sh
```

Linux picks the right package for your distro. To choose one yourself, end the command with `sh -s -- deb` and swap `deb` for `rpm`, `aur`, `flatpak`, `appimage`, `portable` or `portable-musl`.

---

> [!IMPORTANT]
> Voidstrap currently supports **Windows 10 and above**, **Android**, and **Linux**.  
> **macOS support is currently in development** and will be available in a future release.
>
> If you're looking for a MacOS Bootstrapper in the meantime:
> - **macOS:** [AppleBlox](https://github.com/AppleBlox/appleblox)

> [!WARNING]
> Voidstrap is not an exploit and never will be. We are not considered an exploit. We are here to give users more freedom, features, and support for Roblox.
>
> As of Voidstrap Version `1.1.2.3`, Multi-Instance Launching has been removed from the app and will not be added back in the future.

## FAQ

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/faq-banned-dark.svg"><img src="assets/readme/faq-banned-light.svg" alt="Can Voidstrap get me banned?" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

  Voidstrap does not inject cheats, exploit Roblox, or bypass Roblox security. It functions as a launcher and configuration manager.
</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/faq-virus-dark.svg"><img src="assets/readme/faq-virus-light.svg" alt="Is Voidstrap a virus?" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

  Voidstrap is fully open source, allowing anyone to inspect and review its source code.
  If your antivirus flags Voidstrap, it's a false positive caused by how Windows detects and handles unsigned applications.
  
  You can review the complete source code [here](https://github.com/KloBraticc/Voidstrap).
</details>

## Installation

1. Download the [latest version](https://github.com/KloBraticc/Voidstrap/releases/latest).
2. Run the `.exe` and finish the setup.
3. Launch Voidstrap.
4. Enjoy a simpler Roblox!

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-setup-dark.svg"><img src="assets/readme/android-setup-light.svg" alt="Android setup" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

Voidstrap for Android needs Android 7 or newer. Download `Voidstrap-Android-direct-<version>.apk` from the [latest release](https://github.com/KloBraticc/Voidstrap/releases/latest) and install it.

Android doesn't let apps change Roblox's files by themselves, so Voidstrap needs extra access to apply your FastFlags. There are three ways to give it that access. You can start any of them from **Settings > Voidstrap helper** in the app, which also shows your exact command with a copy button.

| Method | Needs a computer | After your phone restarts |
| --- | --- | --- |
| Shizuku (easiest) | No | Start Shizuku again, and Voidstrap takes care of the rest |
| ADB | Yes | Run the start command again |
| Root | No | Nothing, it keeps working |

Mods only work with root.

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-shizuku-dark.svg"><img src="assets/readme/android-shizuku-light.svg" alt="Shizuku" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

1. Install [Shizuku](https://shizuku.rikka.app/) and start it by following its own guide.
2. In Voidstrap, open **Settings > Voidstrap helper** and tap **Start with Shizuku**.
3. Allow Voidstrap when Shizuku asks.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-adb-usb-dark.svg"><img src="assets/readme/android-adb-usb-light.svg" alt="ADB with a USB cable" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

1. Turn on Developer options: open **Settings > About phone** and tap **Build number** seven times. On some phones, Build number is inside **Software information**, or is called **OS version** instead.
2. Open **Developer options** (usually under **Settings > System**) and turn on **USB debugging**.
3. On your computer, download Google's [SDK Platform Tools](https://developer.android.com/tools/releases/platform-tools) and unzip it.
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

In PowerShell, type `.\adb` instead of `adb`. If `adb devices` shows nothing, try another cable, since some cables can only charge.

</details>

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/android-adb-wifi-dark.svg"><img src="assets/readme/android-adb-wifi-light.svg" alt="ADB over WiFi (Android 11 or newer)" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

1. Follow steps 1 to 4 of the USB cable guide.
2. Connect your phone and your computer to the same WiFi network.
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

### Gentoo Linux

Voidstrap is packaged for Gentoo in the [Voidstrap overlay](https://github.com/Pedrodroks34/voidstrap-overlay), maintained by [@Pedrodroks34](https://github.com/Pedrodroks34).

The overlay provides two ebuilds:

- `games-action/voidstrap`: builds the latest stable release from source.
- `games-action/voidstrap-9999`: builds the latest code from the upstream `main` branch.

The versioned package is recommended for normal installations. The `9999` live ebuild is only needed if you want to test the latest development code. It is **not required** for installing Voidstrap.

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

The `games-action/voidstrap-9999` ebuild tracks the upstream `main` branch. It is intended for testing the latest development changes and is **not necessary for normal installation**.

To use the live version:

```bash
echo 'games-action/voidstrap **' > /etc/portage/package.accept_keywords/voidstrap
emerge --ask =games-action/voidstrap-9999
```

The live ebuild may contain unreleased changes and can require newer dependencies than the latest stable release.

## Built With

[![C#][shield-csharp]][link-csharp] [![.NET][shield-dotnet]][link-dotnet] [![WPF][shield-wpf]][link-wpf] [![WPF UI][shield-wpfui]][link-wpfui] [![WebView2][shield-webview2]][link-webview2]

[![Java][shield-java]][link-java] [![Rust][shield-rust]][link-rust] [![Android][shield-android]][link-android]

## Building

| App | Build hosts | Targets |
| --- | --- | --- |
| Windows | Windows | x64 |
| Linux | Linux or Windows. macOS can cross publish the executable. | x64 and ARM64, glibc or musl |
| macOS | macOS, Windows or Linux. Disk images require macOS. | Intel and Apple silicon |
| Android | Windows, Linux or macOS with the Android NDK host tools | ARM64, ARMv7 and x64 in each APK |

Clone with [Git](https://git-scm.com/downloads):

```bash
git clone --recurse-submodules https://github.com/KloBraticc/Voidstrap.git
cd Voidstrap
```

> [!TIP]
> On Windows, use a short path like `C:\src\Voidstrap` to avoid path length errors. One less thing to debug.

Already cloned? Run `git submodule update --init --recursive`. Desktop builds need `external/wpfui` and the bundled files in `external/LibreWPF`.

Desktop builds need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), **10.0.300 or a newer stable 10.0 release**, as set in `global.json`. Check with `dotnet --version`. The runtime alone cannot build the app. The first build needs internet access for NuGet packages and may take a while.

Run commands from the repository root unless shown otherwise. Linux and macOS need [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell) for `publish-all.ps1`. Windows supports both Windows PowerShell and PowerShell 7.

### Windows app

**You need:** Windows 10 or 11 (64 bit), the .NET SDK described above, and the initialized submodule.

```powershell
dotnet build Voidstrap.sln -c Release
```

The app is saved to `src\Voidstrap.App\bin\Release\net10.0-windows\win-x64\Voidstrap.exe`.

To publish a single `Voidstrap.exe`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only windows -NoPause
```

Output: `PublishedBuilds\Windows\Voidstrap.exe`. Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

### Linux app

**You need:** the .NET SDK, Bash and a C compiler. On Ubuntu or Debian:

```bash
sudo apt-get update
sudo apt-get install build-essential
```

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

**Release packages (optional)**

To make `.tar.gz`, `.deb`, `.rpm`, `.AppImage` and `.flatpak` files, install [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/linux-overview) and the packaging tools. On Ubuntu or Debian:

```bash
sudo apt-get install build-essential curl ca-certificates binutils dpkg rpm flatpak flatpak-builder
```

Then run:

```bash
pwsh -NoProfile -File ./publish-all.ps1 -Only linux-x64 -LinuxPackages -NoPause
```

Output: `PublishedBuilds/Linux`. The first run downloads AppImage tools, supporting libraries, and the Flatpak runtime and SDK. Yes, more downloads. Flatpak needs a working environment. ARM64 packaging on x64 may need emulation or an ARM64 host.

* To build only an AppImage, replace `-LinuxPackages` with `-AppImage`. Keep `curl`, `ca-certificates`, `binutils` and `dpkg`. RPM and Flatpak tools are unnecessary.
* Add `-SkipAppImage` to build the other packages without AppImage.
* Select `linux-arm64`, `linux-musl-x64` or `linux-musl-arm64` with `-Only` for those targets. Musl targets produce only a `.tar.gz`.
* Full packaging is attempted by default. `-LinuxPackages` fails if tools are missing. Otherwise, the script may fall back to a plain executable. Check the final notes.

**Building Linux releases from Windows**

Install WSL and the packaging tools above inside its Linux distribution. Install .NET on Windows, then run in Windows PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only linux-x64 -LinuxPackages -NoPause
```

The script publishes with Windows .NET and packages through WSL. Without WSL and its tools, omit `-LinuxPackages` to allow a plain executable in `PublishedBuilds\Linux`. Git Bash alone cannot create the full glibc Linux package set.

### macOS app

**You need:** the .NET SDK described above, PowerShell 7 and Bash. On macOS, install the Xcode Command Line Tools with `xcode-select --install` for the signing and packaging tools.

On an Apple silicon Mac:

```bash
pwsh -NoProfile -File ./publish-all.ps1 -Only osx-arm64 -NoPause
```

For an Intel Mac, use `-Only osx-x64`. On macOS, the output is `PublishedBuilds/macOS/Voidstrap-osx-arm64.dmg` and `Voidstrap-osx-arm64.zip` (or `osx-x64` for Intel). Each contains `Voidstrap.app` with .NET included.

Local packages use ad hoc signing without notarization. For Developer ID signing, set `MACOS_SIGN_IDENTITY`. To notarize, also set `MACOS_NOTARY_PROFILE` to your notarytool keychain profile.

Windows and Linux can cross publish macOS targets. On Windows, use the PowerShell command with `-Only osx-arm64` or `-Only osx-x64`. Packaging needs Git Bash or WSL. These hosts produce `Voidstrap-osx-arm64.tar.gz` or `Voidstrap-osx-x64.tar.gz` app bundles. If packaging fails, output may be a plain executable. Check the final notes and test on a Mac.

### Android app

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

Release APKs need your own keystore. Create one with `keytool`, using your own path and passwords:

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

Use an absolute keystore path. On Windows, use forward slashes such as `C:/keys/voidstrap-release.jks`. Keep the same signing key for later updates to your own APKs.

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

## Forking

To create your own copy of Voidstrap:

1. Open the [Voidstrap repository](https://github.com/KloBraticc/Voidstrap).
2. Click **Fork**.
3. Select your GitHub account.

## Credits

Voidstrap is built on [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap) by pizzaboxer.

## License

[![License][shield-license]][link-license]

Voidstrap is released under the [MIT License](LICENSE.VOIDSTRAP). It is built on Bloxstrap and Fishstrap, and their licenses are kept alongside it:

| Project | License | Copyright | File |
| --- | --- | --- | --- |
| Voidstrap | MIT | © 2026 Bratic | [LICENSE.VOIDSTRAP](LICENSE.VOIDSTRAP) |
| Bloxstrap | MIT | © 2022 pizzaboxer | [LICENSE.BLOXSTRAP](LICENSE.BLOXSTRAP) |
| Fishstrap | MIT | © 2025 returnrqt | [LICENSE.FISHSTRAP](LICENSE.FISHSTRAP) |

<details>
  <summary><picture><source media="(prefers-color-scheme: dark)" srcset="assets/readme/third-party-licenses-dark.svg"><img src="assets/readme/third-party-licenses-light.svg" alt="Third party licenses" width="82%" height="40" align="left"></picture><picture><img src="assets/readme/spacer.svg" alt="" width="1" height="40" align="middle"></picture></summary>

Components included in this repository keep their own licenses:

| Component | Used in | License | File |
| --- | --- | --- | --- |
| [WPF UI](https://github.com/lepoco/wpfui) | Windows app | MIT | [License](external/wpfui/LICENSE) |
| WPF UI docs template by Singulink | WPF UI docs | MIT | [License](external/wpfui/docs/templates/LICENSE) |
| Fluent System Icons by Microsoft | Windows app | MIT | [License](external/wpfui/src/Wpf.Ui/License%20-%20Fluent%20System%20Icons.txt) |
| Segoe Fluent Icons by Microsoft | Windows app | Microsoft font license | [License](external/wpfui/src/Wpf.Ui/License%20-%20Segoe%20Fluent%20Icons.txt) |
| VirtualizingWrapPanel | Windows app | MIT | [License](external/wpfui/src/Wpf.Ui/License%20-%20VirtualizingWrapPanel.txt) |
| Fira Code font | Windows app | SIL Open Font License 1.1 | [License](external/wpfui/src/Wpf.Ui/License%20-%20Fira%20Code.txt) |
| Inter font | Windows app | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Inter.txt) |
| Selawik font by Microsoft | Windows app | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Selawik.txt) |
| Montserrat font | Windows app | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Montserrat.txt) |
| Noto Sans Thai font | Windows app | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-NotoSansThai.txt) |
| Rubik font | Windows app | SIL Open Font License 1.1 | [License](src/Voidstrap.App/Resources/Fonts/OFL-Rubik.txt) |
| Voidstrap Emoji font, built from [Twemoji Mozilla](https://github.com/mozilla/twemoji-colr) 0.7.0 by `build/Packaging/Linux/build-emoji-font.py`, art by [Twemoji](https://github.com/twitter/twemoji) | Linux app | CC BY 4.0 art, Apache 2.0 build code | [License](src/Voidstrap.App/Resources/Fonts/LICENSE-TwemojiMozilla.txt) |
| [Bibata Modern Ice cursors](https://github.com/ful1e5/Bibata_Cursor) | Windows and Android apps | GPL 3.0 | [License](src/Voidstrap.App/Resources/Mods/Cursor/BibataModernIce/LICENSE.txt) |
| ppmd-rust | Android app | CC0 1.0 or MIT No Attribution | [CC0](android/rust/vendor/ppmd-rust/LICENSE-CC0.md), [MIT-0](android/rust/vendor/ppmd-rust/LICENSE-MIT-0.md) |

</details>

---

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/KloBraticc/Voidstrap/output/star-chart-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/KloBraticc/Voidstrap/output/star-chart-light.svg">
    <img src="https://raw.githubusercontent.com/KloBraticc/Voidstrap/output/star-chart-light.svg" alt="Voidstrap star history" width="100%">
  </picture>
</p>

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

[repo-releases]:          https://github.com/KloBraticc/Voidstrap/releases
[repo-latest]:            https://github.com/KloBraticc/Voidstrap/releases/latest
[repo-stargazers]:        https://github.com/KloBraticc/Voidstrap/stargazers
[discord-invite]:         https://discord.gg/dfA9PdWgcV

[shield-license]:         https://img.shields.io/github/license/KloBraticc/Voidstrap?style=for-the-badge
[link-license]:           LICENSE.VOIDSTRAP

[shield-csharp]:          https://img.shields.io/badge/C%23-239120?style=for-the-badge
[shield-dotnet]:          https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white
[shield-wpf]:             https://img.shields.io/badge/WPF-0078D4?style=for-the-badge
[shield-wpfui]:           https://img.shields.io/badge/WPF%20UI-24292F?style=for-the-badge
[shield-webview2]:        https://img.shields.io/badge/WebView2-0C59A4?style=for-the-badge
[shield-java]:            https://img.shields.io/badge/Java-ED8B00?style=for-the-badge&logo=openjdk&logoColor=white
[shield-rust]:            https://img.shields.io/badge/Rust-000000?style=for-the-badge&logo=rust&logoColor=white
[shield-android]:         https://img.shields.io/badge/Android-3DDC84?style=for-the-badge&logo=android&logoColor=white

[link-csharp]:            https://learn.microsoft.com/dotnet/csharp/
[link-dotnet]:            https://dotnet.microsoft.com/
[link-wpf]:               https://github.com/dotnet/wpf
[link-wpfui]:             https://github.com/lepoco/wpfui
[link-webview2]:          https://developer.microsoft.com/microsoft-edge/webview2/
[link-java]:              https://openjdk.org/
[link-rust]:              https://www.rust-lang.org/
[link-android]:           https://developer.android.com/
