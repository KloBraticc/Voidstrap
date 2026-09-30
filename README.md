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
  <a href="https://voidstrapp.pages.dev/pages/documentation">Documentation</a> |
  <a href="https://discord.gg/5tJBqBH8ck">Discord</a>
</p>

<div align="center">

[![Total Downloads][shield-repo-total]][repo-releases]
[![Latest Downloads][shield-repo-downloads]][repo-latest]
[![Latest Release][shield-repo-latest]][repo-latest]
[![Discord][shield-discord-server]][discord-invite]
[![Stars][shield-repo-stars]][repo-stargazers]
[![Sponsors][shield-repo-sponsors]][sponsor-link]

</div>

<h5 align="center">
  Leave a star if you like the project! ⭐️
</h5>

<p align="center">
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/windows8/windows8-original.svg" alt="Windows" width="32" height="32"/>
  &nbsp;&nbsp;
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/android/android-original.svg" alt="Android" width="32" height="32"/>
  &nbsp;&nbsp;
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/linux/linux-original.svg" alt="Linux" width="32" height="32"/>
  &nbsp;&nbsp;

  <!-- macOS
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/apple/apple-original.svg" alt="macOS" width="32" height="32"/>
  -->
</p>

## Quick Install

```powershell
irm https://voidstrapp.pages.dev/quick-install | iex
```

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
  <summary><strong>Can Voidstrap get me banned?</strong></summary>

  Voidstrap does not inject cheats, exploit Roblox, or bypass Roblox security. It functions as a launcher and configuration manager.
</details>

<details>
  <summary><strong>Is Voidstrap a virus?</strong></summary>

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
  <summary><strong>Android setup</strong></summary>

