using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Fresh, fixed official metadata only; candidates never receive a model request during preflight.</summary>
public sealed class RotationCandidates : IDisposable
{
    private readonly VaultStore vault;
    private readonly Func<string,CancellationToken,Task<LiveCredential>> credentials;
    private readonly AccountUsageService usage;
    private readonly HttpClient http;
    public RotationCandidates(VaultStore vault,Func<string,CancellationToken,Task<LiveCredential>> credentials,
        AccountUsageService usage,HttpMessageHandler? handler=null)
    {
        this.vault=vault;this.credentials=credentials;this.usage=usage;
        http=new HttpClient(handler ?? new SocketsHttpHandler {AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(3)})
            {Timeout=TimeSpan.FromSeconds(6)};
    }
    public async Task<string?> NextAsync(string from,string? model,IReadOnlySet<string> visited,CancellationToken cancellation)
    {
        var accounts=vault.List().Select(entry=>entry.Id).ToArray();
        var start=Array.IndexOf(accounts,from);
        HashSet<string>? sourceModels=null;
        if(model is null)
        {
            try {sourceModels=await ModelsAsync(from,cancellation).ConfigureAwait(false);}catch{return null;}
            if(sourceModels.Count==0)return null;
        }
        for(var offset=1;offset<=accounts.Length;offset++)
        {
            cancellation.ThrowIfCancellationRequested();
            var id=accounts[(Math.Max(-1,start)+offset)%accounts.Length];
            if(visited.Contains(id))continue;
            try
            {
                var snapshot=await usage.RefreshAsync(id,true,cancellation).ConfigureAwait(false);
                if(snapshot.Status!="ok" || snapshot.Stale || snapshot.UsageAllowed!=true)continue;
                var models=await ModelsAsync(id,cancellation).ConfigureAwait(false);
                if(model is not null ? models.Contains(model) : sourceModels!.IsSubsetOf(models))return id;
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
            catch { /* An uncertain identity, quota or model catalogue is not an eligible target. */ }
        }
        return null;
    }
    private async Task<HashSet<string>> ModelsAsync(string id,CancellationToken cancellation)
    {
                var credential=await credentials(id,cancellation).ConfigureAwait(false);
                using var request=new HttpRequestMessage(HttpMethod.Get,"https://chatgpt.com/backend-api/codex/models?client_version=0.163.0-alpha.10.2");
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential.AccessToken);
                request.Headers.Add("ChatGPT-Account-Id",credential.AccountId);
                request.Headers.Add("originator","codex_desktop");
                using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false);
                if(!response.IsSuccessStatusCode)throw new IOException();
                    using var document=await OfficialMetadataJson.ReadAsync(response,4*1024*1024,cancellation).ConfigureAwait(false);
                    if(!document.RootElement.TryGetProperty("models",out var models) || models.ValueKind!=JsonValueKind.Array)throw new IOException();
                    return models.EnumerateArray().Where(entry=>entry.ValueKind==JsonValueKind.Object && entry.TryGetProperty("slug",out var slug) && slug.ValueKind==JsonValueKind.String)
                        .Select(entry=>entry.GetProperty("slug").GetString()!).Where(value=>value.Length is >0 and <=128 && value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.'))
                        .Take(256).ToHashSet(StringComparer.Ordinal);
    }
    public void Dispose()=>http.Dispose();
}
