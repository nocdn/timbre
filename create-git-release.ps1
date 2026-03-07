[CmdletBinding()]
param(
    [string]$CommitMessage,
    [string]$Version,
    [switch]$SkipConfirmation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-RequiredValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prompt
    )

    while ($true) {
        $value = Read-Host $Prompt
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }

        Write-Host 'A value is required.' -ForegroundColor Yellow
    }
}

function Invoke-GitCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $displayCommand = @('git') + $Arguments
    Write-Host ''
    Write-Host "> $($displayCommand -join ' ')" -ForegroundColor Cyan

    & git @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "git command failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

if (-not $CommitMessage) {
    $CommitMessage = Read-RequiredValue 'Enter commit message'
}

if (-not $Version) {
    $Version = Read-RequiredValue 'Enter version number (example: 1.5.6)'
}

$trimmedVersion = $Version.Trim()
$tagName = if ($trimmedVersion.StartsWith('v')) { $trimmedVersion } else { "v$trimmedVersion" }

Write-Host ''
Write-Host 'About to run:' -ForegroundColor Green
Write-Host "  git add ."
Write-Host "  git commit -m ""$CommitMessage"""
Write-Host '  git push'
Write-Host "  git tag $tagName"
Write-Host "  git push origin $tagName"

if (-not $SkipConfirmation) {
    $confirmation = Read-Host 'Continue? (Y/N)'
    if ($confirmation -notmatch '^(y|yes)$') {
        Write-Host 'Cancelled.'
        exit 0
    }
}

Invoke-GitCommand -Arguments @('add', '.')
Invoke-GitCommand -Arguments @('commit', '-m', $CommitMessage)
Invoke-GitCommand -Arguments @('push')
Invoke-GitCommand -Arguments @('tag', $tagName)
Invoke-GitCommand -Arguments @('push', 'origin', $tagName)

Write-Host ''
Write-Host "Release tag pushed successfully: $tagName" -ForegroundColor Green
