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

### Run the real-provider smoke test
Use this from the repo root to run the integration smoke test that transcribes the shared fixture audio against every configured non-streaming provider:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" test .\timbre.tests\timbre.tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~RealTranscriptionSmokeTests"
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

## Integration Test Assets
- Real provider smoke tests live in `timbre.tests/RealTranscriptionSmokeTests.cs`.
- Provider API keys for local/manual testing live in the repo-root `.env` file.
- Shared fixture audio for manual API probing and smoke tests lives at `TEST-AUDIO.wav` in the repo root.
- Use these assets when working on provider integrations, provider-specific bugs, request/response shape validation, or when you need to confirm how a provider API behaves with real audio instead of mocks.
- Do not print, persist, or echo API keys back to the user.

## Notes For Agents
- **ALWAYS build and run tests after modifying code.** Do not assume your changes work without verifying they compile and pass existing tests.
- **Add tests for new features:** If you are implementing new logic, adding new models, or fixing complex bugs, always consider writing or updating tests in the `timbre.tests` project (using xUnit, Moq, and FluentAssertions).
- When changing provider integrations or diagnosing provider behavior, strongly consider running `RealTranscriptionSmokeTests` with the repo-root `.env` and `TEST-AUDIO.wav` so you can verify real end-to-end transcription behavior.
- If the user asks to add a new provider, check the repo-root `.env` file early for a usable API key for that provider before doing substantial implementation work.
- For a new provider, if the `.env` file does not contain a key for that provider, stop and ask the user to add one before proceeding further.
- For a new provider, after confirming a key exists, manually probe the provider API with the shared `TEST-AUDIO.wav` during implementation so you can inspect the real request/response behavior alongside thoroughly reading the provider's docs.
- For a new provider, if manual API probing shows the key is invalid, unauthorized, mis-scoped, or otherwise errors in a way that blocks reliable integration work, stop and tell the user so they can correct the key before you continue.
- If a new provider is added, update both `timbre.tests/RealTranscriptionSmokeTests.cs` and `AGENTS.md` in the same change so the provider is testable immediately and the workflow stays documented.
- If real API behavior differs from the provider docs, trust the actual response you observe, note the discrepancy, and implement against the real behavior.
- Prefer using the full `dotnet` path if command execution says `dotnet` is not recognized.
- For WinUI settings rows with paired controls like `TextBox`/`ComboBox` plus a trailing action button, use `Spacing="8"` on the horizontal container to match the existing settings layout.
- Keep transcription provider selection as a vertical list of radio buttons.
- Keep transcription model controls standardized across providers:
  - Keep each transcription provider settings panel ordered as: API key, `Streaming` toggle if supported, model picker, language input if supported, then help text.
  - Every transcription provider must expose a model picker as a `ComboBox`, even if the provider currently has only one model.
  - If a provider supports streaming/realtime/live transcription for any model, show a `ToggleSwitch` titled exactly `Streaming`.
  - When `Streaming` is on, the model picker must show only streaming-compatible models. When `Streaming` is off, it must show only non-streaming/batch/upload models.
  - Providers with no streaming/realtime/live support should not show a `Streaming` toggle.
  - Provider-specific secondary streaming options, such as Mistral's streaming cadence, may be shown only as extra controls; do not use them instead of the standardized `Streaming` toggle and model picker.
  - Keep provider capabilities centralized in `TranscriptionProviderCatalog`: display name, supported models, streaming compatibility, language behavior, upload limits, and defaults. Keep `TranscriptionModelCatalog` only as a compatibility facade for model-only call sites.
- Keep provider language inputs narrow (`Width="160"`) and use consistent helper text. For providers that support auto-detect, use exactly: `Use ISO-639-1 or ISO-639-3 code.`, `Setting 'auto', the provider will detect it.`, `If left blank, defaults to 'auto'`. For providers that do not support auto-detect, use only: `Use ISO-639-1 or ISO-639-3 code.`
- Prefer `Streaming` naming in app code for provider live/realtime toggles. When migrating older names such as `MistralRealtimeEnabled`, keep backward compatibility in settings loading.
- Tray icon context menu reliability was fixed by giving it a dedicated hidden native owner window in `TrayIconService` and de-duping `WM_CONTEXTMENU`/`WM_RBUTTONUP`; avoid reworking that tray/menu path unless you have a clear, reproducible bug.
- When changing WinUI input/focus behavior, build after edits because some issues only show up at compile/runtime.
- If asked to diagnose crashes around settings input, check the latest log in `%LOCALAPPDATA%\Timbre\logs\` before guessing.
