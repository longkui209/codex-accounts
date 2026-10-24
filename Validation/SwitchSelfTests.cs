using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.Validation;

/// <summary>Offline checks that exclusively use generated credentials in a new isolated directory.</summary>
public static class SwitchSelfTests
{
    private static readonly RuntimeStatus Stopped = new(true, "synthetic stopped", []);
    private static readonly RuntimeStatus Busy = new(false, "synthetic running", ["synthetic-codex"]);

    public static IReadOnlyList<string> Run(string outputRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("离线切换自检需要 Windows DPAPI 与文件保护。");
        var reports = new List<string>();
        var validationRoot = Path.Combine(Path.GetFullPath(outputRoot), "switch-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validationRoot);
        FilePrivacy.ApplyDirectory(validationRoot);

        Check(reports, "切换保存当前账号、加密备份且目标文件保持逐字节一致", () =>
        {
            using var data = Scenario.Create(validationRoot, "switch");
            var service = new AccountSwitchService(data.Vault, data.Home, () => Stopped);
            var backup = service.Switch(data.TargetId);
            Require(!string.IsNullOrEmpty(backup), "Missing encrypted backup.");
            EqualFile(data.AuthPath, data.TargetBytes);
            using var saved = data.Vault.Read(data.OriginalId);
            EqualSnapshot(saved, data.OriginalBytes);
            using var recovery = data.Vault.ReadBackup(backup);
            EqualSnapshot(recovery, data.OriginalBytes);
            Require(data.Vault.List().Count == 2, "Original identity was not retained.");
            EqualFile(data.ConfigPath, data.ConfigBytes);
            AssertNoPlaintextBackup(data);
            AssertNoTemporary(data.Home);
        });

        Check(reports, "加密恢复保持原始认证字节并备份恢复前账号", () =>
        {
            using var data = Scenario.Create(validationRoot, "restore");
            var service = new AccountSwitchService(data.Vault, data.Home, () => Stopped);
            var priorBackup = service.Switch(data.TargetId);
            var restoredBackup = service.RestoreLatest();
            Require(restoredBackup == priorBackup, "Restore did not use the pre-switch backup.");
            EqualFile(data.AuthPath, data.OriginalBytes);
            using var newest = data.Vault.ReadBackup(data.Vault.LatestBackupFileName!);
            EqualSnapshot(newest, data.TargetBytes);
            AssertNoPlaintextBackup(data);
            AssertNoTemporary(data.Home);
        });

        Check(reports, "运行中拒绝切换且不创建备份、不覆盖认证", () =>
        {
            using var data = Scenario.Create(validationRoot, "busy");
            var beforeVault = File.ReadAllBytes(Path.Combine(data.Vault.DirectoryPath, "accounts.dpapi"));
            try
            {
                Rejected(() => new AccountSwitchService(data.Vault, data.Home, () => Busy).Switch(data.TargetId));
                EqualFile(data.AuthPath, data.OriginalBytes);
                EqualFile(Path.Combine(data.Vault.DirectoryPath, "accounts.dpapi"), beforeVault);
                Require(data.Vault.LatestBackupFileName is null, "Busy check created a backup.");
                AssertNoTemporary(data.Home);
            }
            finally { CryptographicOperations.ZeroMemory(beforeVault); }
        });

        Check(reports, "写入前二次运行检查拒绝中途启动的客户端", () =>
        {
            using var data = Scenario.Create(validationRoot, "second-guard");
            var calls = 0;
            var service = new AccountSwitchService(data.Vault, data.Home, () => ++calls == 1 ? Stopped : Busy);
            Rejected(() => service.Switch(data.TargetId));
            Require(calls == 2, "Second runtime guard was not invoked.");
            EqualFile(data.AuthPath, data.OriginalBytes);
            AssertNoTemporary(data.Home);
        });

        Check(reports, "不能确认进程状态时拒绝切换", () =>
        {
            using var data = Scenario.Create(validationRoot, "guard-error");
            var service = new AccountSwitchService(data.Vault, data.Home,
                () => throw new UnauthorizedAccessException("synthetic unreadable process"));
            Rejected(() => service.Switch(data.TargetId));
            EqualFile(data.AuthPath, data.OriginalBytes);
            AssertNoTemporary(data.Home);
        });

        Check(reports, "只读认证拒绝切换且保留文件属性", () =>
        {
            using var data = Scenario.Create(validationRoot, "readonly");
            File.SetAttributes(data.AuthPath, File.GetAttributes(data.AuthPath) | FileAttributes.ReadOnly);
            try
            {
                Rejected(() => new AccountSwitchService(data.Vault, data.Home, () => Stopped).Switch(data.TargetId));
                EqualFile(data.AuthPath, data.OriginalBytes);
                Require((File.GetAttributes(data.AuthPath) & FileAttributes.ReadOnly) != 0, "Read-only flag was changed.");
                AssertNoTemporary(data.Home);
            }
            finally { File.SetAttributes(data.AuthPath, FileAttributes.Normal); }
        });

        Check(reports, "检测中途同文件写入并保留并发账号", () =>
        {
            using var data = Scenario.Create(validationRoot, "concurrent-edit");
            var concurrent = MakeAuth("concurrent", "fresh");
            try
            {
                var calls = 0;
                var service = new AccountSwitchService(data.Vault, data.Home, () =>
                {
                    if (++calls == 2) File.WriteAllBytes(data.AuthPath, concurrent);
                    return Stopped;
                });
                Rejected(() => service.Switch(data.TargetId));
                EqualFile(data.AuthPath, concurrent);
                using var previous = data.Vault.ReadBackup(data.Vault.LatestBackupFileName!);
                EqualSnapshot(previous, data.OriginalBytes);
                AssertNoTemporary(data.Home);
            }
            finally { CryptographicOperations.ZeroMemory(concurrent); }
        });

        Check(reports, "检测中途原子文件替换并保留并发账号", () =>
        {
            using var data = Scenario.Create(validationRoot, "concurrent-replace");
            var concurrent = MakeAuth("concurrent-replacement", "fresh");
            try
            {
                var calls = 0;
                var service = new AccountSwitchService(data.Vault, data.Home, () =>
                {
                    if (++calls == 2)
                    {
                        var replacement = Path.Combine(data.Home, "synthetic-other-login.tmp");
                        File.WriteAllBytes(replacement, concurrent);
                        File.Replace(replacement, data.AuthPath, null);
                    }
                    return Stopped;
                });
                Rejected(() => service.Switch(data.TargetId));
                EqualFile(data.AuthPath, concurrent);
                AssertNoTemporary(data.Home);
            }
            finally { CryptographicOperations.ZeroMemory(concurrent); }
        });

        Check(reports, "初始无认证时不覆盖中途出现的新登录", () =>
        {
            using var data = Scenario.Create(validationRoot, "new-concurrent-file");
            File.Delete(data.AuthPath); // Only the newly generated synthetic file in this scenario.
            var concurrent = MakeAuth("concurrent-new", "fresh");
            try
            {
                var calls = 0;
                var service = new AccountSwitchService(data.Vault, data.Home, () =>
                {
                    if (++calls == 2) File.WriteAllBytes(data.AuthPath, concurrent);
                    return Stopped;
                });
                Rejected(() => service.Switch(data.TargetId));
                EqualFile(data.AuthPath, concurrent);
                AssertNoTemporary(data.Home);
            }
            finally { CryptographicOperations.ZeroMemory(concurrent); }
        });

        Check(reports, "同账号旧快照不会覆盖当前已轮换凭据", () =>
        {
            using var data = Scenario.Create(validationRoot, "same-identity");
            var fresh = MakeAuth("target", "rotated-current");
            try
            {
                File.WriteAllBytes(data.AuthPath, fresh);
                var service = new AccountSwitchService(data.Vault, data.Home, () => Stopped);
                Require(service.Switch(data.TargetId) == "", "Same account should have been a no-op.");
                EqualFile(data.AuthPath, fresh);
                using var refreshed = data.Vault.Read(data.TargetId);
                EqualSnapshot(refreshed, fresh);
                Require(data.Vault.LatestBackupFileName is null, "Same-account no-op created an unnecessary backup.");
                AssertNoTemporary(data.Home);
            }
            finally { CryptographicOperations.ZeroMemory(fresh); }
        });

        Check(reports, "认证存储 keyring / auto / ephemeral 拒绝文件切换", () =>
        {
            foreach (var mode in new[] { "keyring", "auto", "ephemeral" })
            {
                using var data = Scenario.Create(validationRoot, "store-" + mode);
                File.WriteAllText(data.ConfigPath, "cli_auth_credentials_store = \"" + mode + "\"\n", new UTF8Encoding(false));
                Rejected(() => new AccountSwitchService(data.Vault, data.Home, () => Stopped).Switch(data.TargetId));
                EqualFile(data.AuthPath, data.OriginalBytes);
                AssertNoTemporary(data.Home);
            }
        });

        Check(reports, "认证配置在切换过程中更改时拒绝写入", () =>
        {
            using var data = Scenario.Create(validationRoot, "config-race");
            var calls = 0;
            var service = new AccountSwitchService(data.Vault, data.Home, () =>
            {
                if (++calls == 2) File.WriteAllText(data.ConfigPath, "cli_auth_credentials_store = \"auto\"\n", new UTF8Encoding(false));
                return Stopped;
            });
            Rejected(() => service.Switch(data.TargetId));
            EqualFile(data.AuthPath, data.OriginalBytes);
            Require(File.ReadAllText(data.ConfigPath).Contains("auto", StringComparison.Ordinal), "Concurrent config change was overwritten.");
            AssertNoTemporary(data.Home);
        });

        Check(reports, "无当前认证时安全创建目标缓存且不编造恢复备份", () =>
        {
            using var data = Scenario.Create(validationRoot, "first-login");
            File.Delete(data.AuthPath);
            Require(new AccountSwitchService(data.Vault, data.Home, () => Stopped).Switch(data.TargetId) == "", "Absent auth should not generate a fictitious backup.");
            EqualFile(data.AuthPath, data.TargetBytes);
            AssertNoTemporary(data.Home);
        });

        Check(reports, "空白或损坏的现有认证拒绝覆盖", () =>
        {
            using var data = Scenario.Create(validationRoot, "invalid-existing");
            File.WriteAllBytes(data.AuthPath, []);
            Rejected(() => new AccountSwitchService(data.Vault, data.Home, () => Stopped).Switch(data.TargetId));
            EqualFile(data.AuthPath, []);
            AssertNoTemporary(data.Home);
        });

        Check(reports, "运行阻塞名称覆盖桌面、后台和 code-mode-host", () =>
        {
            foreach (var name in new[] { "Codex", "ChatGPT", "codex-app-server", "codex-command-runner", "codex-windows-sandbox", "code-mode-host" })
                Require(CodexRuntime.IsBlockingProcessName(name), "Expected blocking process name was omitted.");
            Require(!CodexRuntime.IsBlockingProcessName("notepad"), "Unrelated program blocks switching.");
        });

        Check(reports, "长路径下切换与恢复仍保留逐字节认证、加密备份及临时文件保护", () =>
        {
            var longRoot = validationRoot;
            while (longRoot.Length < 280) longRoot = Path.Combine(longRoot, "synthetic-segment-" + new string('p', 40));
            using var data = Scenario.Create(longRoot, "long-path");
            Require(data.AuthPath.Length > 260, "Fixture did not exceed the legacy path limit.");
            var service = new AccountSwitchService(data.Vault, data.Home, () => Stopped);
            var backup = service.Switch(data.TargetId);
            EqualFile(data.AuthPath, data.TargetBytes);
            using (var recovery = data.Vault.ReadBackup(backup)) EqualSnapshot(recovery, data.OriginalBytes);
            Require(service.RestoreLatest() == backup, "Long-path recovery used the wrong backup.");
            EqualFile(data.AuthPath, data.OriginalBytes);
            EqualFile(data.ConfigPath, data.ConfigBytes);
            AssertNoPlaintextBackup(data);
            AssertNoTemporary(data.Home);
        });

        CheckReparseProtection(reports, validationRoot);
        return reports;
    }

    private static void CheckReparseProtection(List<string> reports, string root)
    {
        using var data = Scenario.Create(root, "reparse-file");
        var generatedTarget = Path.Combine(data.Home, "synthetic-external-auth.json");
        File.Move(data.AuthPath, generatedTarget);
        try { File.CreateSymbolicLink(data.AuthPath, generatedTarget); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            File.Move(generatedTarget, data.AuthPath);
            reports.Add("SKIP: 认证 reparse point 检查（此 Windows 环境不允许创建测试符号链接）");
            return;
        }
        Check(reports, "拒绝认证文件 reparse point 且不修改链接目标", () =>
        {
            Rejected(() => new AccountSwitchService(data.Vault, data.Home, () => Stopped).Switch(data.TargetId));
            EqualFile(generatedTarget, data.OriginalBytes);
            Require((File.GetAttributes(data.AuthPath) & FileAttributes.ReparsePoint) != 0, "Synthetic link was removed.");
            AssertNoTemporary(data.Home);
        });
    }

    private static void Check(List<string> reports, string name, Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Raw exception messages and stack traces must never be copied into credential reports.
            var reason = exception is UserFacingException safe ? safe.Message : "未输出原始异常内容。";
            throw new UserFacingException("离线合成切换验证未通过：" + name + "。错误类型：" + exception.GetType().Name + "；" + reason);
        }
        reports.Add("PASS: " + name);
    }

