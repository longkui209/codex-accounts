using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.LiveSession;

/// <summary>Launches only the installed, signed desktop with a child-scoped transport override.</summary>
public static class DesktopLaunch
{
    private const string ExpectedPackageName = "OpenAI.Codex";
    private const string ExpectedPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string ExpectedPublisherId = "2p2nqsd0c76g0";
    private const string ExpectedPublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    private const string ExpectedSigner = "OpenAI OpCo, LLC";
    private const string SignatureQueryPathVariable = "CODEX_ACCOUNT_SWITCHER_SIGNATURE_PATH";

    private const string SignatureCommand = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $ProgressPreference = 'SilentlyContinue'
        $signature = Get-AuthenticodeSignature -LiteralPath $env:CODEX_ACCOUNT_SWITCHER_SIGNATURE_PATH
        [pscustomobject]@{
            SignatureStatus = [string]$signature.Status
            SignerName = if ($signature.SignerCertificate) {
                $signature.SignerCertificate.GetNameInfo(
                    [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
            } else { '' }
        } | ConvertTo-Json -Compress
        """;

    // This reads public installation metadata and Authenticode status only. No auth/config is read.
    private const string DiscoveryCommand = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $ProgressPreference = 'SilentlyContinue'
        $records = @(Get-AppxPackage -Name OpenAI.Codex -PackageTypeFilter Main | ForEach-Object {
            $desktop = Join-Path $_.InstallLocation 'app\ChatGPT.exe'
            if (Test-Path -LiteralPath $desktop -PathType Leaf) {
                $signature = Get-AuthenticodeSignature -LiteralPath $desktop
                [pscustomobject]@{
                    Name = [string]$_.Name
                    PackageFamily = [string]$_.PackageFamilyName
                    PublisherId = [string]$_.PublisherId
                    Publisher = [string]$_.Publisher
                    Version = [string]$_.Version
                    SignatureKind = [string]$_.SignatureKind
                    PackageStatus = [string]$_.Status
                    InstallLocation = [string]$_.InstallLocation
                    SignatureStatus = [string]$signature.Status
                    SignerName = if ($signature.SignerCertificate) {
                        $signature.SignerCertificate.GetNameInfo(
                            [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
                    } else { '' }
                }
            }
        })
        ConvertTo-Json -InputObject $records -Compress -Depth 3
        """;

    public static string BuildWebSocketUrl(string desktopPipeName)
    {
        if (string.IsNullOrEmpty(desktopPipeName) || desktopPipeName.Length > 128 ||
            !Regex.IsMatch(desktopPipeName, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant))
            throw new UserFacingException("桌面连接管道名称无效，未启动 Codex。");

        // The literal backslash after the device dot prevents WHATWG dot-segment removal.
        // localhost preserves the desktop's normal local/no-SOCKS routing branch.
        return @"ws+unix://localhost//.\pipe/" + desktopPipeName + ":/";
    }

    public static Process Launch(string desktopPipeName)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("账号直切启动入口仅支持 Windows。");

        var websocketUrl = BuildWebSocketUrl(desktopPipeName);
        var executable = FindInstalledDesktop();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            CreateNoWindow = false
        };
        start.Environment["CODEX_APP_SERVER_WS_URL"] = websocketUrl;
        start.Environment.Remove("CODEX_APP_SERVER_FORCE_CLI");

        // Inspect/initial binding belongs to the owner before it starts its backend. Calling
        // Inspect here would mistake that already-owned backend for an old desktop client.
        // Existing desktop singleton instances do not inherit this new process's environment.
        try
        {
            return Process.Start(start) ?? throw new UserFacingException("未能启动官方 Codex 桌面版。");
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or SecurityException)
        {
            throw new UserFacingException("官方 Codex 桌面启动失败；请保留普通启动入口并重新启用账号直切。");
        }
    }

