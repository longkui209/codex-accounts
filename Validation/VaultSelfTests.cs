using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.Validation;

/// <summary>
/// Explicitly invoked validation using only synthetic credentials and fresh
/// isolated directories. It never opens the user's CODEX_HOME or live auth.json.
/// No credential bytes or JWT claims are included in the returned report.
/// </summary>
public static class VaultSelfTests
{
    public static IReadOnlyList<string> Run(string outputRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("DPAPI 合成凭据验证只能在 Windows 当前用户环境运行。");
        var isolatedRoot = Path.Combine(Path.GetFullPath(outputRoot),
            "synthetic-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedRoot);
        FilePrivacy.ApplyDirectory(isolatedRoot);
        var reports = new List<string>();
        var store = new VaultStore(Path.Combine(isolatedRoot, "roundtrip"));

        using var first = SyntheticAuth.Create("account-synthetic-a", "subject-synthetic-a",
            "sample-a@example.test", "synthetic-refresh-original");
        using var initial = AuthSnapshot.FromBytes(first.Bytes);
        Check(initial.Info.ExpiresAtUtc == first.AccessExpiration,
            "access token 的到期时间优先于 id token");
        Check(initial.Info.MaskedEmail == "s***@e***.test" && initial.Info.Plan == "Plus",
            "脱敏身份展示");
        Check(!initial.ToString().Contains("synthetic-refresh", StringComparison.Ordinal),
            "认证快照不输出凭据");
        var saved = store.Save(initial, "合成账号 A");
        Check(saved.Id == initial.Info.IdentityKey && store.List().Count == 1, "稳定账号标识");
        using (var restored = store.Read(saved.Id))
            CheckSameBytes(first.Bytes, restored, "DPAPI 账号往返");
        CheckEncryptedFiles(store.DirectoryPath);
        reports.Add("合成凭据往返、身份脱敏、到期时间优先级及全库 DPAPI 加密通过。");

        using var refreshed = SyntheticAuth.Create("account-synthetic-a", "subject-synthetic-a",
            "sample-a@example.test", "synthetic-refresh-rotated");
        using var latest = AuthSnapshot.FromBytes(refreshed.Bytes);
        var updated = store.Save(latest);
        Check(updated.Id == saved.Id && updated.Label == "合成账号 A" && store.List().Count == 1,
            "重复账号去重并保留名称");
        using (var current = store.Read(updated.Id))
            CheckSameBytes(refreshed.Bytes, current, "轮换后的 refresh token 被保存");
        store.Rename(updated.Id, "合成账号已改名");
        Check(new VaultStore(store.DirectoryPath).List().Single().Label == "合成账号已改名",
            "另一实例读取完整持久化索引");
        reports.Add("重复保存去重、刷新凭据更新、保留名称及重命名持久化通过。");

        var backupFileName = store.Backup(initial);
        Check(store.LatestBackupFileName == backupFileName, "加密恢复备份索引");
        using (var backup = store.ReadBackup(backupFileName))
            CheckSameBytes(first.Bytes, backup, "加密恢复备份往返");
        CheckEncryptedFiles(store.DirectoryPath);
        ExpectRejection(() => { using var ignored = store.ReadBackup("../auth.json"); }, "备份路径穿越拒绝");
        reports.Add("DPAPI 恢复备份、加密备份索引及路径穿越拒绝通过。");

        using var another = SyntheticAuth.Create("account-synthetic-b", "subject-synthetic-a",
            "sample-b@example.test", "synthetic-refresh-another");
        using (var second = AuthSnapshot.FromBytes(another.Bytes)) store.Save(second, "合成账号 B");
        Check(store.List().Count == 2, "不同账号不会错误合并");
        using (var second = AuthSnapshot.FromBytes(another.Bytes))
        {
            ExpectRejection(() => store.Remove(second.Info.IdentityKey, () => throw new UserFacingException("Synthetic guard refusal.")),"移除保护失败不写入");
            Check(store.List().Count == 2 && store.LastRemoved() is null,"拒绝移除保持原账号库");
            var removal = store.Remove(second.Info.IdentityKey);
            Check(store.List().Count == 1 && new VaultStore(store.DirectoryPath).LastRemoved()?.RemovalId == removal.RemovalId,
                "移除持久化且可跨实例撤销");
            Check(store.LatestBackupFileName == backupFileName,"移除不改动桌面切换恢复指针");
            CheckEncryptedFiles(store.DirectoryPath);
            ExpectRejection(() => store.RestoreRemoved("../auth.json"),"撤销标识不能变成文件路径");
            store.RestoreRemoved(removal.RemovalId);
            Check(store.List().Count == 2 && store.LastRemoved() is null,"撤销恢复记录与备注");
            using(var roundtrip = store.Read(second.Info.IdentityKey)) CheckSameBytes(another.Bytes,roundtrip,"撤销保持凭据字节完整");
            ExpectRejection(() => store.RestoreRemoved(removal.RemovalId),"重复撤销不能覆盖现有凭据");
            var previous = store.Remove(second.Info.IdentityKey);
            store.Save(second,"合成重新添加");
            ExpectRejection(() => store.RestoreRemoved(previous.RemovalId),"重新登录后不能被旧移除副本覆盖");
            Check(store.List().Single(entry=>entry.Id==second.Info.IdentityKey).Label=="合成重新添加","保留新保存的账号备注");
            reports.Add("PASS: 移除有保护和 DPAPI 副本；可跨实例撤销，凭据字节不变，原桌面恢复指针不变。");
            reports.Add("PASS: 无效撤销、重复撤销与旧副本覆盖重新添加账号均拒绝，失败不清除保存记录。");
        }

        var corruptStore = new VaultStore(Path.Combine(isolatedRoot, "corruption"));
        corruptStore.Save(initial, "合成损坏验证");
        var corruptFile = Directory.EnumerateFiles(corruptStore.DirectoryPath, "*.dpapi").Single();
        var ciphertext = File.ReadAllBytes(corruptFile);
        try
        {
            ciphertext[ciphertext.Length / 2] ^= 0x20;
            File.WriteAllBytes(corruptFile, ciphertext);
            ExpectRejection(() => { _ = corruptStore.List(); }, "被篡改的 DPAPI 账号库拒绝");
        }
        finally { CryptographicOperations.ZeroMemory(ciphertext); }
        reports.Add("不同账号分离及损坏密文拒绝通过。");

        var oversized = new byte[AuthSnapshot.MaximumBytes + 1];
        try { ExpectRejection(() => { using var ignored = AuthSnapshot.FromBytes(oversized); }, "超长认证拒绝"); }
        finally { CryptographicOperations.ZeroMemory(oversized); }

        using var unsupported = SyntheticAuth.Create("account-synthetic-api", "subject-synthetic-api",
            "sample-api@example.test", "synthetic-refresh-unsupported", authMode: "api_key");
        ExpectRejection(() => { using var ignored = AuthSnapshot.FromBytes(unsupported.Bytes); },
            "不支持的认证方式拒绝");
        using var apiKey = SyntheticAuth.Create("account-synthetic-key", "subject-synthetic-key",
            "sample-key@example.test", "synthetic-refresh-key", apiKey: "synthetic-api-key-not-real");
        ExpectRejection(() => { using var ignored = AuthSnapshot.FromBytes(apiKey.Bytes); }, "API Key 拒绝");
        var invalid = "{\"auth_mode\":\"chatgpt\",\"tokens\":{}}"u8.ToArray();
        try { ExpectRejection(() => { using var ignored = AuthSnapshot.FromBytes(invalid); }, "缺失凭据拒绝"); }
        finally { CryptographicOperations.ZeroMemory(invalid); }
        reports.Add("1 MiB 大小边界、不支持的认证方式、API Key 和缺失凭据拒绝通过。");

        var disposable = AuthSnapshot.FromBytes(first.Bytes);
        disposable.Dispose();
        disposable.Dispose();
        ExpectRejection(() => { _ = disposable.CopyBytes(); }, "释放后拒绝读取快照");
        reports.Add("认证快照重复释放安全，释放后无法读取凭据。");
        return reports;
    }

    private static void CheckEncryptedFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            Check(Path.GetExtension(file) == ".dpapi", "未创建明文索引或临时凭据文件");
            var encrypted = File.ReadAllBytes(file);
            var tokenMarker = Encoding.UTF8.GetBytes("synthetic-refresh");
            var indexMarker = Encoding.UTF8.GetBytes("maskedEmail");
            try
            {
                Check(encrypted.AsSpan().IndexOf(tokenMarker) < 0 &&
                      encrypted.AsSpan().IndexOf(indexMarker) < 0, "凭据和索引不以明文保存");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                CryptographicOperations.ZeroMemory(tokenMarker);
                CryptographicOperations.ZeroMemory(indexMarker);
            }
        }
    }

    private static void CheckSameBytes(byte[] expected, AuthSnapshot actual, string name)
    {
        var bytes = actual.CopyBytes();
        try { Check(expected.AsSpan().SequenceEqual(bytes), name); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new UserFacingException("隔离合成凭据验证失败：" + name + "。");
    }

    private static void ExpectRejection(Action action, string name)
    {
        try { action(); }
        catch (UserFacingException) { return; }
        throw new UserFacingException("隔离合成凭据验证失败：" + name + "。");
    }

    private sealed class SyntheticAuth : IDisposable
    {
        private SyntheticAuth(byte[] bytes, DateTimeOffset expires)
        {
            Bytes = bytes;
            AccessExpiration = expires;
        }

        public byte[] Bytes { get; }
        public DateTimeOffset AccessExpiration { get; }

        public static SyntheticAuth Create(string accountId, string subject, string email,
            string refreshToken, string authMode = "chatgpt", string? apiKey = null)
        {
            var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
            var access = Jwt(new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["exp"] = expires.ToUnixTimeSeconds(),
                ["https://api.openai.com/auth"] = new Dictionary<string, object>
                {
                    ["chatgpt_plan_type"] = "plus",
                    ["chatgpt_account_id"] = accountId
                }
            });
            var identity = Jwt(new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["email"] = email,
                ["exp"] = expires.AddHours(12).ToUnixTimeSeconds()
            });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                auth_mode = authMode,
                OPENAI_API_KEY = apiKey,
                tokens = new { access_token = access, id_token = identity, refresh_token = refreshToken, account_id = accountId },
                last_refresh = DateTimeOffset.UtcNow
            });
            return new SyntheticAuth(bytes, expires);
        }

        private static string Jwt(Dictionary<string, object> claims)
        {
            var header = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8.ToArray();
            var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
            try { return Base64Url(header) + "." + Base64Url(payload) + ".syntheticSignature"; }
            finally
            {
                CryptographicOperations.ZeroMemory(header);
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }
}