    private static void Rejected(Action action)
    {
        try { action(); }
        catch (UserFacingException) { return; }
        throw new InvalidOperationException("Expected a fixed user-facing rejection.");
    }

    private static void EqualFile(string file, byte[] expected)
    {
        var actual = File.ReadAllBytes(file);
        try { Require(CryptographicOperations.FixedTimeEquals(expected, actual), "File did not retain exact bytes."); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    private static void EqualSnapshot(AuthSnapshot snapshot, byte[] expected)
    {
        var actual = snapshot.CopyBytes();
        try { Require(CryptographicOperations.FixedTimeEquals(expected, actual), "Snapshot did not retain exact bytes."); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    private static void AssertNoTemporary(string home) =>
        Require(!Directory.EnumerateFiles(home, ".auth-*.tmp").Any(), "Temporary authentication material remained.");

    private static void AssertNoPlaintextBackup(Scenario data)
    {
        foreach (var file in Directory.EnumerateFiles(data.Vault.DirectoryPath))
        {
            Require(Path.GetExtension(file) == ".dpapi", "Unexpected plaintext vault sidecar.");
            var bytes = File.ReadAllBytes(file);
            try
            {
                Require(bytes.AsSpan().IndexOf(data.OriginalBytes) < 0 && bytes.AsSpan().IndexOf(data.TargetBytes) < 0,
                    "Credential bytes were present in a protected file.");
                Require(!Encoding.UTF8.GetString(bytes).Contains("synthetic-refresh", StringComparison.Ordinal),
                    "A synthetic token marker appeared in plaintext.");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private static void Require(bool condition, string internalMessage)
    {
        if (!condition) throw new InvalidOperationException(internalMessage);
    }

    private static byte[] MakeAuth(string identity, string generation)
    {
        static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = "synthetic-sub-" + identity,
            ["email"] = identity + "@synthetic.invalid",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new Dictionary<string, object> { ["chatgpt_plan_type"] = "plus" }
        });
        try
        {
            // These unsigned synthetic strings are accepted only by the offline format parser.
            var token = "eyJhbGciOiJub25lIn0." + Base64Url(claims) + ".c3ludGhldGlj";
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                auth_mode = "chatgpt",
                OPENAI_API_KEY = (string?)null,
                tokens = new
                {
                    access_token = token,
                    id_token = token,
                    refresh_token = "synthetic-refresh-" + generation,
                    account_id = "synthetic-account-" + identity
                },
                last_refresh = "2026-01-01T00:00:00Z"
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        finally { CryptographicOperations.ZeroMemory(claims); }
    }

    private sealed class Scenario : IDisposable
    {
        public required string Home { get; init; }
        public required VaultStore Vault { get; init; }
        public required byte[] OriginalBytes { get; init; }
        public required byte[] TargetBytes { get; init; }
        public required byte[] ConfigBytes { get; init; }
        public required string OriginalId { get; init; }
        public required string TargetId { get; init; }
        public string AuthPath => Path.Combine(Home, "auth.json");
        public string ConfigPath => Path.Combine(Home, "config.toml");

        public static Scenario Create(string root, string name)
        {
            var home = Path.Combine(root, name, "isolated-codex-home");
            Directory.CreateDirectory(home);
            FilePrivacy.ApplyDirectory(home);
            var vault = new VaultStore(Path.Combine(root, name, "encrypted-vault"));
            var originalBytes = MakeAuth("original", "original");
            var targetBytes = MakeAuth("target", "target");
            var configBytes = Encoding.UTF8.GetBytes("# synthetic validation only\ncli_auth_credentials_store = \"file\"\n");
            using var original = AuthSnapshot.FromBytes(originalBytes);
            using var target = AuthSnapshot.FromBytes(targetBytes);
            var targetSummary = vault.Save(target, "Synthetic target");
            File.WriteAllBytes(Path.Combine(home, "auth.json"), originalBytes);
            File.WriteAllBytes(Path.Combine(home, "config.toml"), configBytes);
            return new Scenario
            {
                Home = home, Vault = vault, OriginalBytes = originalBytes, TargetBytes = targetBytes,
                ConfigBytes = configBytes, OriginalId = original.Info.IdentityKey, TargetId = targetSummary.Id
            };
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(OriginalBytes);
            CryptographicOperations.ZeroMemory(TargetBytes);
            // Synthetic test artifacts remain for the parent verifier to inspect; they are never real login data.
        }
    }
}
