# Using AI

AI is allowed in Voidstrap and always will be. New contributors can use it too. Use it to help you learn the code, work through ideas, write code, fix bugs, or test things. You're still responsible for what you submit.

## Get to know the code first

Take some time to look through the codebase before changing anything. Read the README and contributing guide, learn how the project fits together, and follow the code you're planning to change from start to finish. Check how it works on the platforms your change affects too.

AI can help explain things. Check those explanations against the actual code and ask questions if something doesn't make sense.

## Check your work

Read the whole diff, including anything AI wrote. Make sure you understand what changed and why, then build and test it. If there's a part you can't explain, work through it before sending the change.

Be ready to tell us what you changed, why it was needed, and how you tested it. The same review and checks apply to everyone, whether they used AI or wrote everything themselves.

## For coding assistants

Read [README.md](README.md) and [CONTRIBUTING.md](CONTRIBUTING.md) first. Look at the existing code, where it's used, and how the different platforms handle it before editing. Follow the style already there, keep the change focused, and leave unrelated work alone.

Check your suggestions against the code. Run the checks that matter for the change and say what passed or failed. Explain what you changed and point out anything you couldn't test or still aren't sure about.

AI tools are welcome here. Help the contributor understand and review the work they're submitting.

## Commits

Commit work to `dock-overlay`. Use another branch only if the repository owner asks for it.

Write a short summary that says what the diff actually changes. Start with `feature:`, `fix:`, `improvement:`, `chore:`, `ci:`, `docs:`, `build:`, `refactor:`, `test:`, or `revert:` as appropriate. Use `request:` only for changes that record or handle a request. Keep summaries within 72 characters, skip semicolons, and avoid vague titles like "update stuff" or "prepare release" when the commit changes more than a version number.

Use the repository owner's configured Git identity for commits made on their behalf. Preserve other contributors' authorship when rewriting history. Don't add assistant attribution or co-author trailers.
