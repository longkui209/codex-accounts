[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
    $testExecutable = Join-Path $PSScriptRoot 'dist\CodexAccountSwitcher.exe'
    if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) { throw 'Build the application before testing.' }
    $testDirectory = Join-Path $PSScriptRoot ('validation-output\suite-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $testExecutable
    $start.Arguments = '--self-test "' + $testDirectory + '"'
    $start.WorkingDirectory = $PSScriptRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $core = [Diagnostics.Process]::Start($start)
    try {
        if (-not $core.WaitForExit(120000)) {
            try { $core.Kill($true) } catch { }
            throw 'Synthetic core checks timed out; only the owned test process was stopped.'
        }
        if ($core.ExitCode -ne 0) { throw 'Synthetic core checks failed; inspect the private result.' }
    } finally { $core.Dispose() }
    $coreResult = Get-Content -LiteralPath (Join-Path $testDirectory 'result.json') -Raw | ConvertFrom-Json
    if (-not $coreResult.passed -or @($coreResult.checks).Count -eq 0) { throw 'Core result is missing successful checks.' }

    # Each existing runner creates its own isolated synthetic home. No real CLI or desktop is launched.
    $scripts = @(
        @{ Path = 'Validation\routing-protocol.cjs'; Arguments = @($testExecutable) },
        @{ Path = 'Validation\mcp-protocol.cjs'; Arguments = @() },
        @{ Path = 'Validation\routing-panel-preview.cjs'; Arguments = @('--dom-check') },
        @{ Path = 'scripts\public-release.cjs'; Arguments = @('test') },
        @{ Path = 'scripts\public-release.cjs'; Arguments = @('check') }
    )
    foreach ($entry in $scripts) {
        $arguments = @($entry.Arguments)
        $output = & node (Join-Path $PSScriptRoot $entry.Path) @arguments
        if ($LASTEXITCODE -ne 0) { throw ('Synthetic check failed: ' + $entry.Path) }
        $lines = @($output | Where-Object { $_ -match '^\{' })
        if ($lines.Count -eq 0) { throw ('Missing structured result: ' + $entry.Path) }
        $result = $lines[-1] | ConvertFrom-Json
        if (-not $result.passed) { throw ('Check did not report success: ' + $entry.Path) }
        Write-Output ('PASS: ' + $entry.Path)
    }
    $coreSkipped = @($coreResult.checks | Where-Object { $_ -match '^SKIP:' }).Count
    $summary = [ordered]@{ passed = $true; syntheticOnly = $true; coreChecks = @($coreResult.checks).Count; coreSkipped = $coreSkipped; runners = $scripts.Count; realAccountOperations = $false }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testDirectory 'summary.json') -Encoding UTF8
    Write-Output ($summary | ConvertTo-Json -Compress)
} finally { Pop-Location }
