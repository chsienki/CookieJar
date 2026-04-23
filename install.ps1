<#
.SYNOPSIS
  Installs CookieJar: publishes the .NET host, registers the Edge native-messaging
  manifest + registry key, and prints instructions for loading the extension.

.PARAMETER ExtensionId
  The Edge extension ID. If omitted, the script first tells you to load the
  unpacked extension to obtain an ID, then re-run with -ExtensionId <id>.

.PARAMETER Browser
  edge (default) or chrome.

.PARAMETER InstallDir
  Destination for the published host. Defaults to %LOCALAPPDATA%\CookieJar.
#>
[CmdletBinding()]
param(
    [string]$ExtensionId,
    [ValidateSet('edge','chrome')] [string]$Browser = 'edge',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'CookieJar')
)

$ErrorActionPreference = 'Stop'
$repoRoot     = Split-Path -Parent $PSCommandPath
$projectFile  = Join-Path $repoRoot 'server\CookieJar.Host\CookieJar.Host.csproj'
$publishDir   = Join-Path $InstallDir 'bin'
$hostExe      = Join-Path $publishDir 'CookieJar.Host.exe'
$extensionDir = Join-Path $repoRoot 'extension'

Write-Host "==> Publishing host to $publishDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
& dotnet publish $projectFile -c Release -r win-x64 --self-contained false -o $publishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

Write-Host "==> Generating bearer token (if needed)" -ForegroundColor Cyan
$token = (& $hostExe --print-token).Trim()
Write-Host "    Token file: $InstallDir\token.txt"

if (-not $ExtensionId) {
    Write-Host ""
    Write-Host "==> Next step: load the extension to obtain its ID" -ForegroundColor Yellow
    Write-Host "    1. Open  edge://extensions/"
    Write-Host "    2. Toggle 'Developer mode' on (bottom-left)."
    Write-Host "    3. Click 'Load unpacked' and select:"
    Write-Host "         $extensionDir"
    Write-Host "    4. Copy the extension ID shown under the extension card."
    Write-Host "    5. Re-run:  .\install.ps1 -ExtensionId <id>"
    return
}

Write-Host "==> Writing native-messaging manifest for $Browser (extension $ExtensionId)" -ForegroundColor Cyan
& $hostExe --write-manifest $hostExe $ExtensionId --browser $Browser
if ($LASTEXITCODE -ne 0) { throw "manifest install failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    Reload the CookieJar extension in $Browser to pick up the host."
Write-Host ""
Write-Host "    Quick test (PowerShell):"
Write-Host "      `$t = Get-Content $InstallDir\token.txt"
Write-Host "      Invoke-RestMethod -Headers @{Authorization=`"Bearer `$t`"} ``"
Write-Host "        'http://127.0.0.1:47891/cookies?domain=example.com'"
