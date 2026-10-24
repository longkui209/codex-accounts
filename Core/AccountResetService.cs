using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.Core;

public sealed record ResetCredit(string Id, string Status, string ResetType, DateTimeOffset? GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc, bool ExpiryKnown);
public sealed record ResetCredits(string Status, long? AvailableCount, IReadOnlyList<ResetCredit>? Cards,
    DateTimeOffset? UpdatedAtUtc, DateTimeOffset AttemptedAtUtc, string Message)
{
    public bool Stale { get; init; }
}
public sealed record ResetPreview(string AccountId, string ConfirmationId, ResetCredit Card,
    DateTimeOffset ConfirmBeforeUtc, bool Recovering, string Message);
public sealed record ResetResult(string Code, long? WindowsReset, bool Confirmed, string Message);
internal sealed record ResetAttempt(string ConfirmationId, ResetCredit Card, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ChangedAtUtc, string State, ResetResult? Result);

/// <summary>Account-bound reset metadata and explicit redemption. Never invoked by automatic usage refresh.</summary>
public sealed class AccountResetService : IDisposable
{
    private static readonly Uri DetailsEndpoint = new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
    private static readonly Uri ConsumeEndpoint = new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume");
    private readonly VaultStore vault;
    private readonly Func<string, CancellationToken, Task<LiveCredential>> credentials;
    private readonly Func<string, AccountUsage?> usage;
    private readonly HttpClient http;
    private readonly TimeProvider clock;

