using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// The complete account index, labels and authentication files are encrypted as one
/// DPAPI document. Recovery snapshots are separately encrypted; no plaintext index
/// or token sidecars are created. This class never reads the live Codex auth file.
/// </summary>
public sealed class VaultStore
{
    private const string VaultFileName = "accounts.dpapi";
    private const int MaximumAccounts = 64;
    private const int MaximumVaultBytes = 96 * 1024 * 1024;
    private const int MaximumBackupBytes = AuthSnapshot.MaximumBytes + 64 * 1024;
    private static readonly ConcurrentDictionary<string, object> ProcessGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string mutexName;
    private readonly object processGate;

    public VaultStore(string directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("账号加密库仅支持 Windows 当前用户环境。");
        try
        {
            DirectoryPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (DirectoryPath.StartsWith("\\\\", StringComparison.Ordinal) ||
                string.Equals(DirectoryPath.TrimEnd('\\'),
                    Path.GetPathRoot(DirectoryPath)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException("账号库必须存放在本机的专用文件夹中。");
            CheckDirectoryChain();
            using var identity = WindowsIdentity.GetCurrent();
            var userSid = identity.User?.Value;
            if (userSid is null)
                throw new UserFacingException("无法确认当前 Windows 用户，账号库操作已取消。");
            var mutexBytes = Encoding.UTF8.GetBytes(userSid + "\0" + DirectoryPath.ToUpperInvariant());
            try
            {
                mutexName = "Global\\CodexAccountSwitcherVault_" +
                    Convert.ToHexString(SHA256.HashData(mutexBytes));
            }
            finally { CryptographicOperations.ZeroMemory(mutexBytes); }
            processGate = ProcessGates.GetOrAdd(DirectoryPath, _ => new object());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         ArgumentException or System.Security.SecurityException)
        {
            throw new UserFacingException("无法访问指定账号库文件夹，操作已取消。");
        }
    }

    public string DirectoryPath { get; }

    public string? LatestBackupFileName => WithVault(document => document.LatestBackupFileName);

    public IReadOnlyList<AccountSummary> List() => WithVault(document =>
        (IReadOnlyList<AccountSummary>)document.Accounts
            .OrderByDescending(account => account.SavedAtUtc)
            .Select(ToSummary).ToArray());

    public AccountSummary Save(AuthSnapshot snapshot, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requestedLabel = label is null ? null : ValidateLabel(label);
        return WithVault(document =>
        {
            var info = snapshot.Info;
            var existing = document.Accounts.Find(account => account.Id == info.IdentityKey);
            if (existing is null && document.Accounts.Count >= MaximumAccounts)
                throw new UserFacingException("账号库最多保存 64 个账号，本次添加已取消。");

            var authBytes = snapshot.CopyBytes();
            if (existing is null)
            {
                existing = new StoredAccount
                {
                    Id = info.IdentityKey,
                    Label = requestedLabel ?? DefaultLabel(info),
                    MaskedEmail = info.MaskedEmail,
                    Plan = info.Plan,
                    ExpiresAtUtc = info.ExpiresAtUtc,
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Auth = authBytes
                };
                document.Accounts.Add(existing);
            }
            else
            {
                // Re-saving a login refreshes the whole auth snapshot, including
                // rotated refresh tokens, while retaining the user's label.
                CryptographicOperations.ZeroMemory(existing.Auth);
                existing.Auth = authBytes;
                existing.Label = requestedLabel ?? existing.Label;
                existing.MaskedEmail = info.MaskedEmail;
                existing.Plan = info.Plan;
                existing.ExpiresAtUtc = info.ExpiresAtUtc;
                existing.SavedAtUtc = DateTimeOffset.UtcNow;
            }
            Persist(document);
            return ToSummary(existing);
        });
    }

    public AuthSnapshot Read(string id)
    {
        ValidateAccountId(id);
        return WithVault(document =>
        {
            var account = document.Accounts.Find(candidate => candidate.Id == id);
            if (account is null) throw new UserFacingException("所选账号已不在本地账号库中，请刷新列表。");
            return AuthSnapshot.FromBytes(account.Auth);
        });
    }

    public AuthSnapshot ReadForRefresh(string id, TimeSpan lockTimeout)
    {
        ValidateAccountId(id);
        return WithVault(document =>
        {
            var account = document.Accounts.Find(candidate => candidate.Id == id);
            if (account is null) throw new UserFacingException("所选账号已不在本地账号库中，请刷新列表。");
            return AuthSnapshot.FromBytes(account.Auth);
        }, lockTimeout);
    }

    /// <summary>
    /// Commits a rotated credential only while the exact original auth bytes are
    /// still current. A concurrent official login is never overwritten, and the
    /// user's account label and desktop-cache recovery pointer are retained.
    /// </summary>
    public bool CompareAndSave(string profileId, AuthSnapshot expected, AuthSnapshot replacement, TimeSpan? lockTimeout = null)
    {
        ValidateAccountId(profileId);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        if (expected.Info.IdentityKey != profileId || replacement.Info.IdentityKey != profileId)
            throw new UserFacingException("刷新后的账号身份发生变化，已拒绝保存；请重新通过官方登录添加账号。");
        var expectedBytes = expected.CopyBytes();
        try
        {
            return WithVault(document =>
            {
                var account = document.Accounts.Find(candidate => candidate.Id == profileId);
                if (account is null || !CryptographicOperations.FixedTimeEquals(account.Auth, expectedBytes))
                    return false;
                // This is a whole-vault disaster-recovery checkpoint, not a
                // desktop auth-cache backup. Never change LatestBackupFileName.
                var recovery = ReadProtectedFile(Path.Combine(DirectoryPath, VaultFileName), MaximumVaultBytes);
                try
                {
                    AtomicWrite(Path.Combine(DirectoryPath, "refresh-vault-backup-" +
                        DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + "-" +
                        Guid.NewGuid().ToString("N") + ".dpapi"), recovery, replace: false);
                }
                finally { CryptographicOperations.ZeroMemory(recovery); }
                var replacementBytes = replacement.CopyBytes();
                CryptographicOperations.ZeroMemory(account.Auth);
                account.Auth = replacementBytes;
                account.MaskedEmail = replacement.Info.MaskedEmail;
                account.Plan = replacement.Info.Plan;
                account.ExpiresAtUtc = replacement.Info.ExpiresAtUtc;
                account.SavedAtUtc = DateTimeOffset.UtcNow;
                Persist(document);
                return true;
            }, lockTimeout);
        }
        finally { CryptographicOperations.ZeroMemory(expectedBytes); }
    }

    public void Rename(string id, string label)
    {
        ValidateAccountId(id);
        var safeLabel = ValidateLabel(label);
        _ = WithVault(document =>
        {
            var account = document.Accounts.Find(candidate => candidate.Id == id);
            if (account is null) throw new UserFacingException("所选账号已不在本地账号库中，请刷新列表。");
            account.Label = safeLabel;
            Persist(document);
            return true;
        });
    }

    public RemovedAccountSummary Remove(string id, Action? guard = null)
    {
        ValidateAccountId(id);
        return WithVault(document =>
        {
            var account = document.Accounts.Find(entry => entry.Id == id)
                ?? throw new UserFacingException("该账号已不在保存列表中，请刷新查看。");
            guard?.Invoke();
            var removal = new RemovedAccountSummary(Guid.NewGuid().ToString("N"), id, account.Label, account.MaskedEmail, DateTimeOffset.UtcNow);
            var plain = JsonSerializer.SerializeToUtf8Bytes(account, JsonOptions);
            byte[]? encrypted = null;
            try
            {
                encrypted = SecretProtection.Protect(plain);
                AtomicWrite(RemovalPath(removal.RemovalId), encrypted, replace: false);
                RemovalPointer().Locked(_ => (removal, true));
                document.Accounts.Remove(account);
                try { Persist(document); }
                finally { CryptographicOperations.ZeroMemory(account.Auth); }
                return removal;
            }
            finally { CryptographicOperations.ZeroMemory(plain); if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted); }
        });
    }

    public RemovedAccountSummary? LastRemoved() => WithVault(document =>
    {
        try
        {
            var removal = RemovalPointer().Locked(value => ((RemovedAccountSummary?)null, value));
            return removal is { Restored: false } && !document.Accounts.Any(entry => entry.Id == removal.AccountId) ? removal : null;
        }
        catch (UserFacingException) { return null; }
    });

    public void RestoreRemoved(string removalId)
    {
        _ = WithVault(document =>
        {
            var removal = RemovalPointer().Locked(value => ((RemovedAccountSummary?)null, value));
            if (removal is null || removal.Restored || removal.RemovalId != removalId)
                throw new UserFacingException("该移除记录已变化，请刷新后再撤销。");
            if (document.Accounts.Any(entry => entry.Id == removal.AccountId))
                throw new UserFacingException("该账号已在列表中，没有覆盖现有登录凭据。");
            if (document.Accounts.Count >= MaximumAccounts) throw new UserFacingException("账号库已满，暂时无法撤销移除。");
            var encrypted = ReadProtectedFile(RemovalPath(removalId), 2 * AuthSnapshot.MaximumBytes);
            byte[]? plain = null;
            StoredAccount? account = null;
            var added = false;
            try
            {
                plain = SecretProtection.Unprotect(encrypted);
                account = JsonSerializer.Deserialize<StoredAccount>(plain, JsonOptions) ?? throw InvalidVault();
                using var snapshot = AuthSnapshot.FromBytes(account.Auth);
                if (snapshot.Info.IdentityKey != removal.AccountId || account.Id != removal.AccountId ||
                    snapshot.Info.MaskedEmail != account.MaskedEmail || snapshot.Info.Plan != account.Plan || snapshot.Info.ExpiresAtUtc != account.ExpiresAtUtc)
                    throw InvalidVault();
                _ = ValidateLabel(account.Label);
                account.SavedAtUtc = DateTimeOffset.UtcNow;
                document.Accounts.Add(account); added = true;
                Persist(document);
                // Presence in the main vault also prevents a later undo from overwriting new tokens.
                try { RemovalPointer().Locked(value => value?.RemovalId == removalId ? (value with { Restored = true }, true) : (null, false)); } catch { }
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                if (!added && account?.Auth is not null) CryptographicOperations.ZeroMemory(account.Auth);
            }
        });
    }
    private PluginHost.PrivateJobStore<RemovedAccountSummary> RemovalPointer() => new(DirectoryPath,"last-removal.dpapi");
    private string RemovalPath(string id)
    {
        if (!Guid.TryParseExact(id,"N",out _)) throw new UserFacingException("移除记录标识无效。");
        return Path.Combine(DirectoryPath,"removed-account-" + id + ".dpapi");
    }

    public string Backup(AuthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WithVault(document =>
        {
            var fileName = "backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") +
                "-" + Guid.NewGuid().ToString("N") + ".dpapi";
            var auth = snapshot.CopyBytes();
            byte[]? encrypted = null;
            try
            {
                encrypted = SecretProtection.Protect(auth);
                AtomicWrite(Path.Combine(DirectoryPath, fileName), encrypted, replace: false);
                document.LatestBackupFileName = fileName;
                Persist(document);
                return fileName;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(auth);
                if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            }
        });
    }

