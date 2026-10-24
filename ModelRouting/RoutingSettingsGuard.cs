using System.Text;
using System.Text.RegularExpressions;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.ModelRouting;

internal static class RoutingSettingsGuard
{
    public static void AssertNoExistingEndpoint(string home)
    {
        using var directories = AuthFileSafety.ProtectDirectories(home);
        using var file = AuthFileSafety.ReadOptional(Path.Combine(home, "config.toml"), 4 * 1024 * 1024);
        if (file is null) return;
        var text = new UTF8Encoding(false, true).GetString(file.Bytes);
        // Fail closed rather than replacing an existing gateway or workspace bootstrap.
        // No config text or endpoint value is included in errors or output.
        if (Regex.IsMatch(text, @"(?m)^\s*(?:openai_base_url|chatgpt_base_url)\s*=", RegexOptions.CultureInvariant))
            throw new UserFacingException("已有自定义模型或工作区请求地址。为保留现有连接，本工具不会覆盖，请继续使用普通 Codex 入口。");
        if (Regex.IsMatch(text, @"\buse_agent_identity\s*=\s*true\b", RegexOptions.CultureInvariant))
            throw new UserFacingException("已启用实验性签名身份鉴权，此模式尚不支持订阅转发。请使用普通 Codex 入口；本工具不会关闭该功能。");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_APP_SERVER_OPENAI_BASE_URL")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_APP_SERVER_CHATGPT_BASE_URL")))
            throw new UserFacingException("已有自定义请求地址环境设置。本工具不会覆盖，请继续使用普通 Codex 入口。");
    }
}
