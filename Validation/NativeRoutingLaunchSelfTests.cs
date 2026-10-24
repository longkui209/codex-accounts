using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

/// <summary>Pure startup construction only: no desktop/CLI launch, installation lookup or network.</summary>
public static class NativeRoutingLaunchSelfTests
{
    private const string FixtureExecutable = @"C:\synthetic-native-routing\app\ChatGPT.exe";
    private const string RelayVariable = "CODEX_APP_SERVER_OPENAI_BASE_URL";

    public static IReadOnlyList<string> Run()
    {
        var reports = new List<string>();
        var inherited = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CODEX_WINDOWS_REGISTERED_CORE"] = "synthetic-core-selection",
            ["CODEX_WINDOWS_SANDBOX_PACKAGE_FAMILY"] = "synthetic-official-package",
            ["CODEX_APP_TOOLS_PIPE_PATH"] = @"\\.\pipe\synthetic-app-tools",
            ["CODEX_TASK_WORKSPACE_VERIFYING_IDENTITY"] = "synthetic-verification",
            ["CODEX_MCP_NODE_PATH"] = @"C:\synthetic-native-routing\node.exe",
            ["CODEX_HOME"] = @"C:\synthetic-native-routing\home",
            ["HTTPS_PROXY"] = "http://proxy.example.test:8080",
            ["NO_PROXY"] = "127.0.0.1,localhost",
            ["OPENAI_API_KEY"] = "synthetic-inherited-value",
            [RelayVariable] = "https://synthetic-previous-base.example.test"
        };
        var snapshot = inherited.ToArray();
        var relay = new Uri("http://127.0.0.1:49152/backend-api/codex");
        var start = NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, relay, inherited);
        Require(start.FileName == FixtureExecutable && !start.UseShellExecute &&
                start.ArgumentList.Count == 0 && string.IsNullOrEmpty(start.Arguments) &&
                start.WorkingDirectory == Path.GetDirectoryName(FixtureExecutable), "仅构造原生桌面启动参数");
        Require(start.Environment[RelayVariable] == relay.AbsoluteUri, "设置指定本地推理地址");
        Require(snapshot.Where(entry => entry.Key != RelayVariable)
                .All(entry => start.Environment.TryGetValue(entry.Key, out var value) && value == entry.Value),
            "Core、AppTools、验证、代理与其余子环境原样继承");
        Require(start.Environment.Count == inherited.Count &&
                snapshot.All(entry => inherited[entry.Key] == entry.Value), "只有一项子环境变更且不修改输入");
        reports.Add("PASS: 原生桌面参数构造只替换子环境推理地址，保留 Core、AppTools、验证及代理。");

        foreach (var previous in new string?[] { null, "", "example.test", "example.test,", "localhost", "127.0.0.1,example.test", "*" })
        {
            var proxyEnvironment = new Dictionary<string, string?>
            {
                ["HTTP_PROXY"] = "http://synthetic-proxy.example.test:7890",
                ["HTTPS_PROXY"] = "http://synthetic-proxy.example.test:7890",
                ["ALL_PROXY"] = "socks5://synthetic-proxy.example.test:7891"
            };
            if (previous is not null) proxyEnvironment["NO_PROXY"] = previous;
            var proxySnapshot = proxyEnvironment.ToArray();
            var direct = NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, relay, proxyEnvironment);
            var bypasses = direct.Environment["NO_PROXY"]!.Split(',').Select(value => value.Trim());
            Require(bypasses.Contains("127.0.0.1") || bypasses.Contains("*"), "本机连接始终绕过子进程代理");
            Require(proxySnapshot.All(entry => proxyEnvironment[entry.Key] == entry.Value), "不修改输入或全局代理");
            Require(proxyEnvironment.Where(entry => entry.Key != "NO_PROXY").All(entry => direct.Environment[entry.Key] == entry.Value),
                "保留远程代理地址与其他子环境");
            if (!string.IsNullOrWhiteSpace(previous)) Require(direct.Environment["NO_PROXY"]!.StartsWith(previous, StringComparison.Ordinal),
                "保留已有绕过项");
        }
        reports.Add("PASS: 缺少 NO_PROXY 时仅为新桌面补充 127.0.0.1，原有绕过项、远程代理与输入环境保持不变。");

        foreach (var port in new[] { 1025, 65535 })
        {
            var address = new Uri($"http://127.0.0.1:{port}/backend-api/codex");
            var boundary = NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, address, []);
            Require(boundary.Environment[RelayVariable] == address.AbsoluteUri, "允许范围内端口边界");
        }
        foreach (var invalid in new[]
        {
            "https://127.0.0.1:49152/backend-api/codex",
            "http://localhost:49152/backend-api/codex",
            "http://192.0.2.1:49152/backend-api/codex",
            "http://[::1]:49152/backend-api/codex",
            "http://127.0.0.1/backend-api/codex",
            "http://127.0.0.1:1024/backend-api/codex",
            "http://127.0.0.1:49152/backend-api/codex/",
            "http://127.0.0.1:49152/v1",
            "http://synthetic-private-user:synthetic-private-secret@127.0.0.1:49152/backend-api/codex",
            "http://127.0.0.1:49152/backend-api/codex?synthetic-private-secret",
            "http://127.0.0.1:49152/backend-api/codex#synthetic-private-secret",
            "http://127.0.0.1:49152/other/../backend-api/codex",
            "http://2130706433:49152/backend-api/codex",
            "http://127.0.0.1:49152/backend-api/%63odex"
        })
            Rejected(() => NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, new Uri(invalid), []),
                "非法推理地址", "synthetic-private-secret");
        Rejected(() => NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, new Uri("relative", UriKind.Relative), []),
            "相对地址");
        Rejected(() => NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, null!, []), "缺少地址");
        reports.Add("PASS: 端口边界通过，非规范回环地址、协议、路径、用户信息和查询片段均固定文案拒绝。");

        foreach (var name in new[] { "CODEX_CLI_PATH", "CODEX_APP_SERVER_WS_URL", "CODEX_APP_SERVER_FORCE_CLI" })
        {
            foreach (var value in new[] { "synthetic-private-override", " " })
            {
                var conflict = new Dictionary<string, string?> { [name] = value };
                Rejected(() => NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, relay, conflict),
                    "已有启动覆盖设置", "synthetic-private-override");
                Require(conflict[name] == value, "拒绝时不得删改既有覆盖设置");
            }
            var differentCase = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [name.ToLowerInvariant()] = "synthetic-private-override",
                [name] = ""
            };
            Rejected(() => NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, relay, differentCase),
                "不区分大小写且不得空值遮蔽冲突", "synthetic-private-override");
        }
        var emptyOverrides = new Dictionary<string, string?>
        {
            ["CODEX_CLI_PATH"] = "", ["CODEX_APP_SERVER_WS_URL"] = "", ["CODEX_APP_SERVER_FORCE_CLI"] = null
        };
        var empty = NativeRoutingLaunch.BuildStartInfo(FixtureExecutable, relay, emptyOverrides);
        Require(emptyOverrides.All(entry => empty.Environment.TryGetValue(entry.Key, out var value) && value == entry.Value),
            "空覆盖值保持原样而不是删除");
        reports.Add("PASS: 三种启动覆盖非空即拒绝，大小写和重复键不能绕过，不删除或修改输入环境。");

        foreach (var invalid in new[] { "", "ChatGPT.exe", @"C:\synthetic-native-routing\codex.exe", "\0" })
            Rejected(() => NativeRoutingLaunch.BuildStartInfo(invalid, relay, []), "非法桌面路径");
        reports.Add("PASS: 纯参数验证拒绝非法桌面路径；整个验证未启动应用、读取认证或检查真实安装。");
        return reports;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new UserFacingException("隔离原生启动参数验证失败：" + name + "。");
    }

    private static void Rejected(Action action, string name, string forbidden = "")
    {
        try { action(); }
        catch (UserFacingException error)
        {
            Require(forbidden.Length == 0 || !error.Message.Contains(forbidden, StringComparison.Ordinal),
                "固定错误不得暴露传入地址或环境值");
            return;
        }
        throw new UserFacingException("隔离原生启动参数验证失败：" + name + "被错误接受。");
    }
}
