# AGENTS.md

## Repo Overview
- Repo: `whisper-windows`
- App project: `timbre/timbre.csproj`
- Installer project: `installer/timbre.installer.wixproj`
- Main app is a WinUI 3 desktop app targeting `.NET 8` on Windows.

## Environment Notes
- The shell environment may NOT have `dotnet` on `PATH`, even if the user does in normal PowerShell.
- If `dotnet` is not found, use the full path:
  - `C:\Program Files\dotnet\dotnet.exe`
- Preferred shell for user-facing commands: PowerShell

## Build Commands
### Build the app
Use this from the repo root:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\timbre\timbre.csproj -c Release -p:Platform=x64
```

If `dotnet` is on PATH in the user's shell, this is equivalent:

```powershell
dotnet build .\timbre\timbre.csproj -c Release -p:Platform=x64
```

### Run tests
Use this from the repo root to run the test suite:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" test .\timbre.tests\timbre.tests.csproj -p:Platform=x64
```

### Build the installer
From the repo root:

```powershell
.\build-installer.ps1 -Version 0.0.0
```

To build and immediately launch the installer:

```powershell
.\build-installer.ps1 -Version 0.0.0 -OpenInstaller
```

## Installer / Versioning Notes
- MSI upgrades require monotonically increasing versions.
- If a higher version is already installed, a lower version MSI will not install over it.
- If testing from scratch after uninstalling all previous versions, `0.0.0` is fine.
- Then use `0.0.1`, `0.0.2`, etc. for later local installer tests.

## Release Workflow
- Releases are now created automatically on every push to `main`.
- No manual tag push is required anymore.
- Preview releases were removed.
- The GitHub Actions workflow auto-creates a normal GitHub release per push.

## Logs
- App logs are written to:

```text
%LOCALAPPDATA%\Timbre\logs\
```

- Files are named like:

```text
startup-YYYYMMDD-HHMMSS.log
```

- If the app crashes, inspect the newest log file first.

## Useful Output Paths
- Built app output:

```text
timbre\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\
```

- Built MSI output:

```text
installer\bin\Release\timbre.installer.msi
```

## Notes For Agents
- **ALWAYS build and run tests after modifying code.** Do not assume your changes work without verifying they compile and pass existing tests.
- **Add tests for new features:** If you are implementing new logic, adding new models, or fixing complex bugs, always consider writing or updating tests in the `timbre.tests` project (using xUnit, Moq, and FluentAssertions).
- Prefer using the full `dotnet` path if command execution says `dotnet` is not recognized.
- For WinUI settings rows with paired controls like `TextBox`/`ComboBox` plus a trailing action button, use `Spacing="8"` on the horizontal container to match the existing settings layout.
- Tray icon context menu reliability was fixed by giving it a dedicated hidden native owner window in `TrayIconService` and de-duping `WM_CONTEXTMENU`/`WM_RBUTTONUP`; avoid reworking that tray/menu path unless you have a clear, reproducible bug.
- When changing WinUI input/focus behavior, build after edits because some issues only show up at compile/runtime.
- If asked to diagnose crashes around settings input, check the latest log in `%LOCALAPPDATA%\Timbre\logs\` before guessing.