    public AccountResetService(VaultStore vault, Func<string, CancellationToken, Task<LiveCredential>> credentials,
        Func<string, AccountUsage?> usage, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        this.vault = vault; this.credentials = credentials; this.usage = usage; this.clock = clock ?? TimeProvider.System;
        http = new HttpClient(handler ?? new SocketsHttpHandler
        { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(4) })
        { Timeout = TimeSpan.FromSeconds(10) };
    }
    private PrivateJobStore<T> Store<T>(string id, string folder) where T : class
    {
        if (!LiveState.ValidId(id)) throw new UserFacingException("账号标识无效。");
        return new PrivateJobStore<T>(Path.Combine(vault.DirectoryPath, folder), id + ".dpapi");
    }
    public ResetCredits? Cached(string id)
    {
        try
        {
            var saved = Store<ResetCredits>(id, "resets").Locked(value => ((ResetCredits?)null, value));
            return saved is null ? null : saved with
            { Stale = saved.Status != "ok" || saved.UpdatedAtUtc is null || clock.GetUtcNow() - saved.UpdatedAtUtc > TimeSpan.FromSeconds(90) };
        }
        catch { return null; }
    }
    public async Task RefreshAllAsync(IEnumerable<string> profiles, bool force, CancellationToken cancellation)
    {
        using var concurrency = new SemaphoreSlim(4);
        await Task.WhenAll(profiles.Distinct(StringComparer.Ordinal).Take(64).Select(async id =>
        {
            var entered = false;
            try
            {
                await concurrency.WaitAsync(cancellation).ConfigureAwait(false); entered = true;
                await RefreshAsync(id, force, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { if (entered) concurrency.Release(); }
        })).ConfigureAwait(false);
    }
    public async Task<ResetCredits> RefreshAsync(string id, bool force, CancellationToken cancellation)
    {
        var cache = Store<ResetCredits>(id, "resets");
        var previous = Cached(id); var now = clock.GetUtcNow();
        if (!force && previous is not null && now - previous.AttemptedAtUtc < TimeSpan.FromSeconds(60)) return previous;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10)); cancellation = deadline.Token;
        ResetCredits result;
        try
        {
            var credential = await CredentialAsync(id, cancellation).ConfigureAwait(false);
            using var request = Request(HttpMethod.Get, DetailsEndpoint, credential);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new IOException();
            using var document = await ReadAsync(response, cancellation).ConfigureAwait(false);
            result = Parse(document.RootElement, clock.GetUtcNow());
        }
        catch
        {
            var summary = usage(id);
            var freshCount = summary is { Status: "ok", Stale: false } ? summary.ResetAvailableCount : null;
            result = new("unavailable", freshCount ?? previous?.AvailableCount, previous?.Cards, previous?.UpdatedAtUtc, now,
                freshCount is null ? "重置卡查询未完成，保留上次结果。" : "数量来自官方用量查询；重置卡详情暂未更新。") { Stale = true };
        }
        return cache.Locked(existing => existing is not null && existing.AttemptedAtUtc > result.AttemptedAtUtc
            ? ((ResetCredits?)null, existing) : ((ResetCredits?)result, result));
    }
    public async Task<ResetPreview> PrepareAsync(string id, string creditId, CancellationToken cancellation)
    {
        if (!ValidCreditId(creditId)) throw new UserFacingException("重置卡标识无效。");
        var attempts = Store<ResetAttempt>(id, "reset-actions"); var now = clock.GetUtcNow();
        var pending = attempts.Locked(value => ((ResetAttempt?)null, value));
        if (pending is { State: "unknown" or "sending" })
        {
            if (pending.Card.Id != creditId) throw new UserFacingException("该账号上次重置结果尚未确认，请先核对同一次操作。");
            if (pending.State == "sending" && now - pending.ChangedAtUtc < TimeSpan.FromSeconds(45))
                throw new UserFacingException("该账号的重置操作仍在处理中。");
            return Preview(id, pending, true);
        }
        var details = await RefreshAsync(id, true, cancellation).ConfigureAwait(false);
        var card = details.Cards?.SingleOrDefault(entry => entry.Id == creditId);
        if (details.Status != "ok" || details.Stale || details.AvailableCount is not > 0 || card is null || !Usable(card, now))
            throw new UserFacingException("该重置卡当前不可用或详情未确认，请刷新后查看。");
        return attempts.Locked(value =>
        {
            if (value is { State: "unknown" or "sending" })
                throw new UserFacingException("已有待确认的重置操作，请刷新后核对。");
            var attempt = new ResetAttempt(Guid.NewGuid().ToString("N"), card, now, now, "prepared", null);
            return (attempt, Preview(id, attempt, false));
        });
    }
    public ResetPreview? Pending(string id)
    {
        try
        {
            var value = Store<ResetAttempt>(id, "reset-actions").Locked(value => ((ResetAttempt?)null, value));
            return value is { State: "unknown" or "sending" } ? Preview(id, value, true) : null;
        }
        catch { return null; }
    }
    public async Task<ResetResult> ConsumeAsync(string id, string confirmationId, bool confirmed, CancellationToken cancellation)
    {
        if (!confirmed || !Guid.TryParseExact(confirmationId, "N", out _))
            throw new UserFacingException("必须先查看重置详情并明确确认使用。");
        var attempts = Store<ResetAttempt>(id, "reset-actions"); var now = clock.GetUtcNow();
        var attempt = attempts.Locked(value =>
        {
            if (value is null || value.ConfirmationId != confirmationId) throw new UserFacingException("重置确认与该账号不匹配。");
            if (value.State == "resolved") return ((ResetAttempt?)null, value);
            if (value.State == "sending" && now - value.ChangedAtUtc < TimeSpan.FromSeconds(45))
                throw new UserFacingException("重置仍在处理中，请勿重复提交。");
            if (value.State == "prepared" && (now - value.CreatedAtUtc > TimeSpan.FromMinutes(10) || !Usable(value.Card, now)))
                throw new UserFacingException("重置确认已过期，请重新查看详情后确认。");
            if (value.State is not ("prepared" or "unknown" or "sending")) throw new UserFacingException("重置状态无法确认。");
            var reserved = value with { State = "sending", ChangedAtUtc = now };
            return (reserved, reserved);
        });
        if (attempt.State == "resolved") return attempt.Result!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10)); cancellation = deadline.Token;
        ResetResult result;
        try
        {
            var credential = await CredentialAsync(id, cancellation).ConfigureAwait(false);
            using var request = Request(HttpMethod.Post, ConsumeEndpoint, credential);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { redeem_request_id = attempt.ConfirmationId, credit_id = attempt.Card.Id });
            try
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new IOException();
                using var document = await ReadAsync(response, cancellation).ConfigureAwait(false);
                result = ParseResult(document.RootElement);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch { result = new("unknown", null, false, "官方结果尚未确认。请核对这同一次操作，勿另用一张卡；不会自动重试或更换账号。"); }
        try { attempts.Locked(value =>
        {
            if (value?.ConfirmationId != attempt.ConfirmationId) throw new UserFacingException("重置记录发生变化，请先刷新状态。");
            return (value with { State = result.Confirmed ? "resolved" : "unknown", Result = result, ChangedAtUtc = clock.GetUtcNow() }, true);
        }); }
        catch { result = result with { Message = result.Message + " 本机结果记录未更新；请核对同一次操作，勿另用其他卡。" }; }
        try { Store<ResetCredits>(id, "resets").Locked(value => (value is null ? null : value with
            { Status = "unavailable", Stale = true, Message = "重置操作后等待官方数量与额度更新。", AttemptedAtUtc = DateTimeOffset.MinValue }, true)); }
        catch { /* A metadata-cache failure must never hide an official redemption outcome. */ }
        return result;
    }
    private ResetPreview Preview(string id, ResetAttempt attempt, bool recovering) => new(id, attempt.ConfirmationId,
        attempt.Card, attempt.CreatedAtUtc.AddMinutes(10), recovering, recovering
            ? "上次结果尚未确认；确认后只核对原账号、原卡、同一次操作，不会新建兑换。"
            : "将使用该账号的 1 张重置卡，恢复符合条件的 Codex 主额度；可能改变下次重置时间。实际适用窗口由官方判断。未满足条件时不应消耗。");
    internal static bool Usable(ResetCredit card, DateTimeOffset now) => card.Status == "available" &&
        card.ResetType == "codex_rate_limits" && card.ExpiryKnown && (card.ExpiresAtUtc is null || card.ExpiresAtUtc > now);
    private static bool ValidCreditId(string id) => id is { Length: > 0 and <= 256 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    private async Task<LiveCredential> CredentialAsync(string id, CancellationToken cancellation)
    {
        var credential = await credentials(id, cancellation).ConfigureAwait(false);
        if (credential.Info.IdentityKey != id) throw new UserFacingException("重置账号身份无法确认。");
        return credential;
    }
    private static HttpRequestMessage Request(HttpMethod method, Uri endpoint, LiveCredential credential)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", credential.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new IOException();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        try
        {
            int read;
            while ((read = await input.ReadAsync(chunk,cancellation).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > 512 * 1024) throw new IOException();
                buffer.Write(chunk,0,read);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 12 }, cancellation).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
    }
    internal static ResetCredits Parse(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("available_count", out var count) ||
            !count.TryGetInt64(out var number) || number is < 0 or > 1_000_000 ||
            !root.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Array) throw new IOException();
        var cards = new List<ResetCredit>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in credits.EnumerateArray().Take(64))
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var rawId) || rawId.ValueKind != JsonValueKind.String) continue;
            var id = rawId.GetString()!; if (!ValidCreditId(id) || !ids.Add(id)) continue;
            var status = item.TryGetProperty("status", out var rawStatus) && rawStatus.ValueKind == JsonValueKind.String &&
                rawStatus.GetString() is "available" or "redeeming" or "redeemed" ? rawStatus.GetString()! : "unknown";
            var type = item.TryGetProperty("reset_type", out var rawType) && rawType.ValueKind == JsonValueKind.String &&
                rawType.GetString() == "codex_rate_limits" ? "codex_rate_limits" : "unknown";
            var granted = item.TryGetProperty("granted_at", out var rawGranted) ? Timestamp(rawGranted) : null;
            var expiryKnown = item.TryGetProperty("expires_at", out var rawExpiry) && rawExpiry.ValueKind == JsonValueKind.Null;
            var expiry = item.TryGetProperty("expires_at", out rawExpiry) ? Timestamp(rawExpiry) : null;
            expiryKnown |= expiry is not null;
            cards.Add(new(id, status, type, granted, expiry, expiryKnown));
        }
        return new("ok", number, cards.OrderBy(card => card.ExpiresAtUtc ?? DateTimeOffset.MaxValue).ToArray(), now, now, "官方重置卡已更新。");
    }
    private static DateTimeOffset? Timestamp(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is { Length: >= 20 and <= 40 } && (text.EndsWith('Z') || text[^6] is '+' or '-') &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) && timestamp.Year >= 1970
            ? timestamp.ToUniversalTime() : null;
    }
    internal static ResetResult ParseResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) throw new IOException();
        long? windows = root.TryGetProperty("windows_reset", out var raw) && raw.ValueKind == JsonValueKind.Number &&
            raw.TryGetInt64(out var number) && number is >= 0 and <= 100 ? number : null;
        return code.GetString() switch
        {
            "reset" => new("reset", windows, true, "官方已确认使用重置卡；正在刷新额度和剩余次数。"),
            "already_redeemed" => new("already_redeemed", windows, true, "官方确认同一次重置已处理，没有再次兑换。"),
            "nothing_to_reset" => new("nothing_to_reset", windows, true, "官方判定当前无需重置，重置卡未使用。"),
            "no_credit" => new("no_credit", windows, true, "官方未找到可用重置卡，没有完成重置。"),
            _ => throw new IOException()
        };
    }
    public void Dispose() => http.Dispose();
}
