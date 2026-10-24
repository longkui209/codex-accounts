using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.ModelRouting;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.Validation;

/// <summary>Read-only preflight and process binding. Does not launch, refresh, import or switch an account.</summary>
public static class RoutingDiagnosis
{
    // The selected subscription's catalog is a non-inference endpoint. Never sends a model prompt,
    // changes the selection, or persists authentication in this report. Only public model slugs
    // are retained from the bounded catalog response; account data and raw bodies are discarded.
    public static async Task RunSubscriptionProbeAsync(string outputFile, string? profile = null)
    {
        var destination = Path.GetFullPath(outputFile);
        var allowed = ValidationPaths.OutputDirectory;
        if (!string.Equals(Path.GetDirectoryName(destination), allowed, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Probe output must be a project validation file.");
        var vault = McpServer.CreateVault();
        var state = new LiveState(vault, "routing");
        var selected = profile ?? state.SelectedProfile() ?? throw new UserFacingException("尚无已选择的订阅账号。");
        if (!LiveState.ValidId(selected)) throw new UserFacingException("账号标识无效。");
        var phase = "credentials";
        int? status = null;
        string? contentType = null;
        var modelSlugs = Array.Empty<string>();
        var clientVersion = "0.163.0-alpha.10.2";
        int? usageStatus = null;
        string? currentPlan = null;
        var usagePhase = "not-requested";
        int? accountCheckStatus = null;
        string? accountCheckPlan = null;
        string? accountStructure = null;
        var matchingWorkspaceCount = 0;
        var returnedWorkspaceCount = 0;
        var otherWorkspacePlans = new List<string>();
        bool? usageAccountMatches = null;
        var identityComparisons = new List<object>();
        var requestedEmail = Environment.GetEnvironmentVariable("CODEX_ACCOUNTS_PROBE_EMAIL")?.Trim();
        bool? selectedEmailMatchesRequest = null;
        var matchingEmailProfiles = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            using var saved = new LiveCredentials(vault);
            var credential = await saved.GetAsync(selected, false, deadline.Token).ConfigureAwait(false);
            try
            {
                using var selectedAuth = vault.Read(selected);
                var selectedIdentity = Identity(selectedAuth);
                if (requestedEmail is not null && selectedIdentity.Email is not null)
                {
                    selectedEmailMatchesRequest = string.Equals(requestedEmail,selectedIdentity.Email,StringComparison.OrdinalIgnoreCase);
                    if(selectedEmailMatchesRequest == true)matchingEmailProfiles.Add(selected);
                }
                foreach (var entry in vault.List().Where(entry=>entry.Id != selected).Take(63))
                {
                    using var otherAuth = vault.Read(entry.Id);
                    var other = Identity(otherAuth);
                    if(requestedEmail is not null && other.Email is not null && string.Equals(requestedEmail,other.Email,StringComparison.OrdinalIgnoreCase))
                        matchingEmailProfiles.Add(entry.Id);
                    identityComparisons.Add(new { profile_id=entry.Id, same_email=selectedIdentity.Email is null || other.Email is null ? (bool?)null :
                        string.Equals(selectedIdentity.Email,other.Email,StringComparison.OrdinalIgnoreCase),
                        same_user=selectedIdentity.Subject == other.Subject, same_workspace=selectedIdentity.Workspace == other.Workspace });
                }
            }
            catch { }
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
            using var http = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://chatgpt.com/backend-api/codex/models?client_version=" + clientVersion);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Add("ChatGPT-Account-Id", credential.AccountId);
            request.Headers.Add("originator", "codex_desktop");
            phase = "official-catalog";
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            contentType = response.Content.Headers.ContentType?.MediaType;
            phase = response.IsSuccessStatusCode ? "catalog-accepted" : "catalog-rejected";
            if (response.IsSuccessStatusCode && contentType == "application/json")
            {
                if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new IOException();
                await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024).ConfigureAwait(false);
                using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                if (catalog.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                    modelSlugs = models.EnumerateArray().Where(model => model.ValueKind == JsonValueKind.Object &&
                            model.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String)
                        .Select(model => model.GetProperty("slug").GetString()!)
                        .Where(slug => slug.Length is > 0 and <= 120 && slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(128).ToArray();
            }
            // This is the same official account's usage endpoint, not a model invocation.
            // Read only the enumerated current plan; never retain quota details or contact data.
            try
            {
                using var usage = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
                usage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
                usage.Headers.Add("ChatGPT-Account-Id", credential.AccountId);
                usagePhase = "official-usage";
                using var usageResponse = await http.SendAsync(usage, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                usageStatus = (int)usageResponse.StatusCode;
                usagePhase = usageResponse.IsSuccessStatusCode ? "usage-accepted" : "usage-rejected";
                if (usageResponse.IsSuccessStatusCode && usageResponse.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    await usageResponse.Content.LoadIntoBufferAsync(1024 * 1024).ConfigureAwait(false);
                    using var usageDocument = JsonDocument.Parse(await usageResponse.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                    if (usageDocument.RootElement.TryGetProperty("account_id",out var usageAccount) && usageAccount.ValueKind == JsonValueKind.String)
                        usageAccountMatches = usageAccount.GetString() == credential.AccountId;
                    if (usageDocument.RootElement.TryGetProperty("plan_type", out var plan) && plan.ValueKind == JsonValueKind.String)
                    {
                        var value = plan.GetString();
                        currentPlan = value is "free" or "go" or "plus" or "pro" or "team" or "business" or "enterprise" or "edu" or "unknown"
                            ? value : "unrecognized";
                    }
                }
            }
            catch { usagePhase = "usage-unavailable"; }
            try
            {
                using var accountCheck = new HttpRequestMessage(HttpMethod.Get,"https://chatgpt.com/backend-api/wham/accounts/check");
                accountCheck.Headers.Authorization = new AuthenticationHeaderValue("Bearer",credential.AccessToken);
                accountCheck.Headers.Add("ChatGPT-Account-Id",credential.AccountId);
                accountCheck.Headers.Add("originator","codex_desktop");
                using var accountResponse = await http.SendAsync(accountCheck,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
                accountCheckStatus = (int)accountResponse.StatusCode;
                if (accountResponse.IsSuccessStatusCode && accountResponse.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    await accountResponse.Content.LoadIntoBufferAsync(1024*1024).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                    if(document.RootElement.TryGetProperty("accounts",out var accounts))
                    {
                        void Inspect(JsonElement entry,string? mapKey)
                        {
                            var account = entry.TryGetProperty("account",out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : entry;
                            var accountId = account.TryGetProperty("id",out var rawId) && rawId.ValueKind == JsonValueKind.String ? rawId.GetString() :
                                account.TryGetProperty("account_id",out rawId) && rawId.ValueKind == JsonValueKind.String ? rawId.GetString() : mapKey;
                            var plan = account.TryGetProperty("plan_type",out var rawPlan) && rawPlan.ValueKind == JsonValueKind.String ? rawPlan.GetString() : null;
                            if (plan is null || plan.Length is <1 or >40 || !plan.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-')) plan = null;
                            returnedWorkspaceCount++;
                            if(accountId == credential.AccountId)
                            {
                                matchingWorkspaceCount++;accountCheckPlan = plan;
                                accountStructure = account.TryGetProperty("structure",out var structure) && structure.ValueKind == JsonValueKind.String &&
                                    structure.GetString() is "personal" or "workspace" or "team" ? structure.GetString() : "other-or-missing";
                            }
                            else if(plan is not null) otherWorkspacePlans.Add(plan);
                        }
                        if(accounts.ValueKind == JsonValueKind.Array)
                            foreach(var entry in accounts.EnumerateArray().Take(64)) {if(entry.ValueKind == JsonValueKind.Object)Inspect(entry,null);}
                        else if(accounts.ValueKind == JsonValueKind.Object)
                            foreach(var entry in accounts.EnumerateObject().Take(64)) {if(entry.Value.ValueKind == JsonValueKind.Object)Inspect(entry.Value,entry.Name);}
                    }
                }
            }
            catch { }
        }
        catch (OperationCanceledException) { phase = "timeout"; }
        catch { phase = phase == "credentials" ? "credentials-unavailable" : "network-unavailable"; }
        File.WriteAllText(destination, JsonSerializer.Serialize(new
        {
            account_switch_executed = false, model_request_executed = false,
            selected_account_id = selected, phase, upstream_http_status = status,
            client_version = clientVersion, catalog_model_slugs = modelSlugs,
            usage_phase = usagePhase, usage_http_status = usageStatus, current_server_plan = currentPlan,
            usage_account_matches = usageAccountMatches,
            account_check_http_status = accountCheckStatus, account_check_plan = accountCheckPlan, account_structure = accountStructure,
            matching_workspace_count = matchingWorkspaceCount, returned_workspace_count = returnedWorkspaceCount,
            other_workspace_plans = otherWorkspacePlans.Distinct().ToArray(), checked_at_utc = DateTimeOffset.UtcNow,
            saved_identity_comparisons = identityComparisons,
            selected_email_matches_requested = selectedEmailMatchesRequest, requested_email_matching_profiles = matchingEmailProfiles,
            response_is_json = string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Only equality booleans leave this method. Raw identity claims are never included in a report.
    private static (string? Email,string Subject,string Workspace) Identity(AuthSnapshot snapshot)
    {
        var bytes = snapshot.CopyBytes();
        try
        {
            using var root = JsonDocument.Parse(bytes);
            var tokens = root.RootElement.GetProperty("tokens");
            var pieces = tokens.GetProperty("id_token").GetString()!.Split('.');
            var decoded = Convert.FromBase64String(pieces[1].Replace('-','+').Replace('_','/').PadRight((pieces[1].Length+3)/4*4,'='));
            try
            {
                using var claims = JsonDocument.Parse(decoded);
                return (claims.RootElement.TryGetProperty("email",out var email) && email.ValueKind == JsonValueKind.String ? email.GetString() : null,
                    claims.RootElement.GetProperty("sub").GetString()!,tokens.GetProperty("account_id").GetString()!);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded); }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    public static async Task RunAsync(string outputFile)
    {
        var destination = Path.GetFullPath(outputFile);
        var allowed = ValidationPaths.OutputDirectory + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(destination)?.Equals(allowed.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) != true)
            throw new ArgumentException("Diagnosis output must be a project validation file.");
        var checks = new Dictionary<string, bool>();
        var home = CodexRuntime.ResolveCodexHome();
        var vault = McpServer.CreateVault();
        string? desktopPath = null, cliPath = null;
        void Check(string label, Action action)
        {
            try { action(); checks[label] = true; }
            catch { checks[label] = false; }
        }
        Check("file_auth_supported", () => CodexRuntime.EnsureFileAuthStore(home));
        Check("existing_endpoint_conflict_absent", () => RoutingSettingsGuard.AssertNoExistingEndpoint(home));
        Check("signed_store_desktop_found", () => desktopPath = DesktopLaunch.FindInstalledDesktop());
        Check("signed_official_cli_found", () =>
        {
            cliPath = CodexRuntime.FindCli() ?? throw new IOException();
            DesktopLaunch.ValidateOfficialCli(cliPath);
        });
        Check("current_file_credentials_structurally_compatible", () =>
        {
            using var auth = AuthSnapshot.Read(Path.Combine(home, "auth.json"));
            using var unusedSaved = new LiveCredentials(vault);
            var credentials = new RouterCredentials(vault, home, unusedSaved, auth.Info.IdentityKey);
            _ = credentials.GetAsync(auth.Info.IdentityKey, CancellationToken.None).GetAwaiter().GetResult(); // Desktop branch is read-only.
        });
        var boundCallers = 0;
        if (desktopPath is not null && cliPath is not null)
        {
            // Diagnose already-running native processes without changing their ownership or environment.
            var lifetime = new NativeDesktopLifetime(desktopPath, cliPath, DateTimeOffset.UtcNow.AddDays(-7));
            foreach (var process in Process.GetProcessesByName("codex"))
            {
                using (process) { try { if (lifetime.IsNativeBackendCaller(process.Id)) boundCallers++; } catch { } }
            }
        }
        var routing = await new LiveClient(vault, "routing").StatusAsync().ConfigureAwait(false);
        var report = new
        {
            read_only = true, account_switch_executed = false, desktop_restarted = false, oauth_or_model_request_executed = false,
            checks, compatible_native_callers_found = boundCallers,
            routing_enabled = routing.Enabled, routing_connected = routing.Connected,
            native_binding_observed = routing.NativeConnectionObserved, verification_state = routing.VerificationState
        };
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
