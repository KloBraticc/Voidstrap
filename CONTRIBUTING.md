# Contributing to Voidstrap

Thank you for contributing to Voidstrap. You can help by reporting a reproducible bug, improving a translation, correcting documentation, or submitting code. This guide explains what to include and how changes are reviewed.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting a bug

1. Search the [existing issues](https://github.com/KloBraticc/Voidstrap/issues), including closed reports, for the same problem. If a matching report is open, add useful reproduction details there.
2. Check whether the problem still occurs in the [latest release](https://github.com/KloBraticc/Voidstrap/releases/latest).
3. Choose **Bug report** from the [issue forms](https://github.com/KloBraticc/Voidstrap/issues/new/choose).
4. Include your Voidstrap version, operating system, steps to reproduce the problem, expected result, and actual result. Describe one problem per report.
5. Attach a relevant log, screenshot, or recording when available. Remove passwords, login cookies, access tokens, private server links, and other private information first.

Voidstrap supports Windows, Linux, macOS, and Android. For Linux, include your distribution, package format, desktop session, and Roblox runtime when relevant. For macOS, include your macOS version and whether the Mac uses Intel or Apple silicon. For Android, include the app flavor (`play` or `direct`) and whether you use Shizuku, ADB, or root.

Windows logs normally live in `%LOCALAPPDATA%\Voidstrap\Logs`. Use the app's log-folder shortcut when available, especially if you chose a different installation folder. If you cannot obtain a log, explain that in the report; do not post credentials to make a report more complete.

For setup questions or help using a feature, ask in the [Voidstrap Discord server](https://discord.gg/5tJBqBH8ck). Security reports use the private process below.

## Suggesting a feature

Choose **Feature request** from the [issue forms](https://github.com/KloBraticc/Voidstrap/issues/new/choose). Explain the problem, who it affects, the behavior you want, and any workarounds you have tried. Screenshots or a short example can help.

Voidstrap does not accept features intended for cheating, exploitation, account theft, or bypassing Roblox's security. A feature request starts a discussion; it is not a promise that the feature will be implemented.

## Reporting a security problem

Do not open a public issue for security problems. Follow the [security policy](SECURITY.md) to report them privately through GitHub or Discord.

## Contributing code

### Using AI tools

AI is allowed here and always will be, including for new contributors. Get familiar with the codebase before changing things. Use AI to help you learn and work, then read the whole diff, understand it, and test it before sending it. You're responsible for your changes and should be able to explain what they do and why.

The same review and checks apply to everyone. Read [AGENTS.md](AGENTS.md) for more on this. [CLAUDE.md](CLAUDE.md) links to that same file.

### Setting up

Follow the [Building](README.md#building) section of the README to install the requirements and build the apps.

Clone with submodules, or run `git submodule update --init --recursive` in an existing checkout. The shared desktop application lives in `src/Voidstrap.App`; `src/Voidstrap.Cross` compiles it for Linux and macOS. Platform projects live under `src/Voidstrap.Platform.*` and `src/Voidstrap.Desktop.*`. The Android application is in `android`, and native libraries are in `native`.

### Workflow

1. Fork the repository and create a branch from `main`.
2. Make your change. Keep each pull request focused on one fix or feature.
3. Check that it builds cleanly with the commands below.
4. Open a pull request against `main` that explains what changed, why, and how you tested it. Add screenshots for UI changes.

Use the pull request template to explain the problem, resulting behavior, and checks you actually ran. Link the issue when one exists. Mark unfinished work as a draft, and describe any platform or hardware you could not test.

Workflows run according to the changed paths. Desktop changes normally run **Desktop validation**; Android changes run **Android validation**. Documentation-only changes may not need an application build. All applicable checks must pass before a maintainer merges the pull request.

Reviewers may request changes or ask for reproduction details. Keep follow-up commits focused, respond to the feedback, and avoid repeatedly tagging maintainers. The repository bot does not approve or merge pull requests.

### Checking your change

Run the checks relevant to the files you changed. A successful build on one operating system does not establish that the feature works on every platform.

Windows desktop application, from the repository root:

```powershell
dotnet build Voidstrap.sln -c Debug -warnaserror
```

Linux or macOS shared desktop application, from the repository root:

```bash
dotnet build src/Voidstrap.Cross/Voidstrap.Cross.csproj -c Debug -warnaserror
```

The full solution requires Windows. Follow the [platform build instructions](README.md#building) for native toolchains, runtime dependencies, and packaging.

Android app, from the `android` folder (on Windows, use `.\gradlew.bat`):

```bash
./gradlew assemble lintPlayRelease lintDirectRelease
```

For native archive or RiShade changes, also run the affected Cargo tests. On Windows, use the MSVC target:

```powershell
cargo test --manifest-path native/voidpkg/Cargo.toml --locked --target x86_64-pc-windows-msvc --target-dir "$env:TEMP\VoidstrapTests\voidpkg"
cargo test --manifest-path native/rishade/Cargo.toml --workspace --locked --target x86_64-pc-windows-msvc --target-dir "$env:TEMP\VoidstrapTests\rishade"
```

On Linux, omit the Windows target and place `--target-dir` outside the checkout.

### Code style

* Match the style of the code around your change.
* Do not add code comments. Use clear names instead to keep the code clean, and please keep profanity out.
* The build must have zero warnings. Fix warnings instead of suppressing them.
* Put text that users see in `src/Voidstrap.App/Resources/Strings.resx` instead of hardcoding it. Do not use dashes in UI text or log messages to prevent them from looking ugly. Dashes may be used when necessary or when there is no better alternative.
* Use named methods for event handlers and unsubscribe them when the object is cleaned up. Stop and dispose timers the same way.
* Reuse a single static `HttpClient`, pass a `CancellationToken` to background loops, and use `await Task.Delay` instead of `Thread.Sleep`.
* Use `[LibraryImport]` for native calls, not `[DllImport]`.
* Put shared behavior in the shared application code. A new desktop feature must work on Linux or be hidden there with the platform visibility helpers.
* Keep secrets, generated build output, temporary projects, and personal configuration out of commits.

### Android changes

* Request only the permissions a feature needs.
* Do not obfuscate code, load code at runtime, or run encoded shell commands.
* Keep matchmaker and Roblox login code out of the `play` flavor. CI fails the build if it finds them there.

## Translations

The English source strings are in `src/Voidstrap.App/Resources/Strings.resx`. Translations are in the matching `Strings.<language>.resx` files beside it. Keep placeholders, line breaks, and file-filter patterns intact. Do not replace an existing translation with an empty value.

When adding visible desktop text, add the resource property and all shipped translations in the same change. Use the localization binding so the text changes when the user switches languages. Maintainers synchronize accepted language changes with Crowdin.

## Repository automation

The **Community triage** workflow adds platform labels to issue forms and labels pull requests by the files they change. It uses repository metadata; it does not run code from a pull request.

For a recognized bug or feature form with empty required sections, it posts one specific request for details. Later edits update that same comment. When those details are supplied, it resolves the request and removes the information label it added. Logs and screenshots remain optional. Freeform reports are left for human review.

The bot does not post welcome messages, repeatedly remind people, close inactive issues, assign reviewers, approve changes, or merge pull requests. It also skips closed or locked reports. Maintainers can add `automation: off` to exclude an issue or pull request. Platform labels are managed automatically; unrelated labels and human comments are preserved.

To inspect its decisions without changing an issue, maintainers can run **Community triage** from the repository's **Actions** tab and enter an issue or pull request number. Manual runs only produce a dry-run summary in the workflow log.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE.VOIDSTRAP).
