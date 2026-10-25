using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Only an owned launch's app:// renderer is adapted. Ordinary web pages and the native account/usage object are untouched.</summary>
internal sealed class NativeInputBridge(NativeSurfacePipe pipe,Func<CancellationToken,Task<InferenceGateState>> gate):IAsyncDisposable
{
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<string,bool> sessions=new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private Task? events,updates;
    private NativeInputStatus status=new(true,false,false,false,"starting",null);
    private NativeAdapterCatalog? catalog;
    internal NativeInputStatus Status=>Volatile.Read(ref status);
    internal int InterceptedCount {get;private set;}
    internal bool CaptureValidationFixture {get;init;}
    internal object? ValidationFixture {get;private set;}
    internal object? WorkValidationFixture {get;private set;}
    internal void Prepare()=>catalog=NativeAdapterCatalog.Installed();
    internal async Task LoadValidationResourcesAsync(CancellationToken cancellation)
    {
        if(!CaptureValidationFixture||catalog is null)throw new InvalidOperationException("Diagnostic resource loading is unavailable.");
        foreach(var session in sessions.Keys.ToArray())
        {
            // Owned empty-profile diagnostics only. Lazy composer modules otherwise remain unloaded
            // until login, which is deliberately excluded from automated validation.
            var expression="location.protocol==='app:'&&location.host==='-'?Promise.all("+JsonSerializer.Serialize(catalog.Assets.ToArray())+".map(asset=>import(asset))).then(()=>true):false";
            await pipe.SendAsync("Runtime.evaluate",new{expression,awaitPromise=true,returnByValue=true},session,cancellation).ConfigureAwait(false);
        }
    }
    internal async Task<bool> CaptureValidationRenderingAsync(string destination,CancellationToken cancellation)
    {
        if(!CaptureValidationFixture||File.Exists(destination)||Path.GetExtension(destination)!=".png")throw new InvalidOperationException();
        foreach(var session in sessions.Where(row=>row.Value).Select(row=>row.Key).ToArray())
        {
            var result=await pipe.SendAsync("Runtime.evaluate",new{expression="location.protocol==='app:'&&location.host==='-'&&document.body?{ready:document.body.innerText.length>0,snag:document.body.innerText.includes('ChatGPT hit a snag')}:null",returnByValue=true},session,cancellation).ConfigureAwait(false);
            if(!result.TryGetProperty("result",out var r)||!r.TryGetProperty("value",out var value)||value.ValueKind!=JsonValueKind.Object)continue;
            if(value.GetProperty("snag").GetBoolean())throw new IOException("Owned empty client rendered the crash boundary.");
            if(!value.GetProperty("ready").GetBoolean())continue;
            var captured=await pipe.SendAsync("Page.captureScreenshot",new{format="png",captureBeyondViewport=false},session,cancellation).ConfigureAwait(false);
            var bytes=Convert.FromBase64String(captured.GetProperty("data").GetString()!);
            if(bytes.Length is <32 or >16*1024*1024)throw new IOException();
            using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);await output.WriteAsync(bytes,cancellation).ConfigureAwait(false);return true;
        }
        return false;
    }
    internal async Task StartAsync(CancellationToken cancellation)
    {
        // Both assets must be planned from the current signed install before any interception.
        // Unknown or ambiguous structures fail closed; no version/hash check is bypassed.
        try{catalog??=NativeAdapterCatalog.Installed();}
        catch{Volatile.Write(ref status,new(true,false,false,false,"incompatible",DateTimeOffset.UtcNow));throw;}
        events=EventsAsync();
        await pipe.SendAsync("Browser.getVersion",cancellation:cancellation).ConfigureAwait(false);
        await pipe.SendAsync("Target.setAutoAttach",new {autoAttach=true,waitForDebuggerOnStart=true,flatten=true,
            filter=new object[]{new {type="page"},new {type="iframe"},new {exclude=true}}},cancellation:cancellation).ConfigureAwait(false);
        Volatile.Write(ref status,new(true,true,false,false,"awaiting-frontend",DateTimeOffset.UtcNow));
        updates=UpdatesAsync();
    }
    private async Task EventsAsync()
    {
        try
        {
            await foreach(var packet in pipe.Events.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if(!packet.TryGetProperty("method",out var method) || !packet.TryGetProperty("params",out var parameters))continue;
                var session=packet.TryGetProperty("sessionId",out var sessionValue)?sessionValue.GetString():null;
                switch(method.GetString())
                {
                    case "Target.attachedToTarget":
                        var child=parameters.GetProperty("sessionId").GetString()!;
                        if(sessions.Count>64){await ResumeAsync(child);break;}
                        sessions[child]=false;
                        try
                        {
                            await pipe.SendAsync("Fetch.enable",new {patterns=catalog!.Assets.Select(asset=>
                                new {urlPattern=asset,resourceType="Script",requestStage="Response"}).ToArray()},child,stop.Token).ConfigureAwait(false);
                        }
                        finally{await ResumeAsync(child);}
                        break;
                    case "Target.detachedFromTarget":
                        var removed=parameters.GetProperty("sessionId").GetString()!;sessions.TryRemove(removed,out _);break;
                    case "Fetch.requestPaused" when session is not null:
                        await AdaptAsync(session,parameters).ConfigureAwait(false);break;
                }
            }
        }
        catch(Exception error) when(error is IOException or JsonException or OperationCanceledException or InvalidOperationException)
        {Volatile.Write(ref status,new(true,false,false,false,"disconnected",DateTimeOffset.UtcNow));}
    }
    private async Task ResumeAsync(string session)
    {try{await pipe.SendAsync("Runtime.runIfWaitingForDebugger",session:session,cancellation:stop.Token).ConfigureAwait(false);}catch{}}
    private async Task AdaptAsync(string session,JsonElement parameters)
    {
        var id=parameters.GetProperty("requestId").GetString()!;var fulfilled=false;
        try
        {
            var asset=parameters.GetProperty("request").GetProperty("url").GetString();
            if(catalog is null||!catalog.TryPlan(asset,out var plan)||plan is null||
                !parameters.TryGetProperty("responseStatusCode",out var code) || code.GetInt32()!=200)return;
            var response=await pipe.SendAsync("Fetch.getResponseBody",new {requestId=id},session,stop.Token).ConfigureAwait(false);
            var body=response.GetProperty("body").GetString()!;
            var source=response.TryGetProperty("base64Encoded",out var encoded)&&encoded.GetBoolean()?Encoding.UTF8.GetString(Convert.FromBase64String(body)):body;
            var adapted=plan.Adapt(source);
            if(CaptureValidationFixture&&!plan.Work)ValidationFixture=new {runtimePrelude=NativeSendAdapter.RuntimePrelude,
                originalDecision=plan.Function,adaptedDecision=plan.Function.Replace("=>{","=>{if(__caNativeGateAllows("+plan.Getter+","+plan.Host+"))return "+plan.Inactive+";",StringComparison.Ordinal),
                sourceHash=plan.SourceHash,syntheticOnly=true};
            if(CaptureValidationFixture&&plan.Work)WorkValidationFixture=new {runtimePrelude=NativeSendAdapter.RuntimePrelude,
                originalDecision=plan.Function,originalSourceUntouchedByLocalOverride=plan.DefinitionAsset is not null
                    ?!catalog.Assets.Contains(plan.DefinitionAsset,StringComparer.Ordinal):adapted.Contains(plan.Anchor+plan.Function,StringComparison.Ordinal),
                adaptedLocalDecision=plan.WorkCallback,localConsumerAdapted=!adapted.Contains(plan.Consumer,StringComparison.Ordinal),
                sourceHash=plan.SourceHash,syntheticOnly=true};
            var headers=parameters.GetProperty("responseHeaders").EnumerateArray().Where(header=>
                header.GetProperty("name").GetString()?.ToLowerInvariant() is not ("content-length" or "content-encoding" or "etag" or "content-md5"))
                .Select(header=>new {name=header.GetProperty("name").GetString(),value=header.GetProperty("value").GetString()}).ToArray();
            await pipe.SendAsync("Fetch.fulfillRequest",new {requestId=id,responseCode=200,responseHeaders=headers,body=Convert.ToBase64String(Encoding.UTF8.GetBytes(adapted))},session,stop.Token).ConfigureAwait(false);
            fulfilled=true;sessions[session]=true;InterceptedCount++;
        }
        catch{Volatile.Write(ref status,new(true,true,false,false,"incompatible",DateTimeOffset.UtcNow));}
        finally{if(!fulfilled)try{await pipe.SendAsync("Fetch.continueResponse",new {requestId=id},session,stop.Token).ConfigureAwait(false);}catch{}}
    }
    private async Task UpdatesAsync()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                if(pipe.ReadTermination is not null){Volatile.Write(ref status,new(true,false,false,false,"disconnected",DateTimeOffset.UtcNow));break;}
                InferenceGateState state;
                try{using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(12));state=await gate(deadline.Token).ConfigureAwait(false);}
                catch{state=InferenceGateService.Denied();}
                var adapted=false;var initialObserved=false;var workObserved=false;
                bool? nativeBlocked=null,effectiveBlocked=null;DateTimeOffset? workCheckedAt=null;
                foreach(var session in sessions.Where(entry=>entry.Value).Select(entry=>entry.Key).ToArray())
                {
                    try
                    {
                        var expression="(()=>{if(location.protocol!=='app:'||location.host!=='-')return null;const initial=globalThis.__codexAccountsNativeAdapterV1===true,work=globalThis.__codexAccountsNativeWorkAdapterV1===true;if(!initial&&!work)return null;globalThis."+
                            NativeSendAdapter.SnapshotName+"=Object.freeze("+JsonSerializer.Serialize(state,json)+");globalThis.dispatchEvent(new Event('"+NativeSendAdapter.EventName+"'));const check=globalThis.__codexAccountsLocalWorkQuotaV1;return {initial,work,check:check&&typeof check.nativeBlocked==='boolean'&&typeof check.effectiveBlocked==='boolean'&&Number.isFinite(check.checkedAtMs)?check:null};})()";
                        var result=await pipe.SendAsync("Runtime.evaluate",new {expression,returnByValue=true},session,stop.Token).ConfigureAwait(false);
                        if(result.TryGetProperty("result",out var value)&&value.TryGetProperty("value",out var accepted)&&accepted.ValueKind==JsonValueKind.Object)
                        {
                            var initial=accepted.TryGetProperty("initial",out var i)&&i.ValueKind==JsonValueKind.True;
                            var work=accepted.TryGetProperty("work",out var w)&&w.ValueKind==JsonValueKind.True;
                            initialObserved|=initial;workObserved|=work;adapted|=initial&&work;
                            if(initial&&work&&accepted.TryGetProperty("check",out var check)&&check.ValueKind==JsonValueKind.Object &&
                                check.TryGetProperty("checkedAtMs",out var checkedAt)&&checkedAt.TryGetInt64(out var ms) &&
                                ms>DateTimeOffset.UtcNow.AddSeconds(-15).ToUnixTimeMilliseconds()&&ms<DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()&&
                                check.TryGetProperty("nativeBlocked",out var n)&&n.ValueKind is JsonValueKind.True or JsonValueKind.False&&
                                check.TryGetProperty("effectiveBlocked",out var b)&&b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            {nativeBlocked=n.GetBoolean();effectiveBlocked=b.GetBoolean();workCheckedAt=DateTimeOffset.FromUnixTimeMilliseconds(ms);}
                        }
                    }
                    catch{}
                }
                var current=Status;
                Volatile.Write(ref status,new(true,true,adapted,state.Allowed,adapted?"adapted":current.State=="incompatible"?"incompatible":initialObserved||workObserved?"partial":"awaiting-frontend",DateTimeOffset.UtcNow)
                    {InitialQuotaAdapted=initialObserved,WorkQuotaAdapted=workObserved,WorkQuotaNativeBlocked=nativeBlocked,
                        WorkQuotaEffectiveBlocked=effectiveBlocked,WorkQuotaCheckedAtUtc=workCheckedAt});
                await Task.Delay(2000,stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException){}
        catch{Volatile.Write(ref status,new(true,false,false,false,"disconnected",DateTimeOffset.UtcNow));}
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach(var session in sessions.Keys)
        {
            try{using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));await pipe.SendAsync("Fetch.disable",session:session,cancellation:deadline.Token).ConfigureAwait(false);}catch{}
            try{using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));await pipe.SendAsync("Runtime.runIfWaitingForDebugger",session:session,cancellation:deadline.Token).ConfigureAwait(false);}catch{}
        }
        foreach(var task in new[]{events,updates})if(task is not null)try{await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);}catch{}
        stop.Dispose();
    }
}
