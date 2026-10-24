using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.Core;

public sealed record UsageWindow(double? UsedPercent, long? LimitWindowSeconds, DateTimeOffset? ResetsAtUtc);
public sealed record AccountUsage(string Status, string? Plan, UsageWindow? Primary, UsageWindow? Secondary,
    UsageWindow? CodeReview, DateTimeOffset? UpdatedAtUtc, DateTimeOffset AttemptedAtUtc, string Message)
{
    public bool Stale { get; init; }
    public long? ResetAvailableCount { get; init; }
    public bool? UsageAllowed { get; init; }
}

/// <summary>Official non-inference usage only. Cache contains no tokens, contact details or response bodies.</summary>
public sealed class AccountUsageService : IDisposable
{
    private static readonly Uri Endpoint = new("https://chatgpt.com/backend-api/wham/usage");
    private readonly VaultStore vault;
    private readonly Func<string, CancellationToken, Task<LiveCredential>> credentials;
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    public AccountUsageService(VaultStore vault, Func<string, CancellationToken, Task<LiveCredential>> credentials,
        HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        this.vault = vault;
        this.credentials = credentials;
        this.clock = clock ?? TimeProvider.System;
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(4)
        }) { Timeout = TimeSpan.FromSeconds(10) };
    }
    private PrivateJobStore<AccountUsage> Cache(string id)
    {
        if (!LiveState.ValidId(id)) throw new UserFacingException("账号标识无效。");
        return new PrivateJobStore<AccountUsage>(Path.Combine(vault.DirectoryPath, "usage"), id + ".dpapi");
    }
    public AccountUsage? Cached(string id)
    {
        try
        {
            var cached = Cache(id).Locked(value => ((AccountUsage?)null, value));
            return cached is null ? null : cached with
            {
                Stale = cached.Status != "ok" || cached.UpdatedAtUtc is null || clock.GetUtcNow() - cached.UpdatedAtUtc > TimeSpan.FromSeconds(90)
            };
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
                await concurrency.WaitAsync(cancellation).ConfigureAwait(false);
                entered = true;
                await RefreshAsync(id, force, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { if (entered) concurrency.Release(); }
        })).ConfigureAwait(false);
    }
    public async Task<AccountUsage> RefreshAsync(string id, bool force, CancellationToken cancellation)
    {
        var cache = Cache(id);
        var previous = Cached(id);
        var now = clock.GetUtcNow();
        if (!force && previous is not null && now - previous.AttemptedAtUtc < TimeSpan.FromSeconds(60)) return previous;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));cancellation=deadline.Token;
        AccountUsage result;
        try
        {
            var credential = await credentials(id, cancellation).ConfigureAwait(false);
            if (credential.Info.IdentityKey != id) throw new UserFacingException("账号身份无法确认。");
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Add("ChatGPT-Account-Id", credential.AccountId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "reauth-required" : "unavailable";
                result = Failure(previous, now, status, status == "reauth-required"
                    ? "官方验证未通过，请通过官方登录更新该账号。" : "官方用量暂不可用，保留上次查询结果。");
            }
            else
            {
                using var document=await OfficialMetadataJson.ReadAsync(response,1024*1024,cancellation).ConfigureAwait(false);
                if(document.RootElement.ValueKind==JsonValueKind.Object && document.RootElement.TryGetProperty("account_id",out var reportedAccount) &&
                    reportedAccount.ValueKind==JsonValueKind.String && reportedAccount.GetString()!=credential.AccountId)throw new IOException();
                result = Parse(document.RootElement, clock.GetUtcNow());
            }
        }
        catch (OperationCanceledException) { result = Failure(previous, now, "unavailable", "用量查询超时，保留上次查询结果。"); }
        catch (UserFacingException) { result = Failure(previous, now, "reauth-required", "账号登录需要更新；尚未获取当前套餐与用量。"); }
        catch { result = Failure(previous, now, "unavailable", "未能获取官方用量，保留上次查询结果。"); }
        return cache.Locked(existing =>
        {
            if (existing is not null && existing.AttemptedAtUtc > result.AttemptedAtUtc) return ((AccountUsage?)null, existing);
            return ((AccountUsage?)result, result);
        });
    }
    private static AccountUsage Failure(AccountUsage? previous, DateTimeOffset now, string status, string message)
        => new(status, previous?.Plan, previous?.Primary, previous?.Secondary, previous?.CodeReview,
            previous?.UpdatedAtUtc, now, message) { Stale = true, ResetAvailableCount = previous?.ResetAvailableCount, UsageAllowed = null };

    internal static AccountUsage Parse(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new IOException();
        string? plan = null;
        if (root.TryGetProperty("plan_type", out var rawPlan) && rawPlan.ValueKind == JsonValueKind.String)
        {
            var value = rawPlan.GetString();
            plan = value switch
            {
                "free" => "Free", "go" => "Go", "plus" => "Plus", "pro" => "Pro", "team" => "Team",
                "business" => "Business", "enterprise" => "Enterprise", "edu" => "Edu", _ => null
            };
            // New official tier codes remain visible instead of reverting to a stale JWT label.
            if (plan is null && value is { Length: > 0 and <= 40 } &&
                value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) plan = value;
        }
        UsageWindow? primary = null, secondary = null, review = null;
        bool? allowed = null;
        if (root.TryGetProperty("rate_limit", out var rates) && rates.ValueKind == JsonValueKind.Object)
        {
            primary = Window(rates, "primary_window", now);
            secondary = Window(rates, "secondary_window", now);
            if (rates.TryGetProperty("allowed",out var rawAllowed) && rawAllowed.ValueKind is JsonValueKind.True or JsonValueKind.False)
                allowed = rawAllowed.GetBoolean();
        }
        if (root.TryGetProperty("code_review_rate_limit", out var codeReview) && codeReview.ValueKind == JsonValueKind.Object)
            review = Window(codeReview, "primary_window", now);
        long? resets = null;
        if (root.TryGetProperty("rate_limit_reset_credits", out var credits) && credits.ValueKind == JsonValueKind.Object &&
            credits.TryGetProperty("available_count", out var count) && count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt64(out var number) && number is >= 0 and <= 1_000_000) resets = number;
        if (root.TryGetProperty("spend_control",out var spend) && spend.ValueKind == JsonValueKind.Object &&
            spend.TryGetProperty("reached",out var reached) && reached.ValueKind == JsonValueKind.True) allowed = false;
        return new("ok", plan, primary, secondary, review, now, now, "官方用量已更新。") { ResetAvailableCount = resets, UsageAllowed = allowed };
    }
    private static UsageWindow? Window(JsonElement parent, string key, DateTimeOffset now)
    {
        if (!parent.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) return null;
        double? used = null;
        long? seconds = null;
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("used_percent", out var rawUsed) && rawUsed.ValueKind == JsonValueKind.Number && rawUsed.TryGetDouble(out var percentage) &&
            double.IsFinite(percentage) && percentage is >= 0 and <= 1000) used = percentage;
        if (window.TryGetProperty("limit_window_seconds", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out var length) &&
            length is > 0 and <= 31_536_000) seconds = length;
        if (window.TryGetProperty("reset_at", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unix) && unix is > 0 and < 253_402_300_800)
            reset = DateTimeOffset.FromUnixTimeSeconds(unix);
        else if (window.TryGetProperty("reset_after_seconds", out var after) && after.ValueKind == JsonValueKind.Number && after.TryGetInt64(out var remaining) && remaining is >= 0 and <= 31_536_000)
            reset = now.AddSeconds(remaining);
        return new UsageWindow(used, seconds, reset);
    }
    public void Dispose() => http.Dispose();
}
