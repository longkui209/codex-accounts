using System.Diagnostics;
using System.IO;

namespace CodexAccountSwitcher.Core;

public sealed class OfficialLogin
{
    private readonly VaultStore vault;
    private readonly Func<AuthSnapshot, string?, AccountSummary> saveProfile;
    public OfficialLogin(VaultStore vaultStore, Func<AuthSnapshot, string?, AccountSummary>? saveProfile = null)
    {
        vault = vaultStore;
        this.saveProfile = saveProfile ?? vault.Save;
    }

    public async Task<AccountSummary> AddAccountAsync(string? label, CancellationToken cancellation)
    {
        var executable = CodexRuntime.FindCli();
        if (executable is null) throw new UserFacingException("未找到官方 Codex 命令行程序。请先安装并启动一次 Codex 桌面版。");
        // Login runs in its own home. Adding an account never replaces the live user's auth.json.
        var loginRoot = Path.Combine(vault.DirectoryPath, "login");
        Directory.CreateDirectory(loginRoot);
        FilePrivacy.ApplyDirectory(loginRoot);
        var pending = Path.Combine(loginRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending);
        FilePrivacy.ApplyDirectory(pending);
        Process? process = null;
        var savedAccount = false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = pending
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            start.ArgumentList.Add("login");
            start.Environment["CODEX_HOME"] = pending;
            foreach (var credentialVariable in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" })
                start.Environment.Remove(credentialVariable);
            process = Process.Start(start) ?? throw new UserFacingException("官方登录程序启动失败。");
            // Consume and discard output. Login URLs, codes and tokens are never written to logs.
            var outputDrain = DrainAsync(process.StandardOutput);
            var errorDrain = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(limit.Token);
            await Task.WhenAll(outputDrain, errorDrain);
            if (process.ExitCode != 0) throw new UserFacingException("官方登录未完成。请确认浏览器已登录目标账号后再试，原账号没有被更改。");
            using var snapshot = AuthSnapshot.Read(Path.Combine(pending, "auth.json"));
            cancellation.ThrowIfCancellationRequested();
            var saved = saveProfile(snapshot, label);
            savedAccount = true;
            return saved;
        }
        catch (OperationCanceledException)
        {
            throw new UserFacingException(cancellation.IsCancellationRequested ? "已取消添加账号。原 Codex 登录保持不变。" : "等待登录超过 5 分钟，请重新添加账号。");
        }
        catch (UserFacingException) { throw; }
        catch { throw new UserFacingException("添加账号未完成。请重新登录；没有修改当前 Codex 账号。"); }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: false);
                    using var exitLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(exitLimit.Token);
                }
                catch
                {
                    throw new UserFacingException("本工具的登录进程尚未确认退出，临时目录已保持仅当前用户可访问。请重新打开工具前先结束该登录进程。");
                }
                finally { process.Dispose(); }
            }
            // Only our randomly named child directory is eligible for cleanup; never CODEX_HOME itself.
            var cleaned = false;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try { RemovePendingDirectory(pending, loginRoot); cleaned = !Directory.Exists(pending); }
                catch { cleaned = false; }
                if (cleaned) break;
                await Task.Delay(150);
            }
            if (!cleaned)
                throw new UserFacingException(savedAccount
                    ? "账号已经加密保存，但临时登录缓存仍被系统占用，未能清理。暂存目录仅当前 Windows 用户可访问，请解除占用后清理账号库的 login 子目录。"
                    : "登录已停止，但临时缓存仍被系统占用。暂存目录仅当前 Windows 用户可访问，请解除占用后清理账号库的 login 子目录。");
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static void RemovePendingDirectory(string directory, string loginRoot)
    {
        var absolute = Path.GetFullPath(directory);
        var prefix = Path.GetFullPath(loginRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(absolute)) return;
        if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(absolute, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UserFacingException("临时登录目录包含链接，已停止清理。");
            if ((attributes & FileAttributes.Directory) != 0) RemovePendingDirectory(entry, loginRoot);
            else File.Delete(entry);
        }
        Directory.Delete(absolute, recursive: false);
    }
}
