using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Starts the signed desktop with its native backend and a child-scoped inference URL.</summary>
public static class NativeRoutingLaunch
{
    private const string RelayPath = "/backend-api/codex";
    private const string RelayEnvironmentVariable = "CODEX_APP_SERVER_OPENAI_BASE_URL";
    private static readonly string[] ConflictingOverrides =
    [
        "CODEX_CLI_PATH", "CODEX_APP_SERVER_WS_URL", "CODEX_APP_SERVER_FORCE_CLI"
    ];

    private const string InvalidRelayMessage = "本地推理连接地址无效，未启动 Codex。";
    private const string InvalidExecutableMessage = "官方桌面启动路径无效，未启动 Codex。";
    private const string ConflictMessage = "检测到已有后台启动覆盖设置，未启动 Codex；请先在普通入口恢复默认启动配置。";

    public static Process Launch(Uri relayBaseUri)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("本地推理账号路由入口仅支持 Windows。");

        _ = ValidateRelayAddress(relayBaseUri);
        var executable = DesktopLaunch.FindInstalledDesktop();
        var start = BuildStartInfo(executable, relayBaseUri);
        try
        {
            return Process.Start(start) ?? throw new UserFacingException("未能启动官方 Codex 桌面版。");
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or
                                      IOException or SecurityException)
        {
            throw new UserFacingException("官方 Codex 桌面启动失败；普通入口和登录缓存保持原样。");
        }
    }

    /// <summary>Constructs startup parameters without reading installation or authentication files.</summary>
    public static ProcessStartInfo BuildStartInfo(string executable, Uri relayBaseUri)
    {
        var start = CreateStartInfo(executable);
        var address = ValidateRelayAddress(relayBaseUri);
        ValidateOverrides(start.Environment);
        start.Environment[RelayEnvironmentVariable] = address;
        EnsureLoopbackBypass(start.Environment);
        return start;
    }

    // Tests supply an entirely synthetic inherited environment. This never changes process/user env.
    internal static ProcessStartInfo BuildStartInfo(string executable, Uri relayBaseUri,
        IEnumerable<KeyValuePair<string, string?>> inheritedEnvironment)
    {
        ArgumentNullException.ThrowIfNull(inheritedEnvironment);
        var start = CreateStartInfo(executable);
        var address = ValidateRelayAddress(relayBaseUri);
        var entries = inheritedEnvironment.ToArray();
        ValidateOverrides(entries);
        start.Environment.Clear();
        foreach (var entry in entries) start.Environment[entry.Key] = entry.Value;
        start.Environment[RelayEnvironmentVariable] = address;
        EnsureLoopbackBypass(start.Environment);
        return start;
    }

    // The native HTTP client otherwise sends loopback inference through an inherited proxy.
    // That changes the TCP peer to the proxy process and correctly fails our caller check.
    // Only this new desktop's environment changes; remote proxy settings remain untouched.
    private static void EnsureLoopbackBypass(IDictionary<string, string?> environment)
    {
        environment.TryGetValue("NO_PROXY", out var existing);
        if (existing?.Split(',').Any(value => value.Trim() is "127.0.0.1" or "*") == true) return;
        environment["NO_PROXY"] = string.IsNullOrWhiteSpace(existing)
            ? "127.0.0.1"
            : existing + (existing.EndsWith(',') ? "" : ",") + "127.0.0.1";
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
    {
        try
        {
            if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) ||
                !Path.GetFileName(executable).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException(InvalidExecutableMessage);
            return new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                CreateNoWindow = false
            };
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new UserFacingException(InvalidExecutableMessage);
        }
    }

    private static string ValidateRelayAddress(Uri relayBaseUri)
    {
        if (relayBaseUri is null || !relayBaseUri.IsAbsoluteUri ||
            relayBaseUri.Scheme != Uri.UriSchemeHttp || relayBaseUri.Host != "127.0.0.1" ||
            relayBaseUri.IsDefaultPort || relayBaseUri.Port <= 1024 ||
            relayBaseUri.UserInfo.Length != 0 || relayBaseUri.Query.Length != 0 ||
            relayBaseUri.Fragment.Length != 0 || relayBaseUri.AbsolutePath != RelayPath)
            throw new UserFacingException(InvalidRelayMessage);

        var address = $"http://127.0.0.1:{relayBaseUri.Port}{RelayPath}";
        // Reject alternate IP spellings, escaped paths and dot segments instead of silently normalizing.
        if (!relayBaseUri.OriginalString.Equals(address, StringComparison.Ordinal))
            throw new UserFacingException(InvalidRelayMessage);
        return address;
    }

    private static void ValidateOverrides(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        foreach (var entry in environment)
            if (!string.IsNullOrEmpty(entry.Value) &&
                ConflictingOverrides.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                throw new UserFacingException(ConflictMessage);
    }
}
