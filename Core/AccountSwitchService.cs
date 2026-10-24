using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

public sealed class AccountSwitchService
{
    private readonly VaultStore vault;
    private readonly string codexHome;
    private readonly Func<RuntimeStatus> guard;

    public AccountSwitchService(VaultStore vault, string codexHome, Func<RuntimeStatus>? guard = null)
    {
        this.vault = vault;
        this.codexHome = AuthFileSafety.NormalizeDirectory(codexHome);
        this.guard = guard ?? CodexRuntime.Inspect;
    }

    /// <summary>Writes a saved login cache. The returned name identifies the encrypted recovery backup.</summary>
    public string Switch(string id,string? expectedCurrentIdentity = null)
    {
        using var mutex = AcquireSwitchMutex();
        AssertClientClosed();
        using var directories = AuthFileSafety.ProtectDirectories(codexHome);
        CodexRuntime.EnsureFileAuthStore(codexHome);
        using var target = vault.Read(id);
        return ReplaceCache(target, skipCurrentIdentity: true, expectedCurrentIdentity);
    }

    /// <summary>Restores a recovery cache on disk; this does not verify the account with the service.</summary>
    public string RestoreLatest()
    {
        using var mutex = AcquireSwitchMutex();
        AssertClientClosed();
        using var directories = AuthFileSafety.ProtectDirectories(codexHome);
        CodexRuntime.EnsureFileAuthStore(codexHome);
        var backupName = vault.LatestBackupFileName;
        if (string.IsNullOrWhiteSpace(backupName)) throw new UserFacingException("还没有可恢复的加密备份。");
        using var target = vault.ReadBackup(backupName);
        ReplaceCache(target);
        return backupName;
    }

