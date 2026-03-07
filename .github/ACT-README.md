# Testing GitHub Actions locally with act

This repository includes `.actrc` configured to run the Windows workflow on your local Windows host.

## Requirements

- Windows machine
- `act` installed
- .NET SDK 8.0.418 installed locally
- Node.js installed locally

## Dry run

```powershell
act -n
```

## Run the workflow without publishing a GitHub release

```powershell
act workflow_dispatch -W .github/workflows/build-release.yml --input publish_release=false
```

If `act` says the workflow needs input, you can also pass an event file instead:

```json
{
  "inputs": {
    "publish_release": "false"
  }
}
```

Save that as `act-event.json`, then run:

```powershell
act workflow_dispatch -W .github/workflows/build-release.yml -e .\act-event.json
```

## Run the workflow and simulate release publishing

Create a `.secrets` file with:

```text
GITHUB_TOKEN=ghp_your_token_here
```

Then run:

```powershell
act workflow_dispatch -W .github/workflows/build-release.yml --input publish_release=true --secret-file .secrets
```

## Notes

- Because `.actrc` maps `windows-2022` to `-self-hosted`, the job runs on your local Windows machine instead of a Linux container.
- Docker is not required for this specific workflow when run this way.
- JavaScript actions in the workflow still need Node.js available on your PATH.
- `publish_release=false` is the safest first test.
- The workflow writes the MSI to `installer\bin\Release\` and zips the working app payload from `timbre\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\`.
- Artifact behavior under `act` is only a local approximation of GitHub Actions.
- `publish_release=true` talks to the real GitHub Releases API, so use a test repository or test branch first.
