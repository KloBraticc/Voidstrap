# Contributing to Voidstrap

Thanks for helping make Voidstrap better. This guide explains how to report bugs, suggest features and send changes.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting a bug

1. Search the [existing issues](https://github.com/KloBraticc/Voidstrap/issues) first, in case the bug is already reported.
2. Open a [new issue](https://github.com/KloBraticc/Voidstrap/issues/new) and include:
   * your Voidstrap version and platform (Windows or Android)
   * what you did, what you expected, and what happened instead
   * the log file from `%LOCALAPPDATA%\Voidstrap\Logs` on Windows. Logs make most bugs quick to fix, so attach one whenever you can. Read it first and remove anything you do not want to share.

## Suggesting a feature

Open an issue that describes the problem the feature would solve. Voidstrap will never add cheats, exploits or anything else that breaks Roblox's rules, so requests like that are closed.

## Reporting a security problem

Do not open a public issue for security problems. Follow the [security policy](SECURITY.md) to report them privately through GitHub or Discord.

## Contributing code

### Setting up

Follow the [Building](README.md#building) section of the README to install the requirements and build the apps.

### Workflow

1. Fork the repository and create a branch from `main`.
2. Make your change. Keep each pull request focused on one fix or feature.
3. Check that it builds cleanly with the commands below.
4. Open a pull request against `main` that explains what changed, why, and how you tested it. Add screenshots for UI changes.

Every pull request runs the **Desktop validation** and **Android validation** checks, and both must pass before it can be merged.

### Checking your change

Run the same checks as CI before you open a pull request.

Windows app, from the repository root:

```powershell
dotnet build Voidstrap.sln -c Debug -warnaserror
```

Android app, from the `android` folder (on Windows, use `.\gradlew.bat`):

```bash
./gradlew assemble lintPlayRelease lintDirectRelease
```

### Code style

* Match the style of the code around your change.
* Do not add code comments. Use clear names instead to keep the code clean, and please keep profanity out.
* The build must have zero warnings. Fix warnings instead of suppressing them.
* Put text that users see in `src/Voidstrap.App/Resources/Strings.resx` instead of hardcoding it. Do not use dashes in UI text or log messages to prevent them from looking ugly. Dashes may be used when necessary or when there is no better alternative.
* Use named methods for event handlers and unsubscribe them when the object is cleaned up. Stop and dispose timers the same way.
* Reuse a single static `HttpClient`, pass a `CancellationToken` to background loops, and use `await Task.Delay` instead of `Thread.Sleep`.
* Use `[LibraryImport]` for native calls, not `[DllImport]`.

### Android changes

* Request only the permissions a feature needs.
* Do not obfuscate code, load code at runtime, or run encoded shell commands.
* Keep matchmaker and Roblox login code out of the `play` flavor. CI fails the build if it finds them there.

## Translations

The app's English text is in `src/Voidstrap.App/Resources/Strings.resx`, and each translation is in a matching `Strings.<language>.resx` file next to it. To fix or improve a translation, edit the file for that language.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE.VOIDSTRAP).