    private string ReplaceCache(AuthSnapshot target, bool skipCurrentIdentity = false,string? expectedCurrentIdentity = null)
    {
        var authPath = Path.Combine(codexHome, "auth.json");
        var configPath = Path.Combine(codexHome, "config.toml");
        using var original = AuthFileSafety.ReadOptional(authPath);
        using var originalConfig = AuthFileSafety.ReadOptional(configPath, 4 * 1024 * 1024);
        var backupName = "";
        if(original is null && expectedCurrentIdentity is not null)
            throw new UserFacingException("桌面登录已变化，未覆盖当前登录；请重新确认切换目标。");
        if (original is not null)
        {
            using var current = AuthSnapshot.FromBytes(original.Bytes);
            if(expectedCurrentIdentity is not null && current.Info.IdentityKey != expectedCurrentIdentity)
                throw new UserFacingException("桌面登录已变化，未覆盖当前登录；请重新确认切换目标。");
            if (skipCurrentIdentity && current.Info.IdentityKey == target.Info.IdentityKey)
            {
                // The on-disk login may have a freshly rotated refresh token. Never restore an older
                // vault snapshot over the same live identity merely because the user selected it.
                AssertClientClosed();
                CodexRuntime.EnsureFileAuthStore(codexHome);
                AuthFileSafety.AssertUnchanged(configPath, originalConfig, 4 * 1024 * 1024);
                AuthFileSafety.AssertUnchanged(authPath, original);
                vault.Save(current);
                return "";
            }
            if (original.IsReadOnly) throw new UserFacingException("auth.json 是只读文件，已拒绝切换。未更改文件属性。");
            vault.Save(current);
            backupName = vault.Backup(current);
            if (string.IsNullOrWhiteSpace(backupName))
                throw new UserFacingException("无法创建加密恢复备份，已拒绝写入认证文件。");
        }

        var bytes = target.CopyBytes();
        var temporaryPath = Path.Combine(codexHome, ".auth-switch-" + Guid.NewGuid().ToString("N") + ".tmp");
        AuthFileSafety.FileVersion? temporaryVersion = null;
        var replaced = false;
        try
        {
            // Reparse checks and file IDs make a pathname substitution observable.
            // The mutex protects other switcher instances. Non-cooperating writers must also be closed.
            using (var temporary = AuthFileSafety.CreateTemporary(temporaryPath))
            {
                try
                {
                    temporaryVersion = AuthFileSafety.GetVersion(temporary.SafeFileHandle);
                    AuthFileSafety.AssertHandleSafe(temporary.SafeFileHandle, temporaryPath);
                    FilePrivacy.ApplyFile(temporaryPath);
                    temporary.Write(bytes);
                    temporary.Flush(flushToDisk: true);
                }
                catch
                {
                    AuthFileSafety.MarkTemporaryForDeletion(temporary.SafeFileHandle);
                    throw;
                }
            }
            using (var prepared = AuthFileSafety.ReadOptional(temporaryPath))
            {
                if (prepared is null || !prepared.Version.SameFile(temporaryVersion!) ||
                    !CryptographicOperations.FixedTimeEquals(bytes, prepared.Bytes))
                    throw new UserFacingException("临时认证文件校验未通过，已拒绝替换当前登录。");
                temporaryVersion = prepared.Version;
            }

            AssertClientClosed();
            CodexRuntime.EnsureFileAuthStore(codexHome);
            AuthFileSafety.AssertUnchanged(configPath, originalConfig, 4 * 1024 * 1024);
            AuthFileSafety.AssertUnchanged(authPath, original);
            if (original is null) File.Move(temporaryPath, authPath); // Never overwrite a new concurrent login.
            else File.Replace(temporaryPath, authPath, destinationBackupFileName: null);
            replaced = true;

            using var written = AuthFileSafety.ReadOptional(authPath);
            if (written is null || temporaryVersion is null || !written.Version.SameFile(temporaryVersion))
                throw new UserFacingException("认证文件在写入后被其他程序替换，已停止；没有覆盖该程序的更改。");
            if (!CryptographicOperations.FixedTimeEquals(bytes, written.Bytes))
                throw new UserFacingException("认证文件写入后的内容校验未通过。");
            using var validated = AuthSnapshot.FromBytes(written.Bytes);
            if (!string.Equals(validated.Info.IdentityKey, target.Info.IdentityKey, StringComparison.Ordinal))
                throw new UserFacingException("认证文件写入后的账号身份校验未通过。");
            AuthFileSafety.AssertUnchanged(configPath, originalConfig, 4 * 1024 * 1024);
            return backupName;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            // ReplaceFile may report an error after doing part of the operation. Inspect the actual file.
            var rollback = AttemptRollback(authPath, original, temporaryVersion, bytes, replaced);
            if (rollback == RollbackResult.ConcurrentChange)
                throw new UserFacingException("认证文件在切换期间被其他程序更改，已停止且未覆盖该更改。切换前的账号已保存在加密备份中。");
            if (rollback == RollbackResult.Failed)
                throw new UserFacingException("认证写入未完成，且无法安全自动恢复。请保持 Codex 关闭，并使用工具中的加密备份恢复。");
            if (rollback == RollbackResult.Restored)
                throw new UserFacingException("认证写入或校验未完成，已恢复切换前的认证缓存。后端登录状态尚未验证。");
            if (failure is UserFacingException) throw;
            throw new UserFacingException("认证缓存写入未完成；切换前的文件保持不变。请检查文件权限和磁盘空间后重试。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            AuthFileSafety.DeleteOwnedTemporary(temporaryPath, temporaryVersion);
        }
    }

    private RollbackResult AttemptRollback(string authPath, AuthFileSafety.FileImage? original,
        AuthFileSafety.FileVersion? ownedVersion, byte[] expectedBytes, bool replaced)
    {
        try
        {
            using var current = AuthFileSafety.ReadOptional(authPath);
            if (AuthFileSafety.SameImage(original, current)) return RollbackResult.NotNeeded;
            if (ownedVersion is null) return replaced ? RollbackResult.Failed : RollbackResult.ConcurrentChange;
            if (current is null || !current.Version.SameFile(ownedVersion)) return RollbackResult.ConcurrentChange;
            // A changed write timestamp identifies an observed concurrent write, including same-file edits.
            if (!current.Version.SameWrite(ownedVersion)) return RollbackResult.ConcurrentChange;
            if (!replaced && !CryptographicOperations.FixedTimeEquals(expectedBytes, current.Bytes))
                return RollbackResult.ConcurrentChange;

            if (original is null)
            {
                AuthFileSafety.AssertUnchanged(authPath, current);
                File.Delete(authPath);
                using var absent = AuthFileSafety.ReadOptional(authPath);
                return absent is null ? RollbackResult.Restored : RollbackResult.ConcurrentChange;
            }

            var recoveryPath = Path.Combine(codexHome, ".auth-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
            AuthFileSafety.FileVersion? recoveryVersion = null;
            try
            {
                using (var recovery = AuthFileSafety.CreateTemporary(recoveryPath))
                {
                    try
                    {
                        recoveryVersion = AuthFileSafety.GetVersion(recovery.SafeFileHandle);
                        AuthFileSafety.AssertHandleSafe(recovery.SafeFileHandle, recoveryPath);
                        FilePrivacy.ApplyFile(recoveryPath);
                        recovery.Write(original.Bytes);
                        recovery.Flush(flushToDisk: true);
                    }
                    catch
                    {
                        AuthFileSafety.MarkTemporaryForDeletion(recovery.SafeFileHandle);
                        throw;
                    }
                }
                using (var prepared = AuthFileSafety.ReadOptional(recoveryPath))
                {
                    if (prepared is null || !prepared.Version.SameFile(recoveryVersion!) ||
                        !CryptographicOperations.FixedTimeEquals(original.Bytes, prepared.Bytes))
                        return RollbackResult.Failed;
                    recoveryVersion = prepared.Version;
                }
                AuthFileSafety.AssertUnchanged(authPath, current);
                File.Replace(recoveryPath, authPath, destinationBackupFileName: null);
                using var restored = AuthFileSafety.ReadOptional(authPath);
                return restored is not null && restored.Version.SameFile(recoveryVersion!) &&
                       CryptographicOperations.FixedTimeEquals(original.Bytes, restored.Bytes)
                    ? RollbackResult.Restored : RollbackResult.Failed;
            }
            finally { AuthFileSafety.DeleteOwnedTemporary(recoveryPath, recoveryVersion); }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return RollbackResult.Failed;
        }
    }

    private void AssertClientClosed()
    {
        RuntimeStatus status;
        try { status = guard(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            throw new UserFacingException("无法可靠确认客户端和后台运行状态，已拒绝切换。");
        }
        if (status is null || !status.CanSwitch)
            throw new UserFacingException("请正常退出所有 Codex / ChatGPT 客户端及 Codex 后台后重试。工具不会结束进程或中断任务。");
    }

    private IDisposable AcquireSwitchMutex()
    {
        try
        {
            var pathDigest = SHA256.HashData(Encoding.UTF8.GetBytes(codexHome.ToUpperInvariant()));
            var name = "Global\\CodexAccountSwitcher.Auth." + Convert.ToHexString(pathDigest)[..32];
            var mutex = new Mutex(initiallyOwned: false, name);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new UserFacingException("另一个账号切换操作正在运行，请稍后重试。");
                return new MutexLease(mutex);
            }
            catch
            {
                if (acquired) mutex.ReleaseMutex();
                mutex.Dispose();
                throw;
            }
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or WaitHandleCannotBeOpenedException)
        {
            throw new UserFacingException("无法获得账号切换锁，已拒绝写入认证文件。");
        }
    }

    private enum RollbackResult { NotNeeded, Restored, ConcurrentChange, Failed }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}

/// <summary>Local file protections. No methods log file contents or expose authentication material.</summary>
internal static class AuthFileSafety
{
    private const uint FileReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint SequentialScan = 0x08000000;
    private const uint WriteThrough = 0x80000000;
    private const uint ReparseAttribute = 0x400;
    private const uint DirectoryAttribute = 0x10;

    internal static string NormalizeDirectory(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
                directory.StartsWith("\\\\", StringComparison.Ordinal) || directory.Contains('"') ||
                directory.Contains('*') || directory.Contains('?'))
                throw new UserFacingException("Codex 主目录必须是本机磁盘上的完整路径，不能是网络目录或特殊路径。");
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (normalized.Length < 3 || normalized[1] != ':' || normalized[2] != Path.DirectorySeparatorChar ||
                normalized[3..].Contains(':'))
                throw new UserFacingException("Codex 主目录路径无效，已拒绝访问认证缓存。");
            return normalized;
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UserFacingException("Codex 主目录路径无效，已拒绝访问认证缓存。");
        }
    }

