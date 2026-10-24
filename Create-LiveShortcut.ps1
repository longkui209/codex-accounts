[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$RuntimePath = (Join-Path $PSScriptRoot 'dist\CodexAccountSwitcher.exe'),
    [switch]$Apply,
    [switch]$Remove,
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('DesktopDirectory')
)
$ErrorActionPreference = 'Stop'
# Compose the label without relying on PowerShell 5.1's source-file code page.
$shortcutDisplayName = 'Codex' + [char]0xFF08 + [char]0x8D26 + [char]0x53F7 + [char]0x76F4 + [char]0x5207 + [char]0xFF09
$shortcutName = $shortcutDisplayName + '.lnk'
$shortcutMarker = 'CodexAccountSwitcher.LiveLaunch.v1'
$runtimeFullPath = [IO.Path]::GetFullPath($RuntimePath)
$desktopFullPath = [IO.Path]::GetFullPath($ShortcutDirectory)
if ([IO.Path]::GetFileName($runtimeFullPath) -ne 'CodexAccountSwitcher.exe') {
    throw 'The runtime must be the published CodexAccountSwitcher.exe.'
}
if (-not (Test-Path -LiteralPath $desktopFullPath -PathType Container)) {
    throw 'The intended shortcut directory does not exist.'
}
if (-not $Remove -and -not (Test-Path -LiteralPath $runtimeFullPath -PathType Leaf)) {
    throw 'Publish the live-mode runtime before creating this shortcut.'
}
$shortcutPath = [IO.Path]::GetFullPath((Join-Path $desktopFullPath $shortcutName))
$directoryPrefix = $desktopFullPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $shortcutPath.StartsWith($directoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Shortcut path is outside the intended directory.'
}

$shell = $null
$shortcut = $null
try {
    if (Test-Path -LiteralPath $shortcutPath) {
        $existingItem = Get-Item -LiteralPath $shortcutPath -Force
        if (($existingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $existingItem.PSIsContainer) {
            throw 'The existing shortcut path is not a regular owned shortcut; it was not changed.'
        }
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $existingTarget = [IO.Path]::GetFullPath([string]$shortcut.TargetPath)
        if (-not $existingTarget.Equals($runtimeFullPath, [StringComparison]::OrdinalIgnoreCase) -or
            [string]$shortcut.Arguments -ne '--live-launch' -or
            [string]$shortcut.Description -ne $shortcutMarker) {
            throw 'The same-named shortcut belongs to another target; it was not overwritten or removed.'
        }
    }

    if ($Remove) {
        if (-not (Test-Path -LiteralPath $shortcutPath)) {
            Write-Output 'The owned live-mode shortcut is already absent.'
        } elseif (-not $Apply) {
            Write-Output ('Review: only the verified ' + $shortcutName + ' would be removed. Use -Remove -Apply to apply.')
        } elseif ($PSCmdlet.ShouldProcess($shortcutName, 'Remove the verified owned shortcut')) {
            Remove-Item -LiteralPath $shortcutPath -Force
            Write-Output 'Removed the owned live-mode shortcut. The normal Codex entry and account data remain available.'
        }
        return
    }

    if (-not $Apply) {
        Write-Output ('Review: create ' + $shortcutName + ' for the published runtime with --live-launch. Use -Apply to apply.')
        return
    }
    if ($PSCmdlet.ShouldProcess($shortcutName, 'Create or update the owned live-mode shortcut')) {
        if (-not $shell) { $shell = New-Object -ComObject WScript.Shell }
        if (-not $shortcut) { $shortcut = $shell.CreateShortcut($shortcutPath) }
        $shortcut.TargetPath = $runtimeFullPath
        $shortcut.Arguments = '--live-launch'
        $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($runtimeFullPath)
        $shortcut.Description = $shortcutMarker
        $shortcut.IconLocation = $runtimeFullPath + ',0'
        $shortcut.WindowStyle = 7
        $shortcut.Save()
        Write-Output ('Created ' + $shortcutDisplayName + '. Start it after normally closing the existing desktop. No account was switched during installation.')
    }
} finally {
    if ($shortcut -and [Runtime.InteropServices.Marshal]::IsComObject($shortcut)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
    if ($shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}