Voidstrap for Android needs Android 7 or newer. Download `Voidstrap-Android-direct-<version>.apk` from the [latest release](https://github.com/KloBraticc/Voidstrap/releases/latest) and install it.

Android doesn't let apps change Roblox's files by themselves, so Voidstrap needs extra access to apply your FastFlags. There are three ways to give it that access. You can start any of them from **Settings > Voidstrap helper** in the app, which also shows your exact command with a copy button.

| Method | Needs a computer | After your phone restarts |
| --- | --- | --- |
| Shizuku (easiest) | No | Start Shizuku again, and Voidstrap takes care of the rest |
| ADB | Yes | Run the start command again |
| Root | No | Nothing, it keeps working |

Mods only work with root.

<details>
  <summary><strong>Shizuku</strong></summary>

1. Install [Shizuku](https://shizuku.rikka.app/) and start it by following its own guide.
2. In Voidstrap, open **Settings > Voidstrap helper** and tap **Start with Shizuku**.
3. Allow Voidstrap when Shizuku asks.

</details>

<details>
  <summary><strong>ADB with a USB cable</strong></summary>

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
  <summary><strong>ADB over WiFi (Android 11 or newer)</strong></summary>

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
  <summary><strong>Root</strong></summary>

In Voidstrap, open **Settings**, turn on **Use root**, and allow Voidstrap in your root manager.

</details>

</details>

### Gentoo Linux

Voidstrap is packaged for Gentoo in the [Voidstrap overlay](https://github.com/Pedrodroks34/voidstrap-overlay), maintained by [@Pedrodroks34](https://github.com/Pedrodroks34). It builds the latest code from the `main` branch and needs an amd64 system on a Gentoo desktop profile. Run these commands as root:

1. Add the overlay:

   ```bash
   emerge --ask app-eselect/eselect-repository dev-vcs/git
   eselect repository add Voidstrap-Overlay git https://github.com/Pedrodroks34/voidstrap-overlay.git
   emaint sync --repo Voidstrap-Overlay
   ```

2. Allow the live package and the .NET SDK it is built with:

   ```bash
   mkdir -p /etc/portage/package.accept_keywords
   printf '%s\n' 'games-action/voidstrap **' 'dev-dotnet/dotnet-sdk-bin ~amd64' > /etc/portage/package.accept_keywords/voidstrap
   ```

   If `/etc/portage/package.accept_keywords` is a file on your system, add those two lines to it instead.

3. Build and install Voidstrap:

   ```bash
   emerge --ask games-action/voidstrap
   ```

To update to the latest code, run step 3 again.

## Built With

[![C#][shield-csharp]][link-csharp] [![.NET][shield-dotnet]][link-dotnet] [![WPF][shield-wpf]][link-wpf] [![WPF UI][shield-wpfui]][link-wpfui] [![WebView2][shield-webview2]][link-webview2]

[![Java][shield-java]][link-java] [![Rust][shield-rust]][link-rust] [![Android][shield-android]][link-android]

## Building

| App | Build it on |
| --- | --- |
| Windows | Windows |
| Linux | Linux or Windows |
| Android | Windows or Linux |

Start by cloning the repository with [Git](https://git-scm.com/downloads):

```bash
git clone https://github.com/KloBraticc/Voidstrap.git
cd Voidstrap
```

> [!TIP]
> On Windows, clone into a short folder like `C:\src\Voidstrap`. Windows limits file paths to 260 characters, so a deeply nested folder can make the clone or the build fail.

### Windows app

**You need:** Windows 10 or 11 (64 bit) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), version 10.0.300 or a newer 10.0 release.

```powershell
dotnet build Voidstrap.sln -c Release
```

The app is saved to `src\Voidstrap.App\bin\Release\net10.0-windows\win-x64\Voidstrap.exe`.

To build the single `Voidstrap.exe` file that releases use:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only windows
```

It is saved to `PublishedBuilds\Windows\Voidstrap.exe`. Like the release version, it needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run.

### Linux app

**You need:** the [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/linux), version 10.0.300 or a newer 10.0 release. Check it with `dotnet --version`. If your distro only offers an older version, use Microsoft's [install script](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual).

```bash
dotnet publish src/Voidstrap.Cross/Voidstrap.Cross.csproj \
  -c Release -r linux-x64 --self-contained true \
  -o PublishedBuilds/Linux-x64 \
  -p:EnableWindowsTargeting=true \
  -p:DebugType=none -p:DebugSymbols=false
```

The app is saved to `PublishedBuilds/Linux-x64/Voidstrap`. It includes .NET, so there is nothing else to install. Build only this project on Linux, because the full solution also contains the Windows app, which only builds on Windows.

For other systems, change `linux-x64` (and the output folder) to:

* `linux-arm64` for ARM64
* `linux-musl-x64` or `linux-musl-arm64` for musl based distros like Alpine

**Release packages (optional)**

To make `.tar.gz`, `.deb`, `.rpm`, `.AppImage` and `.flatpak` files, install [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/linux-overview) and the packaging tools. On Ubuntu or Debian:

```bash
sudo apt-get install curl ca-certificates binutils dpkg rpm flatpak flatpak-builder
```

Then run:

```bash
pwsh -NoProfile -File ./publish-all.ps1 -Only linux-x64 -LinuxPackages -NoPause
```

The packages are saved to `PublishedBuilds/Linux`. The first run takes longer because it downloads the AppImage and Flatpak tools.

* For an AppImage only, use `-AppImage` instead of `-LinuxPackages`. This skips the RPM and Flatpak tools.
* musl builds only produce a `.tar.gz`.

### Android app

**You need:**

* [JDK 17](https://adoptium.net/temurin/releases/?version=17)
* The [Android SDK](https://developer.android.com/studio) with SDK Platform 37, Build Tools 37.0.0 and NDK 29.0.14206865 (install them from the SDK Manager in Android Studio)
* [Rust](https://rustup.rs/) 1.85 or newer

Set `JAVA_HOME` to your JDK folder and `ANDROID_HOME` to your Android SDK folder. Then add the Rust targets (only needed once):

```bash
rustup target add aarch64-linux-android armv7-linux-androideabi x86_64-linux-android
```

Build the debug APKs:

```bash
cd android
./gradlew assembleDebug
```

On Windows, use `.\gradlew.bat assembleDebug` instead. The APKs are saved to `android/app/build/outputs/apk/`, in two versions:

* `play`: the Google Play version
* `direct`: the GitHub version, which updates itself and includes the server matchmaker

**Signed release APKs**

Release APKs are signed with your own keystore. Add these lines to `~/.gradle/gradle.properties`:

```properties
voidstrap.storeFile=/path/to/your.keystore
voidstrap.storePassword=your_store_password
voidstrap.keyAlias=your_key_alias
voidstrap.keyPassword=your_key_password
```

Then run this from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only android
```

On Linux, use `pwsh -NoProfile -File ./publish-all.ps1 -Only android` instead. The signed APKs are saved to `PublishedBuilds/Android`.

> [!TIP]
> The publish script reuses build caches to save time. Add `-Clean` to any `publish-all.ps1` command to rebuild from scratch.

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

### Third party licenses

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
| [Bibata Modern Ice cursors](https://github.com/ful1e5/Bibata_Cursor) | Windows and Android apps | GPL 3.0 | [License](src/Voidstrap.App/Resources/Mods/Cursor/BibataModernIce/LICENSE.txt) |
| ppmd-rust | Android app | CC0 1.0 or MIT No Attribution | [CC0](android/rust/vendor/ppmd-rust/LICENSE-CC0.md), [MIT-0](android/rust/vendor/ppmd-rust/LICENSE-MIT-0.md) |

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
[shield-repo-sponsors]:   https://img.shields.io/github/sponsors/KloBraticc?logo=githubsponsors&logoColor=white&label=Sponsors&color=ea4aaa

[repo-releases]:          https://github.com/KloBraticc/Voidstrap/releases
[repo-latest]:            https://github.com/KloBraticc/Voidstrap/releases/latest
[repo-stargazers]:        https://github.com/KloBraticc/Voidstrap/stargazers
[discord-invite]:         https://discord.gg/dfA9PdWgcV
[sponsor-link]:           https://github.com/sponsors/KloBraticc

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