    public AuthSnapshot ReadBackup(string backupFileName)
    {
        ValidateBackupFileName(backupFileName);
        return WithLock(() =>
        {
            var encrypted = ReadProtectedFile(Path.Combine(DirectoryPath, backupFileName), MaximumBackupBytes);
            byte[]? auth = null;
            try
            {
                auth = SecretProtection.Unprotect(encrypted);
                return AuthSnapshot.FromBytes(auth);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (auth is not null) CryptographicOperations.ZeroMemory(auth);
            }
        });
    }

    private T WithVault<T>(Func<VaultDocument, T> action, TimeSpan? lockTimeout = null)
    {
        T Execute()
        {
            var document = Load();
            try { return action(document); }
            finally { ClearDocument(document); }
        }
        return lockTimeout is { } timeout ? WithBoundedLock(Execute, timeout) : WithLock(Execute);
    }

    private T WithBoundedLock<T>(Func<T> action, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new UserFacingException("账号库刷新锁的等待时间无效。");
        var waiting = Stopwatch.StartNew();
        if (!Monitor.TryEnter(processGate, timeout))
            throw new UserFacingException("账号库正在更新，已拒绝等待超时的令牌刷新；请稍后重试。");
        try
        {
            using var mutex = new Mutex(false, mutexName);
            var acquired = false;
            try
            {
                var remaining = timeout - waiting.Elapsed;
                try { acquired = mutex.WaitOne(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new UserFacingException("账号库正在更新，已拒绝等待超时的令牌刷新；请稍后重试。");
                CheckDirectoryChain();
                Directory.CreateDirectory(DirectoryPath);
                FilePrivacy.ApplyDirectory(DirectoryPath);
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                    System.Security.SecurityException or CryptographicException)
        {
            throw new UserFacingException("账号库刷新保存未完成，请检查文件权限后重新通过官方登录添加账号。");
        }
        finally { Monitor.Exit(processGate); }
    }

    private T WithLock<T>(Func<T> action)
    {
        lock (processGate)
        {
            try
            {
                using var mutex = new Mutex(false, mutexName);
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new UserFacingException("另一实例正在操作账号库，请稍后再试。");
                    CheckDirectoryChain();
                    Directory.CreateDirectory(DirectoryPath);
                    FilePrivacy.ApplyDirectory(DirectoryPath);
                    return action();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
            catch (UserFacingException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             System.Security.SecurityException or CryptographicException)
            {
                throw new UserFacingException("账号库操作未完成：请检查当前 Windows 用户的文件权限和磁盘可用空间。");
            }
        }
    }

    private VaultDocument Load()
    {
        var path = Path.Combine(DirectoryPath, VaultFileName);
        if (!File.Exists(path)) return new VaultDocument();
        var encrypted = ReadProtectedFile(path, MaximumVaultBytes);
        byte[]? plaintext = null;
        VaultDocument? document = null;
        try
        {
            plaintext = SecretProtection.Unprotect(encrypted);
            document = JsonSerializer.Deserialize<VaultDocument>(plaintext, JsonOptions);
            if (document is null || document.Version != 1 || document.Accounts is null ||
                document.Accounts.Count > MaximumAccounts)
                throw InvalidVault();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var account in document.Accounts)
            {
                if (account is null || account.Auth is null || !IsAccountId(account.Id) ||
                    !identities.Add(account.Id) || account.Auth.Length == 0 ||
                    account.Auth.Length > AuthSnapshot.MaximumBytes ||
                    account.SavedAtUtc == default || account.SavedAtUtc.Offset != TimeSpan.Zero)
                    throw InvalidVault();
                _ = ValidateLabel(account.Label);
                using var snapshot = AuthSnapshot.FromBytes(account.Auth);
                if (account.Id != snapshot.Info.IdentityKey || account.MaskedEmail != snapshot.Info.MaskedEmail ||
                    account.Plan != snapshot.Info.Plan || account.ExpiresAtUtc != snapshot.Info.ExpiresAtUtc)
                    throw InvalidVault();
            }
            if (document.LatestBackupFileName is not null)
                ValidateBackupFileName(document.LatestBackupFileName);
            return document;
        }
        catch (UserFacingException)
        {
            if (document is not null) ClearDocument(document);
            throw InvalidVault();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
                                         InvalidOperationException or NotSupportedException)
        {
            if (document is not null) ClearDocument(document);
            throw InvalidVault();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Persist(VaultDocument document)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        byte[]? encrypted = null;
        try
        {
            encrypted = SecretProtection.Protect(plaintext);
            if (encrypted.Length > MaximumVaultBytes) throw InvalidVault();
            AtomicWrite(Path.Combine(DirectoryPath, VaultFileName), encrypted, replace: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private byte[] ReadProtectedFile(string path, int maximumBytes)
    {
        CheckFile(path);
        FilePrivacy.ApplyFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumBytes) throw InvalidVault();
        var buffer = new byte[checked((int)stream.Length)];
        try { stream.ReadExactly(buffer); return buffer; }
        catch { CryptographicOperations.ZeroMemory(buffer); throw; }
    }

    private void AtomicWrite(string target, byte[] encrypted, bool replace)
    {
        CheckFile(target, mayBeMissing: true);
        var temporary = Path.Combine(DirectoryPath, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 8192, FileOptions.WriteThrough))
            {
                FilePrivacy.ApplyFile(temporary);
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            CheckFile(target, mayBeMissing: true);
            if (File.Exists(target))
            {
                if (!replace) throw new UserFacingException("恢复备份文件已经存在，操作已取消。");
                FilePrivacy.ApplyFile(target);
                File.Replace(temporary, target, null, ignoreMetadataErrors: false);
            }
            else File.Move(temporary, target, overwrite: false);
            FilePrivacy.ApplyFile(target);
        }
        finally
        {
            // Only our freshly generated, encrypted temporary file is cleaned up.
            // A completed recovery backup is never removed on a later failure.
            try
            {
                if (File.Exists(temporary) &&
                    (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void CheckDirectoryChain()
    {
        var candidate = DirectoryPath;
        while (!string.IsNullOrEmpty(candidate))
        {
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                    throw new UserFacingException("账号库路径不能包含符号链接、重解析点或普通文件。");
            }
            candidate = Path.GetDirectoryName(candidate);
        }
    }

    private void CheckFile(string path, bool mayBeMissing = false)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath,
                StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("账号库文件路径无效，操作已取消。");
        if (mayBeMissing && !File.Exists(path) && !Directory.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new UserFacingException("账号库文件不能是目录、符号链接或重解析点。");
    }

    private static string DefaultLabel(AccountInfo info) =>
        info.MaskedEmail == "未提供邮箱" ? "ChatGPT " + info.Plan + " 账号" : info.MaskedEmail;

    private static string ValidateLabel(string? label)
    {
        var normalized = label?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 80 || normalized.Any(char.IsControl))
            throw new UserFacingException("账号名称须为 1 至 80 个字符，且不能包含控制字符。");
        return normalized;
    }

    private static bool IsAccountId(string? id) => id is not null && id.Length == 64 &&
        id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateAccountId(string? id)
    {
        if (!IsAccountId(id)) throw new UserFacingException("所选账号标识无效，请刷新列表。");
    }

    private static void ValidateBackupFileName(string? fileName)
    {
        if (fileName is null || !Regex.IsMatch(fileName,
                @"\Abackup-[0-9]{8}T[0-9]{9}Z-[0-9a-f]{32}\.dpapi\z", RegexOptions.CultureInvariant))
            throw new UserFacingException("恢复备份文件名无效，操作已取消。");
    }

    private static UserFacingException InvalidVault() =>
        new("账号库已损坏或无法由当前 Windows 用户解密，请保留原文件并使用加密恢复备份。");

    private static AccountSummary ToSummary(StoredAccount account) => new(account.Id, account.Label,
        account.MaskedEmail, account.Plan, account.SavedAtUtc, account.ExpiresAtUtc);

    private static void ClearDocument(VaultDocument document)
    {
        if (document.Accounts is null) return;
        foreach (var account in document.Accounts)
            if (account?.Auth is not null) CryptographicOperations.ZeroMemory(account.Auth);
    }

    private sealed class VaultDocument
    {
        public VaultDocument() { }
        public int Version { get; set; } = 1;
        public List<StoredAccount> Accounts { get; set; } = [];
        public string? LatestBackupFileName { get; set; }
    }

    private sealed class StoredAccount
    {
        public StoredAccount() { }
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string MaskedEmail { get; set; } = string.Empty;
        public string Plan { get; set; } = string.Empty;
        public DateTimeOffset SavedAtUtc { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public byte[] Auth { get; set; } = [];
    }
}
