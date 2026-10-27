[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Apply,
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$BackupDirectory = (Join-Path $env:LOCALAPPDATA 'CodexAccountSwitcher\startup\shortcut-backups')
)
$ErrorActionPreference = 'Stop'
$name = 'Codex' + [char]0xFF08 + [char]0x8BA2 + [char]0x9605 + [char]0x5207 + [char]0x6362 + [char]0xFF09 + '.lnk'
$marker = 'CodexAccountSwitcher.RoutingLaunch.v1'
$nativeMarker = 'CodexAccountSwitcher.RoutingLaunch.NativeEntry.v2'
$directMarker = 'CodexAccountSwitcher.RoutingLaunch.DirectImage.v3'
. (Join-Path $PSScriptRoot 'NativeLaunchCommand.ps1')
$powershell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
$directArguments = Get-CodexNativeLaunchArguments
$shortcutPath = Join-Path ([IO.Path]::GetFullPath($ShortcutDirectory)) $name
if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) {
    Write-Output ([pscustomobject]@{ state = 'not-present'; changed = $false }); return
}
function Assert-PlainPath([string]$path) {
    for ($current = [IO.Path]::GetFullPath($path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Shortcut migration refuses linked paths.'
        }
    }
}
Assert-PlainPath $shortcutPath
$shell = New-Object -ComObject WScript.Shell
$link = $null
$replacement = $null
$temporary = $null
try {
    $link = $shell.CreateShortcut($shortcutPath)
    if ($link.Description -eq $directMarker) {
        $state = if ([string]::Equals([string]$link.TargetPath,$powershell,[StringComparison]::OrdinalIgnoreCase) -and
            (Get-CodexShortcutArguments $shortcutPath) -eq $directArguments) { 'direct-entry' } else { 'not-owned' }
        Write-Output ([pscustomobject]@{state=$state;changed=$false});return
    }
    $ownedNativeEntry = $false
    if ($link.Description -eq $nativeMarker) {
        $systemExplorer = Join-Path $env:WINDIR 'explorer.exe'
        $ownedNativeEntry = [string]::Equals([string]$link.TargetPath, $systemExplorer, [StringComparison]::OrdinalIgnoreCase) -and
            [string]$link.Arguments -match '^shell:AppsFolder\\OpenAI\.Codex_2p2nqsd0c76g0![A-Za-z0-9_.-]+$'
    }
    $published = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist')).TrimEnd('\') + '\'
    $oldTarget = [IO.Path]::GetFullPath([string]$link.TargetPath)
    if (-not $ownedNativeEntry -and ($link.Description -ne $marker -or $link.Arguments -ne '--routing-launch' -or
        [IO.Path]::GetFileName($oldTarget) -ne 'CodexAccountSwitcher.exe' -or
        -not $oldTarget.StartsWith($published, [StringComparison]::OrdinalIgnoreCase))) {
        Write-Output ([pscustomobject]@{ state = 'not-owned'; changed = $false }); return
    }
    $packages = @(Get-AppxPackage -Name OpenAI.Codex -PackageTypeFilter Main | Where-Object {
        $_.PackageFamilyName -eq 'OpenAI.Codex_2p2nqsd0c76g0' -and [string]$_.Status -eq 'Ok'
    })
    if ($packages.Count -ne 1) { throw 'A unique official Codex registration is required.' }
    $package = $packages[0]
    $native = Join-Path $package.InstallLocation 'app\ChatGPT.exe'
    $root = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ChatGPT.exe'
    $filters = if (Test-Path -LiteralPath $root) { @(Get-ChildItem -LiteralPath $root | Where-Object {
        $_.GetValue('CodexAccountsOwner') -eq 'codex-accounts-native-startup-v1' -and
        [string]::Equals([string]$_.GetValue('FilterFullPath'), $native, [StringComparison]::OrdinalIgnoreCase)
    }) } else { @() }
    if ($filters.Count -ne 1) {
        Write-Output ([pscustomobject]@{ state = 'integration-not-ready'; changed = $false }); return
    }
    $registeredRoot = Get-Item -LiteralPath $root
    if ($registeredRoot.GetValue('UseFilter') -ne 1 -or $null -ne $registeredRoot.GetValue('Debugger')) {
        Write-Output ([pscustomobject]@{ state = 'integration-not-ready'; changed = $false }); return
    }
    Assert-PlainPath $native
    $signature = Get-AuthenticodeSignature -LiteralPath $native
    if ([string]$signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
        $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'OpenAI OpCo, LLC') {
        throw 'Official Codex signature verification failed.'
    }
    $manifest = Get-AppxPackageManifest -Package $package.PackageFullName
    $applications = @($manifest.Package.Applications.Application | Where-Object { ([string]$_.Executable).Replace('/', '\') -eq 'app\ChatGPT.exe' })
    if ($applications.Count -ne 1 -or [string]$applications[0].Id -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Official native entry is ambiguous.' }
    $validationCode = Get-CodexNativeLaunchCommand -ValidateOnly
    $validated = & ([scriptblock]::Create($validationCode)) | ConvertFrom-Json
    if (-not $validated.verified -or $validated.launchMode -ne 'direct-image') { throw 'Direct native entry preflight failed.' }
    if (-not $Apply) {
        Write-Output ([pscustomobject]@{ state = 'migration-available'; changed = $false; entry = 'direct-current-signed-image' }); return
    }
    if ($PSCmdlet.ShouldProcess($name, 'Route the verified legacy shortcut through the registered native entry')) {
        $before = (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash
        $backupRoot = [IO.Path]::GetFullPath($BackupDirectory)
        if (-not (Test-Path -LiteralPath $backupRoot)) { New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null }
        Assert-PlainPath $backupRoot
        $backup = Join-Path $backupRoot ('routing-shortcut-' + [guid]::NewGuid().ToString('N') + '.lnk')
        Copy-Item -LiteralPath $shortcutPath -Destination $backup -ErrorAction Stop
        if ((Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash -ne $before -or
            (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $before) { throw 'Shortcut changed during backup; original was not replaced.' }
        $temporary = Join-Path ([IO.Path]::GetDirectoryName($shortcutPath)) ('.codex-routing-' + [guid]::NewGuid().ToString('N') + '.lnk')
        Copy-Item -LiteralPath $shortcutPath -Destination $temporary -ErrorAction Stop
        $replacement = $shell.CreateShortcut($temporary)
        $replacement.TargetPath = $powershell
        $replacement.Arguments = ''
        $replacement.WorkingDirectory = $env:USERPROFILE
        $replacement.IconLocation = $native + ',0'
        $replacement.Description = $directMarker
        $replacement.WindowStyle = 7
        $replacement.Save()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($replacement); $replacement = $null
        Set-CodexShortcutArguments -Path $temporary -Arguments $directArguments
        if ((Get-CodexShortcutArguments $temporary) -ne $directArguments) { throw 'The full native command was not preserved in the replacement.' }
        if ((Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash -ne $before) { throw 'Shortcut changed before replacement; original was preserved.' }
        [CodexAccounts.ShortcutArguments]::Replace($temporary, $shortcutPath)
        $temporary = $null
        Write-Output ([pscustomobject]@{ state = 'direct-entry'; changed = $true; entry = 'direct-current-signed-image'; backup = $backup })
    }
} finally {
    if ($replacement) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($replacement) }
    if ($temporary -and (Test-Path -LiteralPath $temporary)) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
        if ([IO.Path]::GetDirectoryName($resolvedTemporary) -eq [IO.Path]::GetDirectoryName($shortcutPath) -and
            [IO.Path]::GetFileName($resolvedTemporary) -match '^\.codex-routing-[a-f0-9]{32}\.lnk$') { Remove-Item -LiteralPath $resolvedTemporary -Force }
    }
    if ($link) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
