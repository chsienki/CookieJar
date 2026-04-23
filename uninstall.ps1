<#
.SYNOPSIS
  Removes CookieJar's native-messaging manifest, registry key, and (optionally)
  the install directory + token.

.PARAMETER KeepToken
  If set, the bearer token at %LOCALAPPDATA%\CookieJar\token.txt is preserved
  so a future install can reuse it.

.PARAMETER InstallDir
  Install directory. Defaults to %LOCALAPPDATA%\CookieJar.
#>
[CmdletBinding()]
param(
    [switch]$KeepToken,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'CookieJar')
)

$ErrorActionPreference = 'Continue'
$hostExe = Join-Path $InstallDir 'bin\CookieJar.Host.exe'

if (Test-Path $hostExe) {
    Write-Host "==> Removing native-messaging manifests and registry keys" -ForegroundColor Cyan
    & $hostExe --remove-manifest
}

if ($KeepToken -and (Test-Path (Join-Path $InstallDir 'token.txt'))) {
    Write-Host "==> Keeping token; removing binaries only" -ForegroundColor Cyan
    Remove-Item -Recurse -Force (Join-Path $InstallDir 'bin') -ErrorAction SilentlyContinue
} elseif (Test-Path $InstallDir) {
    Write-Host "==> Removing $InstallDir" -ForegroundColor Cyan
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
}

Write-Host "==> Done." -ForegroundColor Green
Write-Host "    Remove the extension manually from edge://extensions/ if desired."
