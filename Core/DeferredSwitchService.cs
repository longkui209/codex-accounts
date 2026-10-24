using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountSwitcher.Core;

public sealed record PendingSwitch(string Id, string AccountId, string Label, string State,
    string Message, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, bool LaunchAfterSwitch);

/// <summary>Waits for normal client exit using an encrypted request and an ordinary background process.</summary>
public sealed class DeferredSwitchService
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const string FailureMessage = "延迟切换未完成。请保持客户端关闭，打开本地工具检查当前缓存和加密备份后重试；后端登录状态尚未验证。";
    private const string WorkerLostMessage = "后台等待程序已退出，未能确认自动切换。请正常退出客户端后运行本地工具手动切换，并检查当前缓存和加密备份。";
    private const string WorkerNotReadyMessage = "后台等待程序未能确认就绪，自动切换未登记成功。请正常退出客户端后运行本地工具手动切换；当前登录状态未验证。";
    private const string WorkerInterruptedMessage = "后台切换程序已退出，未能确认切换结果。请保持客户端关闭，运行本地工具检查当前缓存和加密备份；不要把本次请求视为成功。";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> FailureStages = new(StringComparer.Ordinal)
    {
        "startup-failed", "ready-timeout", "worker-exited", "worker-failed"
    };
    private static readonly HashSet<string> WorkerStages = new(StringComparer.Ordinal)
    {
        "legacy", "queued", "starting", "ready", "waiting", "switching", "completed", "cancelled", "expired",
        "startup-failed", "ready-timeout", "worker-exited", "worker-failed"
    };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly VaultStore vault;
    private readonly string codexHome;
    private readonly Func<RuntimeStatus> guard;
    private readonly Action launch;
    private readonly Action<string> startWorker;
    private readonly bool monitorWorker;
    private readonly TimeProvider timeProvider;
    private readonly string metadataMutexName;
    private readonly string workerMutexPrefix;
    private string MetadataPath => Path.Combine(vault.DirectoryPath, "deferred-switch.dpapi");

    public DeferredSwitchService(VaultStore vault, string codexHome, Func<RuntimeStatus>? guard = null,
        Action? launch = null, Action<string>? startWorker = null, TimeProvider? timeProvider = null,
        bool? monitorWorker = null)
    {
        this.vault = vault;
        this.codexHome = AuthFileSafety.NormalizeDirectory(codexHome);
        this.guard = guard ?? CodexRuntime.Inspect;
        this.launch = launch ?? CodexRuntime.LaunchDesktop;
        this.startWorker = startWorker ?? StartDefaultWorker;
        this.monitorWorker = monitorWorker ?? (startWorker is null);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UserFacingException("无法确认当前 Windows 用户，已拒绝创建延迟切换。");
        var scope = Encoding.UTF8.GetBytes(sid + "\0" + vault.DirectoryPath.ToUpperInvariant());
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(scope));
            metadataMutexName = "Global\\CodexAccountSwitcher.Deferred.Metadata." + digest;
            workerMutexPrefix = "Global\\CodexAccountSwitcher.Deferred.Worker." + digest + ".";
        }
        finally { CryptographicOperations.ZeroMemory(scope); }
    }

    public PendingSwitch Request(string accountId, bool launchAfterSwitch = true)
    {
        var account = vault.List().FirstOrDefault(candidate => candidate.Id == accountId)
            ?? throw new UserFacingException("所选账号不在本地账号库中，请先通过官方登录添加账号。");
        var pending = WithMetadata(() =>
        {
            var previous = ReadDocument();
            if (previous is not null && IsActive(RefreshState(previous).Pending.State))
                throw new UserFacingException("已有一个延迟切换请求，请先取消它或等待完成。");
            var now = Now;
            var request = new PendingSwitch(Guid.NewGuid().ToString("N"), account.Id, account.Label,
                "pending", "请求已记录。请正常退出所有 Codex / ChatGPT 客户端及 Codex 后台；工具不会关闭程序或中断任务。",
                now, now.AddMinutes(10), launchAfterSwitch);
            WriteDocument(new PendingDocument
            {
                Pending = request, CodexHome = codexHome, MonitorWorker = monitorWorker,
                RequiresWorkerReadyConfirmation = monitorWorker, WorkerStage = "queued"
            });
            return request;
        });
        try
        {
            startWorker(pending.Id);
            if (monitorWorker) return WaitForWorkerReady(pending.Id) ?? throw new UserFacingException(WorkerNotReadyMessage);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _ = RecordWorkerFailure(pending.Id, "startup-failed");
        }
        var status = Status();
        if (monitorWorker && status?.Id != pending.Id) throw new UserFacingException(WorkerNotReadyMessage);
        return status ?? pending;
    }

    public PendingSwitch? Status() => WithMetadata(() =>
    {
        var document = ReadDocument();
        return document is null ? null : RefreshState(document).Pending;
    });

    /// <summary>Stores only a fixed failure stage and an exit code, never exception text or credentials.</summary>
    public bool RecordWorkerFailure(string requestId, string stage, int? exitCode = null)
    {
        if (!IsRequestId(requestId) || !FailureStages.Contains(stage)) return false;
        return WithMetadata(() =>
        {
            var document = ReadDocument();
            if (document is null || document.Pending.Id != requestId) return false;
            AssertSameHome(document);
            if (!IsActive(document.Pending.State))
            {
                // A status poll can notice exit before the exit observer. Preserve
                // its failure cause while filling in the observed safe exit code.
                if (document.Pending.State == "failed" && stage == "worker-exited" &&
                    document.LastExitCode is null && exitCode is not null)
                {
                    document.LastExitCode = exitCode;
                    WriteDocument(document);
                }
                return false;
            }
            var interrupted = document.Pending.State == "switching";
            document.WorkerStage = stage;
            document.LastExitCode = exitCode;
            document.Pending = document.Pending with
            {
                State = "failed",
                Message = interrupted ? WorkerInterruptedMessage :
                    stage is "startup-failed" or "ready-timeout" ? WorkerNotReadyMessage : WorkerLostMessage
            };
            WriteDocument(document);
            return true;
        });
    }

    internal DeferredWorkerHealth? InspectWorkerHealth() => WithMetadata(() =>
    {
        var document = ReadDocument();
        return document is null ? null : new DeferredWorkerHealth(document.WorkerProcessId,
            document.WorkerReadyAtUtc.HasValue, document.WorkerReadyConfirmedAtUtc.HasValue,
            document.WorkerStage, document.LastExitCode);
    });

    private PendingSwitch? WaitForWorkerReady(string requestId)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ReadyTimeout)
        {
            var result = WithMetadata(() =>
            {
                var document = ReadDocument();
                if (document is null || document.Pending.Id != requestId) return (Done: true, Pending: (PendingSwitch?)null);
                AssertSameHome(document);
                if (!IsActive(document.Pending.State)) return (Done: true, Pending: document.Pending);
                if (!document.WorkerReadyAtUtc.HasValue || !WorkerAlive(document))
                    return (Done: false, Pending: (PendingSwitch?)null);
                document.WorkerReadyConfirmedAtUtc = Now;
                WriteDocument(document);
                return (Done: true, Pending: document.Pending);
            });
            if (result.Done) return result.Pending;
            Thread.Sleep(50);
        }
        _ = RecordWorkerFailure(requestId, "ready-timeout");
        return Status();
    }

    public bool Cancel() => WithMetadata(() =>
    {
        var document = ReadDocument();
        if (document is null) return false;
        document = RefreshState(document);
        if (document.Pending.State is not ("pending" or "waiting")) return false;
        document.Pending = document.Pending with { State = "cancelled", Message = "延迟切换已取消，尚未提交该请求的认证缓存替换。" };
        document.WorkerStage = "cancelled";
        WriteDocument(document);
        return true;
    });

    // Windows mutex ownership is thread-specific; the worker remains on one
    // thread instead of carrying an owned mutex across async continuations.
    public Task<int> RunWorkerAsync(string requestId, CancellationToken cancellation) =>
        IsRequestId(requestId)
            ? Task.Run(() => WorkerLoop(requestId, cancellation), CancellationToken.None)
            : Task.FromResult(2);

    private int WorkerLoop(string requestId, CancellationToken cancellation)
    {
        try
        {
            using var workerLease = AcquireMutex(workerMutexPrefix + requestId, 0);
            var started = WithMetadata(() =>
            {
                var document = ReadDocument();
                if (document is null || document.Pending.Id != requestId || !IsActive(document.Pending.State)) return false;
                AssertSameHome(document);
                using var process = Process.GetCurrentProcess();
                document.WorkerProcessId = process.Id;
                document.WorkerStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
                document.WorkerReadyAtUtc = Now;
                document.WorkerStage = "ready";
                WriteDocument(document);
                return true;
            });
            if (!started) return 2;
            if (!WaitForParentReadyConfirmation(requestId, cancellation)) return 2;
            var closedOnce = false;
            while (true)
            {
                var request = WithMetadata(() =>
                {
                    var document = ReadDocument();
                    if (document is null || document.Pending.Id != requestId) return null;
                    AssertSameHome(document);
                    if (!IsActive(document.Pending.State)) return document.Pending;
                    if (cancellation.IsCancellationRequested)
                    {
                        document.Pending = document.Pending with { State = "cancelled", Message = "后台等待已取消，尚未提交该请求的认证缓存替换。" };
                        document.WorkerStage = "cancelled";
                    }
                    else if (Now >= document.Pending.ExpiresAtUtc)
                    {
                        document.Pending = document.Pending with { State = "expired", Message = "请求超过 10 分钟已过期，请重新创建；工具不会自动关闭客户端。" };
                        document.WorkerStage = "expired";
                    }
                    else if (document.Pending.State == "pending")
                    {
                        document.Pending = document.Pending with { State = "waiting", Message = "正在等待用户正常退出所有 Codex / ChatGPT 客户端及 Codex 后台。" };
                        document.WorkerStage = "waiting";
                    }
                    WriteDocument(document);
                    return document.Pending;
                });
                if (request is null || !IsActive(request.State)) return request?.State == "completed" ? 0 : 2;
                var runtime = SafeGuard();
                if (runtime.CanSwitch && closedOnce)
                {
                    var completed = WithMetadata(() => Commit(requestId, cancellation));
                    if (completed is null) return 2;
                    if (completed.State is "pending" or "waiting")
                    {
                        closedOnce = false;
                        _ = cancellation.WaitHandle.WaitOne(PollInterval);
                        continue;
                    }
                    if (completed.State != "completed") return completed.State == "failed" ? 1 : 2;
                    if (completed.LaunchAfterSwitch)
                    {
                        try { launch(); }
                        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                        {
                            UpdateCompleteMessage(requestId, "认证缓存已切换，桌面客户端未能自动启动，请手动打开 Codex 完成账号验证。");
                        }
                    }
                    return 0;
                }
                closedOnce = runtime.CanSwitch;
                _ = cancellation.WaitHandle.WaitOne(PollInterval);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // A duplicate worker cannot change the live worker's request.
            if (error is WorkerAlreadyRunningException) return 2;
            try { _ = RecordWorkerFailure(requestId, "worker-failed"); } catch { }
            return 1;
        }
    }

    private bool WaitForParentReadyConfirmation(string requestId, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ReadyTimeout)
        {
            var state = WithMetadata(() =>
            {
                var document = ReadDocument();
                if (document is null || document.Pending.Id != requestId || !IsActive(document.Pending.State)) return -1;
                AssertSameHome(document);
                return !document.RequiresWorkerReadyConfirmation || document.WorkerReadyConfirmedAtUtc.HasValue ? 1 : 0;
            });
            if (state != 0) return state == 1;
            if (cancellation.WaitHandle.WaitOne(50)) return false;
        }
        _ = RecordWorkerFailure(requestId, "ready-timeout");
        return false;
    }

    private PendingSwitch? Commit(string requestId, CancellationToken cancellation)
    {
        var document = ReadDocument();
        if (document is null || document.Pending.Id != requestId || !IsActive(document.Pending.State)) return null;
        AssertSameHome(document);
        if (cancellation.IsCancellationRequested || Now >= document.Pending.ExpiresAtUtc)
        {
            document.Pending = document.Pending with
            {
                State = cancellation.IsCancellationRequested ? "cancelled" : "expired",
                Message = "请求已取消或过期，已拒绝提交认证缓存替换。"
            };
            document.WorkerStage = document.Pending.State;
            WriteDocument(document);
            return document.Pending;
        }
        if (!SafeGuard().CanSwitch) return document.Pending;
        document.Pending = document.Pending with { State = "switching", Message = "客户端已退出，正在保存加密恢复备份并替换认证缓存。" };
        document.WorkerStage = "switching";
        WriteDocument(document);
        try
        {
            RuntimeStatus CommitGuard()
            {
                if (cancellation.IsCancellationRequested || Now >= document.Pending.ExpiresAtUtc)
                    return new(false, "请求已取消或过期。", Array.Empty<string>());
                return SafeGuard();
            }
            var backup = new AccountSwitchService(vault, codexHome, CommitGuard).Switch(document.Pending.AccountId);
            document.Pending = document.Pending with
            {
                State = "completed",
                Message = string.IsNullOrEmpty(backup)
                    ? "所选账号的本地认证缓存已就绪；账号是否有效，以 Codex 启动后的后端验证为准。"
                    : "认证缓存已切换并保存加密恢复备份；账号是否有效，以 Codex 启动后的后端验证为准。"
            };
            document.WorkerStage = "completed";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            document.Pending = document.Pending with { State = "failed", Message = FailureMessage };
            document.WorkerStage = "worker-failed";
        }
        WriteDocument(document);
        return document.Pending;
    }

    private RuntimeStatus SafeGuard()
    {
        try { return guard() ?? throw new InvalidOperationException(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            throw new UserFacingException("无法可靠确认客户端及后台运行状态，已停止延迟切换。");
        }
    }

    private PendingDocument RefreshState(PendingDocument document)
    {
        AssertSameHome(document);
        if (!IsActive(document.Pending.State)) return document;
        if (Now >= document.Pending.ExpiresAtUtc)
        {
            document.Pending = document.Pending with { State = "expired", Message = "请求超过 10 分钟已过期，请重新创建；工具不会自动关闭客户端。" };
            document.WorkerStage = "expired";
        }
        else if (document.MonitorWorker && Now - document.Pending.CreatedAtUtc > TimeSpan.FromSeconds(5) && !WorkerAlive(document))
        {
            document.Pending = document.Pending with
            {
                State = "failed", Message = document.Pending.State == "switching" ? WorkerInterruptedMessage : WorkerLostMessage
            };
            document.WorkerStage = "worker-exited";
        }
        else return document;
        WriteDocument(document);
        return document;
    }

    private static bool WorkerAlive(PendingDocument document)
    {
        if (document.WorkerProcessId <= 0 || document.WorkerStartedAtUtc is null) return false;
        try
        {
            using var process = Process.GetProcessById(document.WorkerProcessId);
            return !process.HasExited &&
                new DateTimeOffset(process.StartTime.ToUniversalTime()) == document.WorkerStartedAtUtc;
        }
        catch { return false; }
    }

    private void UpdateCompleteMessage(string id, string message) => WithMetadata(() =>
    {
        var document = ReadDocument();
        if (document?.Pending.Id == id && document.Pending.State == "completed")
        {
            document.Pending = document.Pending with { Message = message };
            WriteDocument(document);
        }
        return true;
    });

    private void StartDefaultWorker(string requestId)
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !File.Exists(executable) ||
            !Path.GetFileNameWithoutExtension(executable).Equals("CodexAccountSwitcher", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException(WorkerLostMessage);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--deferred");
        start.ArgumentList.Add(requestId);
        _ = StartWorkerProcess(start, requestId);
    }

    // The synthetic process harness uses this same spawn/registration/observation path,
    // changing only the worker arguments to its strictly isolated entry point.
    internal int StartWorkerProcess(ProcessStartInfo start, string requestId)
    {
        if (!IsRequestId(requestId)) throw new UserFacingException(WorkerNotReadyMessage);
        var process = Process.Start(start) ?? throw new UserFacingException(WorkerLostMessage);
        try
        {
            WithMetadata(() =>
            {
                var document = ReadDocument();
                if (document?.Pending.Id == requestId && IsActive(document.Pending.State))
                {
                    document.WorkerProcessId = process.Id;
                    document.WorkerStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
                    if (document.WorkerReadyAtUtc is null) document.WorkerStage = "starting";
                    WriteDocument(document);
                }
                return true;
            });
            var processId = process.Id;
            _ = ObserveWorkerAsync(process, requestId);
            return processId;
        }
        catch { process.Dispose(); throw; }
    }

    internal async Task ObserveWorkerAsync(Process process, string requestId)
    {
        // Never inherit MCP protocol handles or print a child's diagnostic output.
        using var stopDraining = new CancellationTokenSource();
        async Task Drain(StreamReader reader)
        {
            var buffer = new char[1024];
            try { while (await reader.ReadAsync(buffer.AsMemory(), stopDraining.Token).ConfigureAwait(false) > 0) Array.Clear(buffer); }
            catch (OperationCanceledException) { }
            finally { Array.Clear(buffer); }
        }
        var output = Drain(process.StandardOutput);
        var error = Drain(process.StandardError);
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.HasExited) _ = RecordWorkerFailure(requestId, "worker-exited", process.ExitCode);
        }
        catch { /* Status detects the recorded process's premature exit. */ }
        finally
        {
            stopDraining.Cancel();
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            process.Dispose();
        }
    }

    private T WithMetadata<T>(Func<T> action)
    {
        try
        {
            using var mutex = AcquireMutex(metadataMutexName, 2000);
            Directory.CreateDirectory(vault.DirectoryPath);
            using var directories = AuthFileSafety.ProtectDirectories(vault.DirectoryPath);
            FilePrivacy.ApplyDirectory(vault.DirectoryPath);
            return action();
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            throw new UserFacingException("无法安全读取或保存延迟切换请求，请检查账号库权限后重试。");
        }
    }

    private PendingDocument? ReadDocument()
    {
        using var image = AuthFileSafety.ReadOptional(MetadataPath, MaximumMetadataBytes);
        if (image is null) return null;
        byte[]? plaintext = null;
        try
        {
            plaintext = SecretProtection.Unprotect(image.Bytes);
            var document = JsonSerializer.Deserialize<PendingDocument>(plaintext, JsonOptions);
            var pending = document?.Pending;
            if (document is null || document.Version != 1 || pending is null || !IsRequestId(pending.Id) ||
                pending.AccountId.Length != 64 || pending.AccountId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                string.IsNullOrWhiteSpace(pending.Label) || pending.Label.Length > 80 || pending.Label.Any(char.IsControl) ||
                pending.Message.Length > 512 || pending.Message.Any(char.IsControl) ||
                pending.State is not ("pending" or "waiting" or "switching" or "completed" or "cancelled" or "expired" or "failed") ||
                pending.CreatedAtUtc.Offset != TimeSpan.Zero || pending.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                pending.ExpiresAtUtc - pending.CreatedAtUtc != TimeSpan.FromMinutes(10) ||
                pending.CreatedAtUtc > Now.AddMinutes(1))
                throw new UserFacingException("延迟切换请求已损坏，已拒绝自动切换。");
            if (!WorkerStages.Contains(document.WorkerStage) || document.WorkerProcessId < 0 ||
                document.WorkerReadyAtUtc is { } ready && (ready.Offset != TimeSpan.Zero || ready < pending.CreatedAtUtc || ready > Now.AddMinutes(1)) ||
                document.WorkerReadyConfirmedAtUtc is { } confirmed &&
                    (confirmed.Offset != TimeSpan.Zero || document.WorkerReadyAtUtc is null || confirmed < document.WorkerReadyAtUtc || confirmed > Now.AddMinutes(1)))
                throw new UserFacingException("延迟切换后台元数据无效，已拒绝自动切换。");
            return document;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            throw new UserFacingException("无法解密或验证延迟切换请求，已拒绝自动切换。");
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void WriteDocument(PendingDocument document)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        byte[]? encrypted = null;
        var temporary = Path.Combine(vault.DirectoryPath, ".deferred-" + Guid.NewGuid().ToString("N") + ".tmp");
        AuthFileSafety.FileVersion? version = null;
        try
        {
            encrypted = SecretProtection.Protect(plaintext);
            if (encrypted.Length > MaximumMetadataBytes) throw new UserFacingException("延迟切换请求大小异常，已拒绝保存。");
            using var previous = AuthFileSafety.ReadOptional(MetadataPath, MaximumMetadataBytes);
            using (var stream = AuthFileSafety.CreateTemporary(temporary))
            {
                version = AuthFileSafety.GetVersion(stream.SafeFileHandle);
                FilePrivacy.ApplyFile(temporary);
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            AuthFileSafety.AssertUnchanged(MetadataPath, previous, MaximumMetadataBytes);
            if (previous is null) File.Move(temporary, MetadataPath);
            else File.Replace(temporary, MetadataPath, null);
            FilePrivacy.ApplyFile(MetadataPath);
            using var verified = AuthFileSafety.ReadOptional(MetadataPath, MaximumMetadataBytes);
            if (verified is null || version is null || !verified.Version.SameFile(version) ||
                !CryptographicOperations.FixedTimeEquals(encrypted, verified.Bytes))
                throw new UserFacingException("延迟切换请求写入校验失败，已停止操作。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            AuthFileSafety.DeleteOwnedTemporary(temporary, version);
        }
    }

    private void AssertSameHome(PendingDocument document)
    {
        if (!string.Equals(document.CodexHome, codexHome, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("该请求对应其他 Codex 主目录，已拒绝自动切换。");
    }

    private DateTimeOffset Now => timeProvider.GetUtcNow();
    private static bool IsActive(string state) => state is "pending" or "waiting" or "switching";
    private static bool IsRequestId(string? id) => id is not null && id.Length == 32 &&
        id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IDisposable AcquireMutex(string name, int milliseconds)
    {
        var mutex = new Mutex(false, name);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                if (milliseconds == 0) throw new WorkerAlreadyRunningException();
                throw new UserFacingException("另一个延迟切换操作正在进行，请稍后再试。");
            }
            return new MutexLease(mutex);
        }
        catch
        {
            if (acquired) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private sealed class WorkerAlreadyRunningException : Exception { }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); } }
    }

    private sealed class PendingDocument
    {
        public PendingDocument() { }
        public int Version { get; set; } = 1;
        public PendingSwitch Pending { get; set; } = null!;
        public string CodexHome { get; set; } = string.Empty;
        public bool MonitorWorker { get; set; }
        public int WorkerProcessId { get; set; }
        public DateTimeOffset? WorkerStartedAtUtc { get; set; }
        public bool RequiresWorkerReadyConfirmation { get; set; }
        public DateTimeOffset? WorkerReadyAtUtc { get; set; }
        public DateTimeOffset? WorkerReadyConfirmedAtUtc { get; set; }
        public string WorkerStage { get; set; } = "legacy";
        public int? LastExitCode { get; set; }
    }
}

internal sealed record DeferredWorkerHealth(int ProcessId, bool Ready, bool ReadyConfirmed, string Stage, int? ExitCode);
