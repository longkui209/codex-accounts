[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$pluginPath = Join-Path $PSScriptRoot 'Plugin'
$marketplacePath = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $pluginPath 'runtime\CodexAccountSwitcher.exe'))) {
    throw 'Run Build-Plugin.ps1 before installing.'
}
$binaryRoot = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
$cli = Get-ChildItem -LiteralPath $binaryRoot -Recurse -Filter 'codex.exe' -File -ErrorAction Stop |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $cli) { throw 'Official Codex CLI was not found.' }

# Only plugin configuration is updated. Authentication and running processes are never touched.
$userCodexPath = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$configPath = Join-Path $userCodexPath 'config.toml'
$backupPath = $null
if (Test-Path -LiteralPath $configPath) {
    Add-Type -AssemblyName System.Security
    $plain = [System.IO.File]::ReadAllBytes($configPath)
    try {
        $encrypted = [System.Security.Cryptography.ProtectedData]::Protect($plain, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $backupRoot = Join-Path $env:LOCALAPPDATA 'CodexAccountSwitcher\config-backups'
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $backupPath = Join-Path $backupRoot ('plugin-config-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dpapi')
        [System.IO.File]::WriteAllBytes($backupPath, $encrypted)
    } finally { [Array]::Clear($plain, 0, $plain.Length) }
}
& $cli plugin marketplace add $marketplacePath --json
if ($LASTEXITCODE -ne 0) { throw 'Marketplace registration failed; authentication was not changed.' }
$expectedManifest = Get-Content -LiteralPath (Join-Path $pluginPath '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
$registryText = & $cli plugin list --json 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Plugin registry check failed; authentication was not changed.' }
$registry = ($registryText -join [Environment]::NewLine) | ConvertFrom-Json
$existing = @($registry.installed | Where-Object { $_.pluginId -eq 'codex-accounts@codex-accounts-local' })
$alreadyCurrent = $existing.Count -eq 1 -and $existing[0].installed -and $existing[0].enabled -and $existing[0].version -eq $expectedManifest.version
if ($alreadyCurrent) {
    $currentCache = Join-Path $userCodexPath ('plugins\cache\codex-accounts-local\codex-accounts\' + $expectedManifest.version)
    foreach ($runtimeName in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json')) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $pluginPath ('runtime\' + $runtimeName)) -Algorithm SHA256).Hash
        $cacheHash = (Get-FileHash -LiteralPath (Join-Path $currentCache ('runtime\' + $runtimeName)) -Algorithm SHA256).Hash
        if ($sourceHash -ne $cacheHash) { throw 'Installed version has different files. Publish a new plugin version rather than overwriting an active cache.' }
    }
    Write-Output 'The exact published plugin version is already installed and enabled.'
} else {
    & $cli plugin add 'codex-accounts@codex-accounts-local' --json
    if ($LASTEXITCODE -ne 0) { throw 'Plugin installation failed; authentication was not changed.' }
}
Write-Output 'Plugin registered. After Codex reloads plugin metadata, use Explore > Codex account switcher; it can be pinned in sidebar customization.'
if ($backupPath) { Write-Output ('Encrypted configuration backup: ' + $backupPath) }
