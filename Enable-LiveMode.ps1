[CmdletBinding()]
param(
    [string]$RuntimePath = (Join-Path $PSScriptRoot 'dist\CodexAccountSwitcher.exe'),
    [switch]$Apply,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$shortcutScript = Join-Path $PSScriptRoot 'Create-LiveShortcut.ps1'
if (-not (Test-Path -LiteralPath $shortcutScript -PathType Leaf)) {
    throw 'The reviewed shortcut installer is missing.'
}
# This installs/removes only an explicit desktop entry. It never starts the app,
# changes user/machine environment variables, alters auth, or creates a service.
& $shortcutScript -RuntimePath $RuntimePath -Apply:$Apply -Remove:$Remove
