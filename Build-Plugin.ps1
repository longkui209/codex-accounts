[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'Build.ps1')
    $runtimePath = Join-Path $PSScriptRoot 'Plugin\runtime'
    New-Item -ItemType Directory -Path $runtimePath -Force | Out-Null
    foreach ($name in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('dist\' + $name)) -Destination (Join-Path $runtimePath $name) -Force
    }
    Write-Output ('Plugin package: ' + (Join-Path $PSScriptRoot 'Plugin'))
} finally { Pop-Location }
