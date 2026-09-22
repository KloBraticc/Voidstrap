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

  <!-- Linux
  <img src="https://raw.githubusercontent.com/devicons/devicon/master/icons/linux/linux-original.svg" alt="Linux" width="32" height="32"/>
  &nbsp;&nbsp;
  -->

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
> Voidstrap currently supports **Windows 10 and above** and **Android**.
> **macOS and Linux support are currently in development** and will be available in a future release.
>
> If you're looking for a Bootstrapper for other platforms in the meantime:
> - **macOS:** [AppleBlox](https://github.com/AppleBlox/appleblox)
> - **Linux:** [Sober](https://sober.vinegarhq.org/)
> - **Linux:** [Lution](https://github.com/wookhq/Lution)

> [!WARNING]
> Voidstrap is not an exploit and never will be. We are not considered an exploit. We are here to give users more freedom, features, and support for Roblox.
>
> As of Voidstrap Version `1.1.2.3`, Multi-Instance Launching has been removed from the app and will not be added back in the future.

## Installation

1. Download the latest version
   👉 https://github.com/KloBraticc/Voidstrap/releases/latest
2. Run the Exe and Finish the setup
3. Launch Voidstrap
4. Enjoy a more simple Roblox

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

## Built With

[![C#][shield-csharp]][link-csharp] [![.NET][shield-dotnet]][link-dotnet] [![WPF][shield-wpf]][link-wpf] [![WPF UI][shield-wpfui]][link-wpfui] [![WebView2][shield-webview2]][link-webview2]

[![Java][shield-java]][link-java] [![Rust][shield-rust]][link-rust] [![Android][shield-android]][link-android]

## Building

The Windows app can only be built on Windows. The Android app can be built on Windows, Linux or macOS.

### Requirements

**Windows app**

* Windows 10 or 11 (x64)
* [.NET SDK 10.0.300](https://dotnet.microsoft.com/download/dotnet/10.0) or newer

**Android app**

* [JDK 17](https://adoptium.net/temurin/releases/?version=17)
* [Android SDK](https://developer.android.com/studio) with SDK Platform 37, Build Tools 37.0.0 and NDK 29.0.14206865, installed from the SDK Manager in Android Studio
* [Rust](https://rustup.rs/) 1.85 or newer, installed with rustup

### Get the source

Clone the repository with [Git](https://git-scm.com/downloads):

```bash
git clone https://github.com/KloBraticc/Voidstrap.git
cd Voidstrap
```

> [!TIP]
> On Windows, clone into a short folder such as `C:\src\Voidstrap`. Windows limits file paths to 260 characters, so a clone inside a deeply nested folder can fail with `Filename too long`, or fail to build with `CS0234` errors about `Windows.Security`.

### Windows app

```powershell
dotnet build Voidstrap.sln -c Release
```

The app is written to `src\Voidstrap.App\bin\Release\net10.0-windows\win-x64\Voidstrap.exe`.

To build the single `Voidstrap.exe` that releases ship, run the publish script:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only windows
```

It is written to `PublishedBuilds\Windows\Voidstrap.exe`. Like the releases, it needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run.

### Android app

Add the Rust targets once:

```bash
rustup target add aarch64-linux-android armv7-linux-androideabi x86_64-linux-android
```

Gradle finds the JDK through `JAVA_HOME` and the Android SDK through `ANDROID_HOME`, so set both before building. Then build the debug APKs:

```bash
cd android
./gradlew assembleDebug
```

On Windows, run `.\gradlew.bat assembleDebug` instead. The APKs are written to `android/app/build/outputs/apk/`, one per flavor: `play` is the Google Play version, and `direct` is the GitHub download, which updates itself and includes the server matchmaker.

Release APKs are signed with your own keystore. Add `voidstrap.storeFile`, `voidstrap.storePassword`, `voidstrap.keyAlias` and `voidstrap.keyPassword` to `~/.gradle/gradle.properties`, then run the publish script from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-all.ps1 -Only android
```

The signed APKs are written to `PublishedBuilds\Android`. Each run of the publish script clears `PublishedBuilds` first, so add `-NoClean` to keep the output of an earlier run.

## Forking

To create your own copy of Voidstrap:

1. Open the [Voidstrap repository](https://github.com/KloBraticc/Voidstrap).
2. Click **Fork**.
3. Select your GitHub account.

## Credits

Voidstrap is built on [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap) by pizzaboxer.

## License

[MIT License](https://github.com/KloBraticc/Voidstrap/blob/main/LICENSE.VOIDSTRAP)

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
