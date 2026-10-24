using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.PluginHost;

internal sealed class PrivateJobStore<T>(string directory, string name) where T : class
{
    private readonly string path = Path.Combine(directory, name);
    public TResult Locked<TResult>(Func<T?, (T? Save, TResult Result)> action)
    {
        using var user = WindowsIdentity.GetCurrent();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path) + user.User?.Value)));
        using var mutex = new Mutex(false, "Local\\CodexAccountsPlugin.Job." + key);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new UserFacingException("账号操作正在进行，请稍后重试。");
            AssertOrdinaryDirectory();
            Directory.CreateDirectory(directory);
            FilePrivacy.ApplyDirectory(directory);
            T? document = null;
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("任务文件包含链接，操作已取消。");
                var encrypted = File.ReadAllBytes(path);
                byte[]? plain = null;
                try
                {
                    if (encrypted.Length > 128 * 1024) throw new UserFacingException("任务文件大小异常。");
                    plain = SecretProtection.Unprotect(encrypted);
                    document = JsonSerializer.Deserialize<T>(plain);
                }
                finally { CryptographicOperations.ZeroMemory(encrypted); if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
            }
            var outcome = action(document);
            if (outcome.Save is not null)
            {
                var plain = JsonSerializer.SerializeToUtf8Bytes(outcome.Save);
                byte[]? encrypted = null;
                var temporary = Path.Combine(directory, ".plugin-job-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    encrypted = SecretProtection.Protect(plain);
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        FilePrivacy.ApplyFile(temporary);
                        file.Write(encrypted);
                        file.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    FilePrivacy.ApplyFile(path);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                    if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
                    if (File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) File.Delete(temporary);
                }
            }
            return outcome.Result;
        }
        catch (UserFacingException) { throw; }
        catch { throw new UserFacingException("本机加密任务状态无法读写，操作已取消。"); }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private void AssertOrdinaryDirectory()
    {
        for (var cursor = new DirectoryInfo(Path.GetFullPath(directory)); cursor is not null; cursor = cursor.Parent)
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UserFacingException("任务目录不能包含链接。");
    }
}
