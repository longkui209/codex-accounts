# Only a fixed, self-contained command is embedded in an owned shortcut.
# It resolves and verifies the current signed image before a direct activation.
function Get-CodexNativeLaunchCommand {
    param([switch]$ValidateOnly)
    $prefix = if ($ValidateOnly) { '$caValidateOnly = $true;' } else { '$caValidateOnly = $false;' }
    $body = @'
$ErrorActionPreference = 'Stop'
try {
    $packages = @(Get-AppxPackage -Name OpenAI.Codex -PackageTypeFilter Main | Where-Object {
        $_.PackageFamilyName -eq 'OpenAI.Codex_2p2nqsd0c76g0' -and [string]$_.Status -eq 'Ok'
    })
    if ($packages.Count -ne 1) { throw 'The official Codex installation is not unique.' }
    $image = [IO.Path]::GetFullPath((Join-Path $packages[0].InstallLocation 'app\ChatGPT.exe'))
    for ($current = $image; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The official image path is linked.' }
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $image
    if ([string]$signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
        $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'OpenAI OpCo, LLC') { throw 'The official Codex signature was not verified.' }
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ChatGPT.exe'
    $root = Get-Item -LiteralPath $key
    if ($root.GetValue('UseFilter') -ne 1 -or $null -ne $root.GetValue('Debugger')) { throw 'Native startup integration is not ready.' }
    $filters = @(Get-ChildItem -LiteralPath $key | Where-Object {
        $_.GetValue('CodexAccountsOwner') -eq 'codex-accounts-native-startup-v1' -and
        [string]::Equals([string]$_.GetValue('FilterFullPath'), $image, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($filters.Count -ne 1) { throw 'No unique owned rule matches the current official image.' }
    $command = [string]$filters[0].GetValue('Debugger')
    $match = [regex]::Match($command, '^"(?<file>[^"]+)" --integrated-launch$')
    if (-not $match.Success) { throw 'The owned startup command is invalid.' }
    $component = $match.Groups['file'].Value
    $versions = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Codex Accounts Integration\versions'
    $relative = [IO.Path]::GetFullPath($component).Substring($versions.Length)
    if (-not $component.StartsWith($versions + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $relative -notmatch '^\\[a-fA-F0-9]{32}\\CodexAccountSwitcher\.exe$') { throw 'The owned component path is invalid.' }
    if (-not (Test-Path -LiteralPath $component -PathType Leaf)) { throw 'The owned component is missing.' }
    if ($caValidateOnly) {
        [pscustomobject]@{ verified = $true; launchMode = 'direct-image'; image = $image; sourceVersion = $packages[0].Version.ToString(); accountDataRead = $false } | ConvertTo-Json -Compress
    } else {
        # AppsFolder activation can bypass IFEO. This intentionally matches the
        # direct signed-image path used by the verified proxy launcher.
        Start-Process -FilePath $image -WorkingDirectory $env:USERPROFILE -WindowStyle Normal
    }
} catch {
    if ($caValidateOnly) { throw }
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show('Codex startup integration could not be verified. Open the account panel and check startup integration.','Codex Accounts',0,48)
}
'@
    return ($prefix + $body).Replace("`r`n", "`n")
}

function Get-CodexNativeLaunchArguments {
    param([switch]$ValidateOnly)
    $command = Get-CodexNativeLaunchCommand -ValidateOnly:$ValidateOnly
    return '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
}

# WScript.Shell silently truncates argument strings at 1023 characters. Use the
# documented Unicode shell-link interface for this self-contained command.
function Initialize-CodexShortcutArgumentsInterop {
    if ('CodexAccounts.ShortcutArguments' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace CodexAccounts {
  public static class ShortcutArguments {
    [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW {
      void GetPath(StringBuilder text,int count,IntPtr data,uint flags);
      void GetIDList(out IntPtr value); void SetIDList(IntPtr value);
      void GetDescription(StringBuilder text,int count);
      void SetDescription([MarshalAs(UnmanagedType.LPWStr)]string text);
      void GetWorkingDirectory(StringBuilder text,int count);
      void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)]string text);
      void GetArguments(StringBuilder text,int count);
      void SetArguments([MarshalAs(UnmanagedType.LPWStr)]string text);
    }
    private static object Create() { return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"),true)); }
    public static string Read(string file) {
      object link=Create();try { ((IPersistFile)link).Load(file,0);var text=new StringBuilder(32768);((IShellLinkW)link).GetArguments(text,text.Capacity);return text.ToString(); }
      finally { Marshal.FinalReleaseComObject(link); }
    }
    public static void Write(string file,string arguments) {
      if(arguments==null||arguments.Length>24000||arguments.IndexOf('\0')>=0)throw new ArgumentException();
      object link=Create();try { ((IPersistFile)link).Load(file,2);((IShellLinkW)link).SetArguments(arguments);((IPersistFile)link).Save(file,true); }
      finally { Marshal.FinalReleaseComObject(link); }
    }
    public static void Replace(string source,string destination) {
      System.IO.File.Replace(source,destination,null);
    }
  }
}
'@
}

function Get-CodexShortcutArguments([string]$Path) {
    Initialize-CodexShortcutArgumentsInterop
    return [CodexAccounts.ShortcutArguments]::Read([IO.Path]::GetFullPath($Path))
}

function Set-CodexShortcutArguments([string]$Path,[string]$Arguments) {
    Initialize-CodexShortcutArgumentsInterop
    [CodexAccounts.ShortcutArguments]::Write([IO.Path]::GetFullPath($Path),$Arguments)
}
