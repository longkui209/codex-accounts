using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.LiveSession;

public sealed class LiveState
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string DirectoryPath { get; }
    public LiveState(VaultStore vault, string scope = "live")
    {
        if (scope is not ("live" or "routing")) throw new ArgumentException("Unsupported state scope.");
        DirectoryPath = Path.Combine(vault.DirectoryPath, scope);
    }
    public string? SelectedProfile()
    {
        try { return Read<Selection>("selection.dpapi")?.ProfileId is { } id && ValidId(id) ? id : null; }
        catch { return null; }
    }
    public void Select(string profileId)
    {
        if (!ValidId(profileId)) throw new UserFacingException("账号标识无效。");
        Write("selection.dpapi", new Selection(profileId));
    }
    public LiveDescriptor? Descriptor()
    {
        try
        {
            var value = Read<LiveDescriptor>("descriptor.dpapi");
            if (value is null || !Guid.TryParseExact(value.InstanceId, "N", out _) ||
                value.ControlPipe != "codex-accounts-control-" + value.InstanceId ||
                value.DesktopPipe != "codex-accounts-desktop-" + value.InstanceId) return null;
            using var owner = Process.GetProcessById(value.OwnerPid);
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != value.OwnerStartTicks ||
                !owner.ProcessName.Equals("CodexAccountSwitcher", StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }
        catch { return null; }
    }
    public void Publish(LiveDescriptor descriptor) => Write("descriptor.dpapi", descriptor);
    public void RemoveOwned(string instanceId)
    {
        try
        {
            using var mutation = AcquireMutationLease();
            var value = Read<LiveDescriptor>("descriptor.dpapi");
            if (value?.InstanceId == instanceId) File.Delete(Path.Combine(DirectoryPath, "descriptor.dpapi"));
        }
        catch { }
    }
    public FileStream AcquireHostLease()
    {
        EnsureDirectory();
        var path = Path.Combine(DirectoryPath, "host.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UserFacingException("直切状态目录含链接，已拒绝启动。");
        try
        {
            var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { FilePrivacy.ApplyFile(path); return lease; } catch { lease.Dispose(); throw; }
        }
        catch (IOException) { throw new UserFacingException("账号直切模式已在运行。"); }
    }
    private void EnsureDirectory()
    {
        Directory.CreateDirectory(DirectoryPath);
        FilePrivacy.ApplyDirectory(DirectoryPath);
    }
    private T? Read<T>(string name)
    {
        if (!Directory.Exists(DirectoryPath)) return default;
        using var directoryLease = AuthFileSafety.ProtectDirectories(DirectoryPath);
        using var image = AuthFileSafety.ReadOptional(Path.Combine(DirectoryPath, name), 64 * 1024);
        if (image is null) return default;
        var plain = SecretProtection.Unprotect(image.Bytes);
        try { return JsonSerializer.Deserialize<T>(plain, Options); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Write<T>(string name, T data)
    {
        using var mutation = AcquireMutationLease();
        EnsureDirectory();
        using var directoryLease = AuthFileSafety.ProtectDirectories(DirectoryPath);
        var plain = JsonSerializer.SerializeToUtf8Bytes(data, Options);
        byte[] encrypted;
        try { encrypted = SecretProtection.Protect(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        var destination = Path.Combine(DirectoryPath, name);
        try
        {
            using var existing = AuthFileSafety.ReadOptional(destination, 64 * 1024);
            File.WriteAllBytes(temporary, encrypted);
            FilePrivacy.ApplyFile(temporary);
            if (existing is not null) AuthFileSafety.AssertUnchanged(destination, existing);
            File.Move(temporary, destination, overwrite:true);
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private FileStream AcquireMutationLease()
    {
        EnsureDirectory();
        using var directories = AuthFileSafety.ProtectDirectories(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "state.lock");
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new UserFacingException("账号状态文件包含链接，操作已取消。");
            try
            {
                var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { FilePrivacy.ApplyFile(path); return lease; } catch { lease.Dispose(); throw; }
            }
            catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(4)) { Thread.Sleep(30); }
        }
    }
    internal static bool ValidId(string? value) => value is { Length:64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed record Selection(string ProfileId);
}