    public static void ValidateOfficialCli(string cliPath)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(cliPath))
            throw new UserFacingException("官方 Codex 后台路径无效，未启动直切后台。");
        try
        {
            var executable = Path.GetFullPath(cliPath);
            if (!Path.GetFileName(executable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(executable))
                throw new UserFacingException("官方 Codex 后台程序不存在，未启动直切后台。");
            EnsureNoReparsePoints(executable);
            var signature = JsonSerializer.Deserialize<SignatureRecord>(ReadMetadata(SignatureCommand, executable));
            if (signature is null || signature.SignatureStatus != "Valid" || signature.SignerName != ExpectedSigner)
                throw new UserFacingException("Codex 后台程序没有有效的 OpenAI 签名，未启动直切后台。");
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or
                                            UnauthorizedAccessException or SecurityException)
        {
            throw new UserFacingException("无法验证官方 Codex 后台签名，未启动直切后台。");
        }
    }

    internal static void ValidateSignedRuntimeFile(string path)
    {
        if(!Path.IsPathFullyQualified(path)||Path.GetFileName(path) is not ("ChatGPT.exe" or "chrome.dll" or "chrome_elf.dll" or "codex.exe"))
            throw new UserFacingException("原生运行文件路径无效。");
        EnsureNoReparsePoints(path);
        var signature=JsonSerializer.Deserialize<SignatureRecord>(ReadMetadata(SignatureCommand,path));
        if(signature is null||signature.SignatureStatus!="Valid"||signature.SignerName!=ExpectedSigner)
            throw new UserFacingException("原生运行文件的 OpenAI 签名校验未通过。");
    }

    internal static void ValidateSandboxServiceImage(string path)
    {
        if(!Path.IsPathFullyQualified(path)||Path.GetFileName(path)!="codex-windows-sandbox-service.exe")
            throw new UserFacingException("官方沙箱服务路径无效。");
        EnsureNoReparsePoints(path);
        var signature=JsonSerializer.Deserialize<SignatureRecord>(ReadMetadata(SignatureCommand,path));
        if(signature is null||signature.SignatureStatus!="Valid"||signature.SignerName!=ExpectedSigner)
            throw new UserFacingException("官方沙箱服务的 OpenAI 签名校验未通过。");
    }

    internal static string FindInstalledDesktop()
    {
        var values=FindInstalledDesktops(false);
        if(values.Length!=1)throw InvalidInstallation();
        return values[0];
    }

    internal static string[] FindInstalledDesktops(bool allUsers)
    {
        try
        {
            var discovery=allUsers?DiscoveryCommand.Replace("Get-AppxPackage -Name OpenAI.Codex -PackageTypeFilter Main",
                "Get-AppxPackage -AllUsers -Name OpenAI.Codex -PackageTypeFilter Main",StringComparison.Ordinal):DiscoveryCommand;
            var packages = JsonSerializer.Deserialize<InstalledPackage[]>(ReadMetadata(discovery)) ?? [];
            var trusted = packages.Where(package =>
                package.Name == ExpectedPackageName &&
                package.PackageFamily == ExpectedPackageFamily &&
                package.PublisherId == ExpectedPublisherId &&
                package.Publisher == ExpectedPublisher &&
                package.SignatureKind == "Store" &&
                package.PackageStatus == "Ok" &&
                package.SignatureStatus == "Valid" &&
                package.SignerName == ExpectedSigner &&
                Version.TryParse(package.Version, out _)).DistinctBy(package=>package.InstallLocation,StringComparer.OrdinalIgnoreCase).ToArray();
            if (trusted.Length is <1 or >16 || !allUsers&&trusted.Length!=1) throw InvalidInstallation();

            var result=new List<string>();
            foreach(var package in trusted)
            {
            if (!Path.IsPathFullyQualified(package.InstallLocation)) throw InvalidInstallation();
            var installRoot = Path.GetFullPath(package.InstallLocation);
            if (!Directory.Exists(installRoot)) throw InvalidInstallation();
            var executable = Path.GetFullPath(Path.Combine(installRoot, "app", "ChatGPT.exe"));
            if (!File.Exists(executable) || !executable.StartsWith(
                    installRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw InvalidInstallation();
            EnsureNoReparsePoints(executable);
            result.Add(executable);
            }
            return result.ToArray();
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or
                                            UnauthorizedAccessException or SecurityException)
        {
            throw InvalidInstallation();
        }
    }

    private static string ReadMetadata(string command, string? signaturePath = null)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // PowerShell 7 callers can inherit a module path without the Windows
        // PowerShell built-ins. The fixed system metadata probe needs its own
        // normal module discovery; no process/user/machine setting is changed.
        start.Environment.Remove("PSModulePath");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(argument);
        if (signaturePath is not null) start.Environment[SignatureQueryPathVariable] = signaturePath;

        try
        {
            using var discovery = Process.Start(start) ?? throw InvalidInstallation();
            var output = discovery.StandardOutput.ReadToEndAsync();
            var errors = discovery.StandardError.ReadToEndAsync();
            if (!discovery.WaitForExit(15_000))
            {
                // Only this metadata query is owned by us. Never stop a desktop process here.
                try { discovery.Kill(entireProcessTree: false); } catch { }
                throw new UserFacingException("验证官方 Codex 安装或签名超时，未启动直切程序。");
            }
            var json = output.GetAwaiter().GetResult();
            _ = errors.GetAwaiter().GetResult(); // Do not echo command output or certificate metadata.
            if (discovery.ExitCode != 0 || json.Length > 65_536)
                throw new UserFacingException("无法验证官方 Codex 安装或签名，未启动直切程序。");
            return json;
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or
                                            ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new UserFacingException("无法验证官方 Codex 安装或签名，未启动直切程序。");
        }
    }

    private static void EnsureNoReparsePoints(string executable)
    {
        if ((File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0) throw InvalidInstallation();
        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
             directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw InvalidInstallation();
        }
    }

    private static UserFacingException InvalidInstallation() => new(
        "没有找到唯一且签名有效的官方 Codex Store 安装，未启动桌面；请先修复官方安装。");

    private sealed class InstalledPackage
    {
        public InstalledPackage() { }
        public string Name { get; set; } = "";
        public string PackageFamily { get; set; } = "";
        public string PublisherId { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Version { get; set; } = "";
        public string SignatureKind { get; set; } = "";
        public string PackageStatus { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string SignatureStatus { get; set; } = "";
        public string SignerName { get; set; } = "";
    }

    private sealed class SignatureRecord
    {
        public SignatureRecord() { }
        public string SignatureStatus { get; set; } = "";
        public string SignerName { get; set; } = "";
    }
}
