# Contributing to Clipora

Thanks for helping improve Clipora.

## Before opening an issue

- Search existing issues first.
- Use the bug-report or feature-request form.
- Remove clipboard contents, local paths, usernames, database files, screenshots with personal data, and other sensitive information.
- For vulnerabilities, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Development setup

Clipora requires Windows 10/11, the .NET 10 SDK, and optionally Inno Setup 6 for installer builds.

```powershell
# Isolated development run
powershell -ExecutionPolicy Bypass -File scripts/start-dev.ps1 -Build

# Build
dotnet build src/Clipora
```

Development builds must be launched through `scripts/start-dev.ps1`; it isolates data under `.dev-data` so development does not touch the installed application's data.

## Pull requests

- Keep each pull request focused on one change.
- Explain the user-visible behavior and how it was verified.
- Run `dotnet build src/Clipora` before submitting.
- Add or update tests when behavior changes.
- Do not commit `bin/`, `obj/`, databases, logs, local configuration, release binaries, certificates, credentials, or user data.
- By submitting a contribution, you agree that it is licensed under the repository's [MIT License](LICENSE).