    internal static IDisposable ProtectDirectories(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("认证文件保护仅支持 Windows。");
        var lease = new DirectoryLease();
        try
        {
            var chain = new List<string>();
            for (var cursor = new DirectoryInfo(NormalizeDirectory(directory)); cursor is not null; cursor = cursor.Parent)
                chain.Add(cursor.FullName);
            chain.Reverse();
            foreach (var path in chain)
            {
                var handle = CreateFile(NativeFilePath(path), FileReadAttributes, ShareRead | ShareWrite, IntPtr.Zero,
                    OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new UserFacingException("无法保护 Codex 主目录；请检查目录存在、访问权限及是否被占用。");
                }
                lease.Add(handle);
                var information = GetInformation(handle);
                if ((information.FileAttributes & ReparseAttribute) != 0 ||
                    (information.FileAttributes & DirectoryAttribute) == 0)
                    throw new UserFacingException("Codex 主目录或父目录包含链接、junction 或 reparse point，已拒绝切换。");
                AssertHandleSafe(handle, path);
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static FileImage? ReadOptional(string path, int maxBytes = 1024 * 1024)
    {
        var handle = CreateFile(NativeFilePath(path), GenericRead, ShareRead, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | SequentialScan, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (code is 2 or 3) return null;
            throw new UserFacingException("认证或配置文件无法安全读取；可能正在被使用或权限不足。已拒绝切换。");
        }

        byte[]? contents = null;
        try
        {
            AssertHandleSafe(handle, path);
            var information = GetInformation(handle);
            if ((information.FileAttributes & DirectoryAttribute) != 0)
                throw new UserFacingException("认证或配置路径指向了目录，已拒绝切换。");
            var version = GetVersion(handle);
            if (version.Length < 0 || version.Length > maxBytes)
                throw new UserFacingException("认证或配置文件大小异常，已拒绝切换。");
            contents = new byte[checked((int)version.Length)];
            using var stream = new FileStream(handle, FileAccess.Read);
            stream.ReadExactly(contents);
            var finalVersion = GetVersion(stream.SafeFileHandle);
            if (!version.SameFile(finalVersion) || !version.SameWrite(finalVersion))
                throw new UserFacingException("认证或配置文件在读取过程中发生变化，已拒绝切换。");
            var image = new FileImage(contents, finalVersion,
                (information.FileAttributes & (uint)FileAttributes.ReadOnly) != 0);
            contents = null;
            return image;
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            throw new UserFacingException("认证或配置文件无法安全读取，已拒绝切换。");
        }
        finally
        {
            if (contents is not null) CryptographicOperations.ZeroMemory(contents);
            handle.Dispose();
        }
    }

    internal static FileStream CreateTemporary(string path)
    {
        var handle = CreateFile(NativeFilePath(path), GenericRead | GenericWrite | DeleteAccess, 0, IntPtr.Zero,
            1, OpenReparsePoint | WriteThrough, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new UserFacingException("无法创建安全的临时认证文件，请检查目录权限和磁盘空间。");
        }
        try { return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false); }
        catch
        {
            MarkTemporaryForDeletion(handle);
            handle.Dispose();
            throw;
        }
    }

    internal static void MarkTemporaryForDeletion(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        _ = SetFileInformationByHandle(handle, 4, ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    internal static void AssertUnchanged(string path, FileImage? expected, int maxBytes = 1024 * 1024)
    {
        using var current = ReadOptional(path, maxBytes);
        if (!SameImage(expected, current))
            throw new UserFacingException("认证或配置文件在切换期间被其他程序更改，已停止且未覆盖该更改。");
        if (current is { IsReadOnly: true } && Path.GetFileName(path).Equals("auth.json", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("认证文件已被设为只读，已拒绝切换。");
    }

    internal static bool SameImage(FileImage? first, FileImage? second) =>
        first is null ? second is null : second is not null &&
        first.Version.SameFile(second.Version) && first.Version.SameWrite(second.Version) &&
        first.IsReadOnly == second.IsReadOnly &&
        CryptographicOperations.FixedTimeEquals(first.Hash, second.Hash);

    internal static FileVersion GetVersion(SafeFileHandle handle)
    {
        var information = GetInformation(handle);
        return new FileVersion(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            checked((long)(((ulong)information.FileSizeHigh << 32) | information.FileSizeLow)),
            ((ulong)information.LastWriteTime.High << 32) | information.LastWriteTime.Low);
    }

    internal static void AssertHandleSafe(SafeFileHandle handle, string expectedPath)
    {
        if ((GetInformation(handle).FileAttributes & ReparseAttribute) != 0)
            throw new UserFacingException("认证或配置文件包含链接 / reparse point，已拒绝访问。");
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new UserFacingException("无法确认认证文件的实际路径，已拒绝切换。");
        var finalPath = buffer.ToString();
        if (finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) finalPath = finalPath[4..];
        if (!Path.TrimEndingDirectorySeparator(finalPath).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)), StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("认证文件的实际位置与指定路径不一致，已拒绝切换。");
    }

    internal static void DeleteOwnedTemporary(string path, FileVersion? ownedVersion)
    {
        if (ownedVersion is null) return;
        try
        {
            using var image = ReadOptional(path);
            if (image is null || !image.Version.SameFile(ownedVersion)) return;
            AssertUnchanged(path, image);
            File.Delete(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Do not remove a file whose ownership could not be established after a race.
        }
    }

    // Win32 calls need an extended local path independently of .NET File APIs.
    // Normalize first; the existing local-drive, reparse, file-ID and ACL checks remain in effect.
    private static string NativeFilePath(string path)
    {
        var normalized = NormalizeDirectory(path);
        return normalized.Length < 248 ? normalized : @"\\?\" + normalized;
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new UserFacingException("无法读取认证文件身份信息，已拒绝切换。");
        return information;
    }

    internal sealed class FileImage(byte[] bytes, FileVersion version, bool readOnly) : IDisposable
    {
        internal byte[] Bytes { get; } = bytes;
        internal byte[] Hash { get; } = SHA256.HashData(bytes);
        internal FileVersion Version { get; } = version;
        internal bool IsReadOnly { get; } = readOnly;
        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }

    internal sealed record FileVersion(uint Volume, ulong Index, long Length, ulong LastWrite)
    {
        internal bool SameFile(FileVersion other) => Volume == other.Volume && Index == other.Index;
        internal bool SameWrite(FileVersion other) => Length == other.Length && LastWrite == other.LastWrite;
    }

    private sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal void Add(SafeFileHandle handle) => handles.Add(handle);
        public void Dispose()
        {
            foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose();
            handles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { internal uint Low; internal uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref FileDispositionInformation information, uint bufferSize);
}
