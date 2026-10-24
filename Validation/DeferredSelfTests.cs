using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.Validation;

/// <summary>Only fresh synthetic homes are used. No real CLI, client, worker process or account is touched.</summary>
public static class DeferredSelfTests
{
    private static readonly RuntimeStatus Closed = new(true, "合成客户端已关闭。", Array.Empty<string>());
    private static readonly RuntimeStatus Busy = new(false, "合成客户端运行中。", Array.Empty<string>());

    public static IReadOnlyList<string> Run(string outputRoot)
    {
        var root = Path.Combine(Path.GetFullPath(outputRoot), "deferred-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FilePrivacy.ApplyDirectory(root);
        var reports = new List<string>();

        using (var data = Scenario.Create(root, "wait-complete"))
        {
            var checks = 0;
            var launches = 0;
            var starts = 0;
            var service = new DeferredSwitchService(data.Vault, data.Home,
                () => ++checks == 1 ? Busy : Closed, () => launches++, _ => starts++);
            var request = service.Request(data.TargetId);
            Require(request.State == "pending" && starts == 1, "等待请求创建");
            RequireUnchanged(data);
            Rejected(() => service.Request(data.TargetId));
            Require(service.Status()?.Id == request.Id, "有待执行请求时禁止覆盖");
            Require(service.RunWorkerAsync(Guid.NewGuid().ToString("N"), CancellationToken.None).GetAwaiter().GetResult() == 2,
                "旧请求标识不能执行新请求");
            RequireUnchanged(data);
            var code = service.RunWorkerAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult();
            Require(code == 0 && service.Status()?.State == "completed" && checks >= 4 && launches == 1,
                "等待运行状态连续变为关闭后切换");
            EqualFile(data.AuthPath, data.Target);
            using var backup = data.Vault.ReadBackup(data.Vault.LatestBackupFileName!);
            var restored = backup.CopyBytes();
            try { Require(CryptographicOperations.FixedTimeEquals(restored, data.Original), "原合成缓存加密备份"); }
            finally { CryptographicOperations.ZeroMemory(restored); }
            var metadata = File.ReadAllBytes(Path.Combine(data.Vault.DirectoryPath, "deferred-switch.dpapi"));
            var marker = Encoding.UTF8.GetBytes(request.Id);
            try { Require(metadata.AsSpan().IndexOf(marker) < 0, "延迟请求索引 DPAPI 加密"); }
            finally { CryptographicOperations.ZeroMemory(metadata); CryptographicOperations.ZeroMemory(marker); }
            Require(!service.Cancel(), "完成后的取消不会改写结果");
            Require(!service.RecordWorkerFailure(request.Id, "worker-exited", 0) && service.Status()?.State == "completed",
                "退出观察不能将完成请求改写为失败");
            reports.Add("PASS: 加密请求、禁止覆盖、旧 ID 拒绝、等待退出、连续两次确认、合成切换与恢复备份。");
        }

        using (var data = Scenario.Create(root, "cancel"))
        {
            var guards = 0;
            var service = new DeferredSwitchService(data.Vault, data.Home, () => { guards++; return Closed; },
                () => throw new InvalidOperationException(), _ => { });
            var request = service.Request(data.TargetId);
            Require(service.Cancel() && !service.Cancel(), "取消请求与重复取消");
            Require(service.RunWorkerAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult() == 2 &&
                    service.Status()?.State == "cancelled" && guards == 0, "取消后即使 guard=true 也拒绝切换");
            RequireUnchanged(data);
            var next = service.Request(data.TargetId, false);
            Require(service.RunWorkerAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult() == 2 &&
                    service.Status()?.Id == next.Id, "旧 worker 不能改写新请求");
            service.Cancel();
            reports.Add("PASS: 取消后拒绝写入、重复取消安全、旧 worker 不处理新的请求。");
        }

        using (var data = Scenario.Create(root, "expiry"))
        {
            var clock = new ManualClock();
            var guards = 0;
            var service = new DeferredSwitchService(data.Vault, data.Home, () => { guards++; return Closed; },
                () => throw new InvalidOperationException(), _ => { }, clock);
            var request = service.Request(data.TargetId, false);
            clock.Advance(TimeSpan.FromMinutes(11));
            Require(service.Status()?.State == "expired", "十分钟请求过期");
            Require(service.RunWorkerAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult() == 2 && guards == 0,
                "过期请求禁止切换");
            RequireUnchanged(data);
            reports.Add("PASS: 注入时钟验证十分钟过期，过期 worker 不触碰认证缓存。");
        }

        using (var data = Scenario.Create(root, "interrupted-close"))
        {
            var clock = new ManualClock();
            var checks = 0;
            var service = new DeferredSwitchService(data.Vault, data.Home, () =>
            {
                if (++checks == 1) return Closed;
                clock.Advance(TimeSpan.FromMinutes(11));
                return Busy;
            }, () => throw new InvalidOperationException(), _ => { }, clock);
            var request = service.Request(data.TargetId, false);
            Require(service.RunWorkerAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult() == 2 &&
                    service.Status()?.State == "expired", "一次关闭检测不满足提交条件");
            RequireUnchanged(data);
            reports.Add("PASS: 客户端重新出现时拒绝提交，仅一次关闭检测不会触发切换。");
        }

        using (var data = Scenario.Create(root, "safe-failure"))
        {
            var service = new DeferredSwitchService(data.Vault, data.Home,
                () => throw new InvalidOperationException("SYNTHETIC-PRIVATE-DIAGNOSTIC"),
                () => throw new InvalidOperationException(), _ => { });
            var request = service.Request(data.TargetId, false);
            Require(service.RunWorkerAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult() == 1,
                "运行状态无法确认时失败");
            var failed = service.Status();
            Require(failed?.State == "failed" && !failed.Message.Contains("SYNTHETIC-PRIVATE-DIAGNOSTIC", StringComparison.Ordinal),
                "失败信息不传播原始异常");
            RequireUnchanged(data);
            var startup = new DeferredSwitchService(data.Vault, data.Home, () => Closed, () => { },
                _ => throw new InvalidOperationException("SYNTHETIC-PRIVATE-STARTUP"));
            var rejected = startup.Request(data.TargetId, false);
            Require(rejected.State == "failed" && !rejected.Message.Contains("SYNTHETIC-PRIVATE-STARTUP", StringComparison.Ordinal),
                "后台未能启动时不伪造成功");
            RequireUnchanged(data);
            reports.Add("PASS: guard 失败及后台启动失败均拒绝写入，并使用固定脱敏错误。");
        }

        using (var data = Scenario.Create(root, "fixed-worker-diagnostics"))
        {
            var service = new DeferredSwitchService(data.Vault, data.Home, () => Busy, () => { }, _ => { });
            var request = service.Request(data.TargetId, false);
            Require(!service.RecordWorkerFailure(request.Id, "SYNTHETIC-RAW-ERROR", 7), "原始错误不能成为阶段码");
            Require(service.Status()?.State == "pending", "非法阶段不改写请求");
            Require(!service.RecordWorkerFailure(Guid.NewGuid().ToString("N"), "startup-failed", 7), "旧请求不登记失败");
            Require(service.RecordWorkerFailure(request.Id, "startup-failed", 7), "固定阶段失败登记");
            var health = service.InspectWorkerHealth();
            Require(service.Status()?.State == "failed" && health?.Stage == "startup-failed" && health.ExitCode == 7 && !health.Ready,
                "安全阶段及退出码独立保存");
            RequireUnchanged(data);
            reports.Add("PASS: 仅固定阶段码、请求 ID 匹配、退出码保存，失败不写合成认证缓存。");
        }

        using (var data = Scenario.Create(root, "legacy-metadata"))
        {
            var service = new DeferredSwitchService(data.Vault, data.Home, () => Closed, () => { }, _ => { });
            var request = service.Request(data.TargetId, false);
            var metadataPath = Path.Combine(data.Vault.DirectoryPath, "deferred-switch.dpapi");
            var encrypted = File.ReadAllBytes(metadataPath);
            byte[]? plaintext = null;
            byte[]? legacyPlaintext = null;
            byte[]? legacyEncrypted = null;
            try
            {
                plaintext = SecretProtection.Unprotect(encrypted);
                var metadata = JsonNode.Parse(plaintext)!.AsObject();
                foreach (var property in new[] { "requiresWorkerReadyConfirmation", "workerReadyAtUtc", "workerReadyConfirmedAtUtc", "workerStage", "lastExitCode" })
                    metadata.Remove(property);
                legacyPlaintext = JsonSerializer.SerializeToUtf8Bytes(metadata);
                legacyEncrypted = SecretProtection.Protect(legacyPlaintext);
                File.WriteAllBytes(metadataPath, legacyEncrypted);
                FilePrivacy.ApplyFile(metadataPath);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                if (legacyPlaintext is not null) CryptographicOperations.ZeroMemory(legacyPlaintext);
                if (legacyEncrypted is not null) CryptographicOperations.ZeroMemory(legacyEncrypted);
            }
            Require(service.Status()?.Id == request.Id && service.InspectWorkerHealth()?.Stage == "legacy", "旧元数据无新增字段可读");
            Require(service.Cancel() && service.Status()?.State == "cancelled", "旧待切换请求可正常取消");
            RequireUnchanged(data);
            reports.Add("PASS: 旧 DPAPI 请求缺少新增准备字段仍可读取并取消，没有重放旧请求。");
        }
        return reports;
    }

    private static void RequireUnchanged(Scenario data)
    {
        EqualFile(data.AuthPath, data.Original);
        Require(data.Vault.LatestBackupFileName is null, "未提交请求不创建认证恢复备份");
        Require(!Directory.EnumerateFiles(data.Home, ".auth-*.tmp").Any(), "无遗留临时认证缓存");
    }

    private static void EqualFile(string path, byte[] expected)
    {
        var actual = File.ReadAllBytes(path);
        try { Require(CryptographicOperations.FixedTimeEquals(actual, expected), "合成认证字节一致"); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new UserFacingException("隔离合成延迟切换验证失败：" + check + "。");
    }

    private static void Rejected(Action action)
    {
        try { action(); }
        catch (UserFacingException) { return; }
        throw new UserFacingException("隔离合成验证失败：已有请求被错误覆盖。");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class Scenario : IDisposable
    {
        private Scenario(string home, VaultStore vault, byte[] original, byte[] target, string targetId)
        {
            Home = home; Vault = vault; Original = original; Target = target; TargetId = targetId;
        }
        public string Home { get; }
        public string AuthPath => Path.Combine(Home, "auth.json");
        public VaultStore Vault { get; }
        public byte[] Original { get; }
        public byte[] Target { get; }
        public string TargetId { get; }

        public static Scenario Create(string root, string name)
        {
            var home = Path.Combine(root, name, "home");
            Directory.CreateDirectory(home);
            FilePrivacy.ApplyDirectory(home);
            var vault = new VaultStore(Path.Combine(root, name, "vault"));
            var original = Auth("synthetic-original", "synthetic-subject-original");
            var target = Auth("synthetic-target", "synthetic-subject-target");
            using var snapshot = AuthSnapshot.FromBytes(target);
            var id = vault.Save(snapshot, "Synthetic deferred target").Id;
            File.WriteAllBytes(Path.Combine(home, "auth.json"), original);
            FilePrivacy.ApplyFile(Path.Combine(home, "auth.json"));
            return new Scenario(home, vault, original, target, id);
        }

        private static byte[] Auth(string accountId, string subject)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["sub"] = subject, ["email"] = "synthetic@example.test",
                ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                ["https://api.openai.com/auth"] = new Dictionary<string, string> { ["chatgpt_plan_type"] = "plus" }
            });
            try
            {
                var jwt = "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".syntheticSignature";
                return JsonSerializer.SerializeToUtf8Bytes(new
                {
                    auth_mode = "chatgpt",
                    tokens = new { access_token = jwt, id_token = jwt, refresh_token = "synthetic-refresh-only", account_id = accountId }
                });
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Original);
            CryptographicOperations.ZeroMemory(Target);
        }
    }
}
