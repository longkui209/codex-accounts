[CmdletBinding()]
param(
    [string]$RuntimePath = (Join-Path $PSScriptRoot 'dist\CodexAccountSwitcher.exe'),
    [switch]$Apply,
    [switch]$Remove,
    [switch]$CheckRuntimeOnly,
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('DesktopDirectory')
)
$ErrorActionPreference = 'Stop'

function Assert-RoutingRuntime {
    $dotnetCommand = Get-Command 'dotnet.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnetCommand) {
        throw 'Install the x64 .NET 8 Desktop Runtime and ASP.NET Core 8 Runtime before preparing subscription routing.'
    }
    $binaryReader = $null
    $binaryStream = $null
    try {
        $binaryStream = [IO.File]::OpenRead($dotnetCommand.Source)
        $binaryReader = New-Object IO.BinaryReader($binaryStream)
        if ($binaryReader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid runtime host.' }
        $binaryStream.Position = 0x3C
        $peOffset = $binaryReader.ReadUInt32()
        if ($peOffset -gt $binaryStream.Length - 6) { throw 'Invalid runtime host.' }
        $binaryStream.Position = $peOffset
        if ($binaryReader.ReadUInt32() -ne 0x00004550 -or $binaryReader.ReadUInt16() -ne 0x8664) {
            throw 'Invalid runtime host.'
        }
    } catch {
        throw 'The subscription-routing runtime requires a valid x64 .NET host.'
    } finally {
        if ($binaryReader) { $binaryReader.Dispose() }
        if ($binaryStream) { $binaryStream.Dispose() }
    }

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $dotnetCommand.Source
    $start.Arguments = '--list-runtimes'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $runtimeQuery = $null
    try {
        $runtimeQuery = [Diagnostics.Process]::Start($start)
        if (-not $runtimeQuery) { throw 'Runtime query failed.' }
        $outputTask = $runtimeQuery.StandardOutput.ReadToEndAsync()
        $errorTask = $runtimeQuery.StandardError.ReadToEndAsync()
        if (-not $runtimeQuery.WaitForExit(10000)) {
            # Stop only this owned read-only metadata query, never a desktop/backend process.
            try { $runtimeQuery.Kill() } catch { }
            throw 'Runtime query timed out.'
        }
        $runtimeListing = $outputTask.GetAwaiter().GetResult()
        $null = $errorTask.GetAwaiter().GetResult()
        if ($runtimeQuery.ExitCode -ne 0) { throw 'Runtime query failed.' }
        $hasNet8 = $runtimeListing -match '(?m)^Microsoft\.NETCore\.App 8\.\d+\.\d+ \['
        $hasDesktop8 = $runtimeListing -match '(?m)^Microsoft\.WindowsDesktop\.App 8\.\d+\.\d+ \['
        $hasAspNet8 = $runtimeListing -match '(?m)^Microsoft\.AspNetCore\.App 8\.\d+\.\d+ \['
        if (-not $hasNet8 -or -not $hasDesktop8 -or -not $hasAspNet8) {
            throw 'Required .NET shared runtime is missing.'
        }
    } catch {
        throw 'Install or repair the x64 .NET 8 Desktop Runtime and ASP.NET Core 8 Runtime before preparing subscription routing.'
    } finally {
        if ($runtimeQuery) { $runtimeQuery.Dispose() }
    }
    Write-Output 'Ready: x64 .NET 8 Desktop and ASP.NET Core 8 shared runtimes are available.'
}

# A build entry may call -CheckRuntimeOnly without creating or requiring a published shortcut target.
if ($CheckRuntimeOnly) {
    if ($Apply -or $Remove) { throw 'Use -CheckRuntimeOnly separately from shortcut installation or removal.' }
    Assert-RoutingRuntime
    return
}
if (-not $Remove) { Assert-RoutingRuntime }
$shortcutScript = Join-Path $PSScriptRoot 'Create-RoutingShortcut.ps1'
if (-not (Test-Path -LiteralPath $shortcutScript -PathType Leaf)) {
    throw 'The reviewed subscription-routing shortcut installer is missing.'
}
# This only prepares/removes an explicit entry; it never builds, starts Codex or changes global env.
& $shortcutScript -RuntimePath $RuntimePath -ShortcutDirectory $ShortcutDirectory -Apply:$Apply -Remove:$Remove
