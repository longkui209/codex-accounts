[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $project 'dist\CodexAccountSwitcher.exe'
if (-not (Test-Path -LiteralPath $runtime)) { throw 'Build the source before checking the owned shortcut fixture.' }
$root = Join-Path $project ('validation-output\shortcut-fixture-' + [guid]::NewGuid().ToString('N'))
$owned = Join-Path $root 'owned'
$foreign = Join-Path $root 'foreign'
New-Item -ItemType Directory -Path $owned, $foreign -Force | Out-Null
$name = 'Codex' + [char]0xFF08 + [char]0x8BA2 + [char]0x9605 + [char]0x5207 + [char]0x6362 + [char]0xFF09 + '.lnk'
$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($directory in @($owned, $foreign)) {
        $link = $shell.CreateShortcut((Join-Path $directory $name))
        try {
            $link.TargetPath = $runtime; $link.Arguments = '--routing-launch'
            $link.Description = if ($directory -eq $owned) { 'CodexAccountSwitcher.RoutingLaunch.v1' } else { 'Synthetic unrelated shortcut' }
            $link.Save()
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    }
    $before = (Get-FileHash -LiteralPath (Join-Path $owned $name)).Hash
    $foreignBefore = (Get-FileHash -LiteralPath (Join-Path $foreign $name)).Hash
    $repair = Join-Path $project 'Repair-LegacyRoutingShortcut.ps1'
    $preview = & $repair -ShortcutDirectory $owned -BackupDirectory (Join-Path $root 'backups')
    if ($preview.state -ne 'migration-available' -or (Get-FileHash -LiteralPath (Join-Path $owned $name)).Hash -ne $before) { throw 'Preview changed the fixture or native integration is not ready.' }
    $result = & $repair -ShortcutDirectory $owned -BackupDirectory (Join-Path $root 'backups') -Apply
    if ($result.state -ne 'native-entry' -or -not $result.changed -or (Get-FileHash -LiteralPath $result.backup).Hash -ne $before) { throw 'Owned migration or backup verification failed.' }
    $link = $shell.CreateShortcut((Join-Path $owned $name))
    try {
        if ($link.TargetPath -ne (Join-Path $env:WINDIR 'explorer.exe') -or $link.Arguments -notmatch '^shell:AppsFolder\\OpenAI\.Codex_2p2nqsd0c76g0![A-Za-z0-9_.-]+$') { throw 'Fixture is not a version-independent native entry.' }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    $repeat = & $repair -ShortcutDirectory $owned -BackupDirectory (Join-Path $root 'backups') -Apply
    if ($repeat.changed -or $repeat.state -ne 'native-entry') { throw 'Repeated migration is not idempotent.' }
    $skipped = & $repair -ShortcutDirectory $foreign -BackupDirectory (Join-Path $root 'foreign-backups') -Apply
    if ($skipped.state -ne 'not-owned' -or (Get-FileHash -LiteralPath (Join-Path $foreign $name)).Hash -ne $foreignBefore) { throw 'Unrelated shortcut was changed.' }
    [pscustomobject]@{ passed = $true; ownedFixtureOnly = $true; nativeMetadataReadOnly = $true; backupVerified = $true; idempotent = $true; foreignShortcutPreserved = $true; desktopLaunched = $false; accountDataRead = $false } | ConvertTo-Json -Compress
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
