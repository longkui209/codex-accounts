using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

public static class CodexRuntime
{
    public static string ResolveCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configured;
        return AuthFileSafety.NormalizeDirectory(directory);
    }

    public static string? FindCli()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(local, "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe")
        };
        foreach (var bin in new[]
                 {
                     Path.Combine(local, "OpenAI", "Codex", "bin"),
                     Path.Combine(local, "Programs", "OpenAI", "Codex", "bin")
                 })
        {
            try
            {
                if (!Directory.Exists(bin)) continue;
                using var protectedDirectories = AuthFileSafety.ProtectDirectories(bin);
                var versionDirectories = Directory.EnumerateDirectories(bin)
                    .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    .Select(path => Path.Combine(path, "codex.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray();
                candidates.AddRange(versionDirectories);
            }
            catch (Exception error) when (error is UserFacingException or ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
            {
                // Do not follow links or unreadable version directories while discovering the CLI.
            }
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            candidates.Add(Path.Combine(directory, "codex.exe"));
        }

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path) || Directory.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                using var directories = AuthFileSafety.ProtectDirectories(Path.GetDirectoryName(path)!);
                return Path.GetFullPath(path);
            }
            catch (Exception error) when (error is UserFacingException or ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
            {
                // A broken PATH entry must not prevent discovering another installed CLI.
            }
        }
        return null;
    }

    public static RuntimeStatus Inspect()
    {
        if (!OperatingSystem.IsWindows())
            return new RuntimeStatus(false, "此工具仅支持 Windows，无法确认 Codex 运行状态。", []);

        var blocking = new List<string>();
        var uncertain = false;
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or SecurityException)
        {
            return new RuntimeStatus(false, "无法读取进程列表，已拒绝切换。请检查系统权限后重试。", []);
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var name = process.ProcessName;
                    // Other switcher windows are serialized by the named mutex; they are not Codex clients.
                    if (name.Equals("CodexAccountSwitcher", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals(OfficialSandboxService.ImageName,StringComparison.OrdinalIgnoreCase)&&OfficialSandboxService.IsVerifiedDaemon(process))continue;
                    if (IsBlockingProcessName(name)) blocking.Add($"{name} (PID {process.Id})");
                }
                catch (InvalidOperationException)
                {
                    // A process may exit after enumeration. It no longer holds a client session.
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 87 or 1168)
                {
                    // ERROR_INVALID_PARAMETER / ERROR_NOT_FOUND also identify an exited process.
                }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or SecurityException or NotSupportedException)
                {
                    uncertain = true;
                }
            }
        }

        blocking.Sort(StringComparer.OrdinalIgnoreCase);
        if (uncertain)
            return new RuntimeStatus(false, "部分进程状态无法读取，已拒绝切换；请确认权限后重试。", blocking);
        if (blocking.Count != 0)
            return new RuntimeStatus(false, "请先正常退出所有 Codex / ChatGPT 桌面窗口及 Codex 后台，再切换账号。", blocking);
        return new RuntimeStatus(true, "已确认未发现 Codex / ChatGPT 客户端或 Codex 后台进程。", blocking);
    }

    internal static bool IsBlockingProcessName(string name) =>
        name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ChatGPT-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("code-mode-host", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("code-mode-host-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("command-runner", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("codex", StringComparison.OrdinalIgnoreCase);

    public static void EnsureFileAuthStore(string codexHome)
    {
        var directory = AuthFileSafety.NormalizeDirectory(codexHome);
        using var directories = AuthFileSafety.ProtectDirectories(directory);
        using var config = AuthFileSafety.ReadOptional(Path.Combine(directory, "config.toml"), 4 * 1024 * 1024);
        if (config is null) return; // The official default is a file store.

        string contents;
        try { contents = new UTF8Encoding(false, true).GetString(config.Bytes); }
        catch (DecoderFallbackException)
        {
            throw new UserFacingException("config.toml 不是有效的 UTF-8 文件，无法确认认证存储方式。未修改配置。");
        }
        foreach (var sourceLine in contents.Split('\n'))
        {
            var line = StripTomlComment(sourceLine.TrimStart('\uFEFF'));
            if (!Regex.IsMatch(line, @"\bcli_auth_credentials_store\b", RegexOptions.CultureInvariant)) continue;
            var assignment = Regex.Match(line,
                "^\\s*(?:cli_auth_credentials_store|\"cli_auth_credentials_store\"|'cli_auth_credentials_store')\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)')\\s*$",
                RegexOptions.CultureInvariant);
            if (!assignment.Success || !assignment.Groups["value"].Value.Equals("file", StringComparison.Ordinal))
                throw new UserFacingException("此工具仅支持 file 认证存储。配置使用 keyring、auto、ephemeral 或无法可靠识别的写法时，已拒绝切换；现有 auth.json 不能证明它是当前登录来源。");
        }
    }

    private static string StripTomlComment(string line)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote == '\0')
            {
                if (character == '#') return line[..index].Trim();
                if (character is '\'' or '"') quote = character;
            }
            else if (quote == '"' && !escaped && character == '\\') { escaped = true; continue; }
            else if (!escaped && character == quote) quote = '\0';
            escaped = false;
        }
        return line.Trim();
    }

    public static void LaunchDesktop()
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("只能在 Windows 上启动 Codex 桌面版。");
        object? shell = null;
        object? folder = null;
        object? items = null;
        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) throw new UserFacingException("系统未提供应用注册列表，请手动打开 Codex。");
            shell = Activator.CreateInstance(shellType);
            if (shell is null) throw new UserFacingException("无法读取系统应用注册列表，请手动打开 Codex。");
            folder = ((dynamic)shell).Namespace("shell:AppsFolder");
            if (folder is null) throw new UserFacingException("无法读取系统应用注册列表，请手动打开 Codex。");
            items = ((dynamic)folder).Items();
            var count = (int)((dynamic)items).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(index);
                    if (item is null) continue;
                    var name = (string?)((dynamic)item).Name;
                    if (!string.Equals(name, "Codex", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "ChatGPT", StringComparison.OrdinalIgnoreCase)) continue;
                    string? appId = Convert.ToString(((dynamic)item).ExtendedProperty("System.AppUserModel.ID"));
                    if (string.IsNullOrWhiteSpace(appId)) appId = Convert.ToString(((dynamic)item).Path);
                    if (appId is not null &&
                        (appId.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
                         appId.StartsWith("OpenAI.Codex!", StringComparison.OrdinalIgnoreCase) ||
                         appId.StartsWith("com.openai.codex", StringComparison.OrdinalIgnoreCase)) &&
                        !appId.Contains('\\') && !appId.Contains('/') && !appId.Contains('"'))
                    {
                        registeredIds.Add(appId);
                    }
                }
                finally { ReleaseComObject(item); }
            }
            if (registeredIds.Count == 0)
                throw new UserFacingException("没有找到官方 Codex 桌面版的注册启动项，请从开始菜单手动打开 Codex。");
            if (registeredIds.Count != 1)
                throw new UserFacingException("检测到多个 Codex 桌面启动项，请从开始菜单手动选择要打开的版本。");

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = "shell:AppsFolder\\" + registeredIds.Single(),
                UseShellExecute = true
            });
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is COMException or Win32Exception or InvalidOperationException or SecurityException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new UserFacingException("无法通过系统注册项启动 Codex，请从开始菜单手动打开。");
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
