using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.Validation;

/// <summary>Actual-process deferred checks. Only its fresh synthetic root is accessible; no desktop is launched.</summary>
public static class DeferredWorkerProtocolHarness
{
    private static readonly string AllowedParent = ValidationPaths.OutputDirectory;
    private static readonly HashSet<string> Cases = new(StringComparer.Ordinal)
    {
        "complete", "early-exit", "no-ready", "lost-after-ready", "parent-exit"
    };
    private const string ClosedMarker = "synthetic-clients-closed";
    private const string StopMarker = "synthetic-worker-stop";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 4 };

    public static async Task RunParentAsync(string isolatedRoot, Stream input, Stream output)
    {
        var root = ValidateRoot(isolatedRoot);
        using var directories = AuthFileSafety.ProtectDirectories(root);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var initialDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await reader.ReadLineAsync(initialDeadline.Token).ConfigureAwait(false);
        if (first is null || first.Length > 1024) throw Refusal();
        using var firstDocument = JsonDocument.Parse(first, new JsonDocumentOptions { MaxDepth = 3 });
        var initial = firstDocument.RootElement;
        var mode = Text(initial, "mode");
        if (mode is not ("create" or "inspect")) throw Refusal();
        var initialNames = initial.EnumerateObject().Select(property => property.Name).ToArray();
        if (initialNames.Distinct(StringComparer.Ordinal).Count() != initialNames.Length ||
            mode == "create" && (initialNames.Length != 2 || initialNames.Any(name => name is not ("mode" or "caseMode"))) ||
            mode == "inspect" && (initialNames.Length != 1 || initialNames[0] != "mode")) throw Refusal();
        string caseMode;
        var home = Path.Combine(root, "codex-home");
        var vault = new VaultStore(Path.Combine(root, "vault"));
        if (mode == "create")
        {
            caseMode = Text(initial, "caseMode");
            if (!Cases.Contains(caseMode) || Directory.EnumerateFileSystemEntries(root).Any()) throw Refusal();
            Directory.CreateDirectory(home);
            FilePrivacy.ApplyDirectory(home);
            var original = CreateAuth("original");
            var target = CreateAuth("target");
            try
            {
                File.WriteAllBytes(Path.Combine(home, "auth.json"), original);
                FilePrivacy.ApplyFile(Path.Combine(home, "auth.json"));
                File.WriteAllBytes(Path.Combine(root, "original.synthetic.json"), original);
                FilePrivacy.ApplyFile(Path.Combine(root, "original.synthetic.json"));
                using var auth = AuthSnapshot.FromBytes(target);
                vault.Save(auth, "Synthetic process target");
            }
            finally { CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(target); }
        }
        else
        {
            var fixture = ReadFixture(root);
            caseMode = fixture.CaseMode;
            ValidateData(root, vault);
        }

        DeferredSwitchService? service = null;
        var workerPid = 0;
        var parentExit = false;
        service = new DeferredSwitchService(vault, home, () => FixtureGuard(root, vault),
            () => throw Refusal(), requestId =>
            {
                WriteFixture(root, new Fixture(1, caseMode, requestId));
                workerPid = service!.StartWorkerProcess(WorkerStart(root, requestId), requestId);
            }, monitorWorker: true);
        var targetId = ValidateData(root, vault);
        PendingSwitch? pending;
        try
        {
            pending = mode == "create" ? service.Request(targetId, false) : service.Status();
            if (pending is null) throw Refusal();
            var requestId = pending.Id;
            await ReportAsync(writer, 0, service, root, vault, workerPid, true).ConfigureAwait(false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 1024) throw Refusal();
                using var commandDocument = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 3 });
                var command = commandDocument.RootElement;
                var id = command.GetProperty("id").GetInt32();
                var operation = Text(command, "operation");
                var duplicateExitCode = (int?)null;
                switch (operation)
                {
                    case "status": break;
                    case "cancel": _ = service.Cancel(); break;
                    case "close": WriteMarker(root, "closed.marker", ClosedMarker); break;
                    case "stop-worker": WriteMarker(root, "stop.marker", StopMarker); break;
                    case "wait-exit":
                        await WaitForExitAsync(service.InspectWorkerHealth()?.ProcessId ?? workerPid).ConfigureAwait(false);
                        await WaitForExitEvidenceAsync(service).ConfigureAwait(false);
                        break;
                    case "duplicate":
                        using (var duplicate = Process.Start(WorkerStart(root, requestId)) ?? throw Refusal())
                        {
                            var stdout = duplicate.StandardOutput.ReadToEndAsync();
                            var stderr = duplicate.StandardError.ReadToEndAsync();
                            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                            duplicateExitCode = duplicate.ExitCode;
                            // Synthetic children do not emit credentials or arbitrary diagnostics.
                            if ((await stdout.ConfigureAwait(false)).Length != 0 || (await stderr.ConfigureAwait(false)).Length != 0) throw Refusal();
                        }
                        break;
                    case "parent-exit":
                        if (caseMode != "parent-exit") throw Refusal();
                        parentExit = true;
                        break;
                    default: throw Refusal();
                }
                await ReportAsync(writer, id, service, root, vault, workerPid, false, duplicateExitCode, parentExit).ConfigureAwait(false);
                if (parentExit) return;
            }
        }
        finally
        {
            if (!parentExit && mode == "create")
            {
                WriteMarker(root, "stop.marker", StopMarker);
                try { await WaitForExitAsync(workerPid).ConfigureAwait(false); } catch { }
            }
        }
    }

    public static async Task<int> RunWorkerAsync(string isolatedRoot, string requestId)
    {
        var root = ValidateRoot(isolatedRoot);
        using var directories = AuthFileSafety.ProtectDirectories(root);
        var fixture = ReadFixture(root);
        if (!Guid.TryParseExact(requestId, "N", out _) || fixture.RequestId != requestId) throw Refusal();
        var vault = new VaultStore(Path.Combine(root, "vault"));
        _ = ValidateData(root, vault);
        if (fixture.CaseMode == "early-exit") return 7;
        if (fixture.CaseMode == "no-ready")
        {
            await Task.Delay(5500).ConfigureAwait(false);
            return 8;
        }
        var service = new DeferredSwitchService(vault, Path.Combine(root, "codex-home"), () => FixtureGuard(root, vault),
            () => throw Refusal(), _ => throw Refusal(), monitorWorker: true);
        var run = service.RunWorkerAsync(requestId, CancellationToken.None);
        while (!run.IsCompleted)
        {
            if (HasMarker(root, "stop.marker", StopMarker)) return 7;
            await Task.Delay(50).ConfigureAwait(false);
        }
        return await run.ConfigureAwait(false);
    }

    private static async Task ReportAsync(StreamWriter writer, int id, DeferredSwitchService service, string root, VaultStore vault,
        int workerPid, bool initial, int? duplicateExitCode = null, bool parentExit = false)
    {
        var targetId = ValidateData(root, vault);
        var pending = service.Status() ?? throw Refusal();
        var health = service.InspectWorkerHealth() ?? throw Refusal();
        using var current = AuthSnapshot.Read(Path.Combine(root, "codex-home", "auth.json"));
        using var original = AuthSnapshot.Read(Path.Combine(root, "original.synthetic.json"));
        var backupMatchesOriginal = false;
        if (vault.LatestBackupFileName is { } backupFile)
        {
            using var backup = vault.ReadBackup(backupFile);
            var restored = backup.CopyBytes();
            var expected = original.CopyBytes();
            try { backupMatchesOriginal = CryptographicOperations.FixedTimeEquals(restored, expected); }
            finally { CryptographicOperations.ZeroMemory(restored); CryptographicOperations.ZeroMemory(expected); }
        }
        var actualPid = health.ProcessId > 0 ? health.ProcessId : workerPid;
        var alive = false;
        if (actualPid > 0)
        {
            try { using var worker = Process.GetProcessById(actualPid); alive = !worker.HasExited; } catch { }
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            id, ready = initial, syntheticOnly = true, realDesktopConnected = false,
            state = pending.State, workerReady = health.Ready, readyConfirmed = health.ReadyConfirmed,
            stage = health.Stage, exitCode = health.ExitCode, workerPid = actualPid, workerAlive = alive,
            cache = current.Info.IdentityKey == original.Info.IdentityKey ? "original" : current.Info.IdentityKey == targetId ? "target" : "unknown",
            encryptedBackup = vault.LatestBackupFileName is not null, backupMatchesOriginal,
            duplicateExitCode, parentExit
        }, JsonOptions)).ConfigureAwait(false);
    }

    private static ProcessStartInfo WorkerStart(string root, string requestId)
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !File.Exists(executable) ||
            !Path.GetFileNameWithoutExtension(executable).Equals("CodexAccountSwitcher", StringComparison.OrdinalIgnoreCase)) throw Refusal();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--deferred-test");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(requestId);
        start.Environment["CODEX_HOME"] = Path.Combine(root, "codex-home");
        start.Environment.Remove("OPENAI_API_KEY");
        return start;
    }

    private static async Task WaitForExitAsync(int processId)
    {
        if (processId <= 0) return;
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
    }

    private static async Task WaitForExitEvidenceAsync(DeferredSwitchService service)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(2))
        {
            var state = service.Status()?.State;
            if (state is "completed" or "cancelled" or "expired" ||
                state == "failed" && service.InspectWorkerHealth()?.ExitCode is not null) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static RuntimeStatus FixtureGuard(string root, VaultStore vault)
    {
        _ = ValidateData(root, vault);
        return HasMarker(root, "closed.marker", ClosedMarker)
            ? new(true, "合成客户端已关闭。", []) : new(false, "合成客户端运行中。", []);
    }

    private static string ValidateData(string root, VaultStore vault)
    {
        var profiles = vault.List();
        var targetId = IdentityKey("target");
        var originalId = IdentityKey("original");
        // The real switch service preserves the previous identity in the vault
        // before backup/replace. Both identities are fixed synthetic fixtures.
        if (profiles.Count is < 1 or > 2 || !profiles.Any(profile => profile.Id == targetId) ||
            profiles.Any(profile => profile.Id != targetId && profile.Id != originalId)) throw Refusal();
        foreach (var profile in profiles)
        {
            using var saved = vault.Read(profile.Id);
            ValidateAuth(saved, profile.Id == targetId ? "target" : "original");
        }
        using (var original = AuthSnapshot.Read(Path.Combine(root, "original.synthetic.json"))) ValidateAuth(original, "original");
        using (var current = AuthSnapshot.Read(Path.Combine(root, "codex-home", "auth.json"))) ValidateAuth(current, null);
        return targetId;
    }

    private static string IdentityKey(string suffix) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("synthetic-process-" + suffix + "\0synthetic-process-subject-" + suffix))).ToLowerInvariant();

    private static void ValidateAuth(AuthSnapshot snapshot, string? expected)
    {
        var bytes = snapshot.CopyBytes();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var tokens = document.RootElement.GetProperty("tokens");
            var accountId = Text(tokens, "account_id");
            var suffix = accountId == "synthetic-process-original" ? "original" : accountId == "synthetic-process-target" ? "target" : throw Refusal();
            if (expected is not null && suffix != expected || Text(tokens, "refresh_token") != "synthetic-process-refresh-" + suffix) throw Refusal();
            foreach (var name in new[] { "id_token", "access_token" })
            {
                var parts = Text(tokens, name).Split('.');
                if (parts.Length != 3 || parts[2] != "syntheticSignature") throw Refusal();
                var payload = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '='));
                try
                {
                    using var claims = JsonDocument.Parse(payload);
                    if (Text(claims.RootElement, "sub") != "synthetic-process-subject-" + suffix ||
                        claims.RootElement.GetProperty("exp").GetInt64() < DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()) throw Refusal();
                }
                finally { CryptographicOperations.ZeroMemory(payload); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] CreateAuth(string suffix)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = "synthetic-process-subject-" + suffix, ["email"] = "synthetic@example.test",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new Dictionary<string, string> { ["chatgpt_plan_type"] = "plus" }
        });
        try
        {
            var jwt = "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".syntheticSignature";
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                auth_mode = "chatgpt", tokens = new
                {
                    account_id = "synthetic-process-" + suffix, id_token = jwt, access_token = jwt,
                    refresh_token = "synthetic-process-refresh-" + suffix
                }
            });
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static Fixture ReadFixture(string root)
    {
        using var file = AuthFileSafety.ReadOptional(Path.Combine(root, "fixture.json"), 1024) ?? throw Refusal();
        var fixture = JsonSerializer.Deserialize<Fixture>(file.Bytes, JsonOptions) ?? throw Refusal();
        if (fixture.Version != 1 || !Cases.Contains(fixture.CaseMode) || !Guid.TryParseExact(fixture.RequestId, "N", out _)) throw Refusal();
        return fixture;
    }

    private static void WriteFixture(string root, Fixture fixture)
    {
        File.WriteAllText(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(fixture, JsonOptions), new UTF8Encoding(false));
        FilePrivacy.ApplyFile(Path.Combine(root, "fixture.json"));
    }

    private static bool HasMarker(string root, string fileName, string expected)
    {
        using var file = AuthFileSafety.ReadOptional(Path.Combine(root, fileName), 64);
        return file is not null && Encoding.UTF8.GetString(file.Bytes) == expected;
    }

    private static void WriteMarker(string root, string fileName, string value)
    {
        File.WriteAllText(Path.Combine(root, fileName), value, new UTF8Encoding(false));
        FilePrivacy.ApplyFile(Path.Combine(root, fileName));
    }

    private static string ValidateRoot(string isolatedRoot)
    {
        var root = Path.GetFullPath(isolatedRoot);
        const string prefix = "deferred-process-";
        var name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), AllowedParent, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) || !Directory.Exists(root)) throw Refusal();
        return root;
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw Refusal();
    private static UserFacingException Refusal() => new("真实进程隔离验证拒绝了非合成目录、凭据或协议；未访问用户账号。");
    private sealed record Fixture(int Version, string CaseMode, string RequestId);
}
