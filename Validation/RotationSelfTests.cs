using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

public static class RotationSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync(string outputRoot)
    {
        var reports=new List<string>();
        var vault=new VaultStore(Path.Combine(outputRoot,"rotation-synthetic-"+Guid.NewGuid().ToString("N")));
        var settings=new RotationSettingsStore(vault);
        Require(!settings.Read().Enabled,"自动轮换必须默认关闭");
        settings.Set(true);Require(new RotationSettingsStore(vault).Read().Enabled,"设置应加密持久化");
        settings.Set(false);Require(!settings.Read().Enabled,"可随时关闭后续轮换");
        reports.Add("PASS: 自动轮换默认关闭，开启和关闭通过当前用户加密持久化，读取失败不会误开启。");
        var errorBytes=Encoding.UTF8.GetBytes("{\"error\":{\"type\":\"usage_limit_reached\",\"resets_at\":2000000000}}");
        using(var response=Response(HttpStatusCode.TooManyRequests,errorBytes,"application/json"))
        {
            var result=await QuotaRejectionProbe.InspectAsync(response,CancellationToken.None).ConfigureAwait(false);
            Require(result.Exhausted && result.ResetsAtUtc==DateTimeOffset.FromUnixTimeSeconds(2000000000),"明确额度拒绝及重置时间准确识别");
            Require((await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(errorBytes),"无候选时拒绝正文必须原样可返回");
        }
        foreach(var pair in new[]{(HttpStatusCode.TooManyRequests,"{\"error\":{\"type\":\"rate_limit_exceeded\"}}"),
                     (HttpStatusCode.Unauthorized,Encoding.UTF8.GetString(errorBytes)),(HttpStatusCode.TooManyRequests,"not-json")})
        {
            var bytes=Encoding.UTF8.GetBytes(pair.Item2);using var response=Response(pair.Item1,bytes,"application/json");
            Require(!(await QuotaRejectionProbe.InspectAsync(response,CancellationToken.None).ConfigureAwait(false)).Exhausted,"通用限速、认证或未知错误不能触发轮换");
            Require((await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(bytes),"非轮换响应不得丢失原字节");
        }
        var oversized=Encoding.UTF8.GetBytes(new string('x',70000));
        using(var response=Response(HttpStatusCode.TooManyRequests,oversized,"text/plain"))
        {
            Require(!(await QuotaRejectionProbe.InspectAsync(response,CancellationToken.None).ConfigureAwait(false)).Exhausted,"大响应不可误识别");
            Require((await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(oversized),"探测上限之后仍须保持所有字节");
        }
        reports.Add("PASS: 仅明确额度耗尽触发；通用 429、401、坏 JSON 与大响应均原样返回，不误轮换。");
        var early=Encoding.UTF8.GetBytes("event: error\ndata:{\"type\":\"error\",\"error\":{\"code\":\"usage_limit_reached\"}}\n\n");
        using(var response=Response(HttpStatusCode.OK,early,"text/event-stream"))
            Require((await QuotaRejectionProbe.InspectAsync(response,CancellationToken.None).ConfigureAwait(false)).Exhausted,"开始输出前的 SSE 配额错误应在后台接续");
        using(var response=Response(HttpStatusCode.OK,early,"text/plain"))
        {
            Require((await QuotaRejectionProbe.InspectAsync(response,CancellationToken.None).ConfigureAwait(false)).Exhausted,"原生非标准媒体类型仍须识别明确SSE额度拒绝");
            Require((await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(early),"非标准类型探测保留原字节");
        }
        var partial=Encoding.UTF8.GetBytes("data:{\"type\":\"response.output_text.delta\",\"delta\":\"already delivered\"}\n\n"+Encoding.UTF8.GetString(early));
        using(var response=Response(HttpStatusCode.OK,partial,"text/event-stream"))
        {
            Require(!(await QuotaRejectionProbe.InspectAsync(response,CancellationToken.None).ConfigureAwait(false)).Exhausted,"已开始输出不能跨账号重放");
            Require((await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(partial),"部分输出与错误仍按原次序保留");
        }
        reports.Add("PASS: 无输出的 SSE 额度错误可接续；已产生回答或工具内容的流绝不跨账号重放。");
        var body=Encoding.UTF8.GetBytes("{\"model\":\"synthetic-model\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"synthetic-call\",\"output\":\"project state\"}],\"reasoning\":{\"effort\":\"max\"},\"synthetic_extension\":true}");
        using(var input=new MemoryStream(body))using(var payload=await TransientRequestBuffer.ReadAsync(input,false,CancellationToken.None).ConfigureAwait(false))
        {
            Require(payload.SelfContained && payload.Model=="synthetic-model","完整模型上下文可重试");
            using var first=payload.Content();using var second=payload.Content();
            Require((await first.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(body) &&
                (await second.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(body),"重试必须保留模型、上下文、工具结果及未知字段的原始字节");
        }
        using(var input=new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"synthetic-model\",\"previous_response_id\":\"account-bound-response\"}")))
        using(var payload=await TransientRequestBuffer.ReadAsync(input,false,CancellationToken.None).ConfigureAwait(false))
            Require(!payload.SelfContained,"依赖前账号服务端续接标识的请求不能盲目换号");
        reports.Add("PASS: 请求仅在内存暂存，原模型、推理、工具结果和上下文逐字节保留；仅服务端续接引用不能盲目换号。");
        var credentials=new Dictionary<string,LiveCredential>();var names=new Dictionary<string,string>();
        foreach(var name in new[]{"A","B","C"})
        {
            var claims=new{ sub="synthetic-rotation-user-"+name,email="synthetic-"+name+"@example.test",exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()};
            var jwt=Encode(new {alg="RS256"})+"."+Encode(claims)+".syntheticSignature";
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new {auth_mode="chatgpt",tokens=new {account_id="synthetic-rotation-"+name,access_token=jwt,id_token=jwt,refresh_token="synthetic-refresh"}});
            using var snapshot=AuthSnapshot.FromBytes(bytes);vault.Save(snapshot,"Synthetic "+name);names[name]=snapshot.Info.IdentityKey;
            credentials[snapshot.Info.IdentityKey]=new("synthetic-rotation-"+name,"synthetic-token-"+name,"plus",snapshot.Info);
        }
        var handler=new MetadataHandler();
        using var usage=new AccountUsageService(vault,(id,_)=>Task.FromResult(credentials[id]),handler);
        using var candidates=new RotationCandidates(vault,(id,_)=>Task.FromResult(credentials[id]),usage,handler);
        var visited=new HashSet<string>{names["A"]};
        Require(await candidates.NextAsync(names["A"],"synthetic-model",visited,CancellationToken.None).ConfigureAwait(false)==names["B"],"应跳过无额度的候选，选择可用同模型账号");
        handler.AllowB=false;
        Require(await candidates.NextAsync(names["A"],"synthetic-model",visited,CancellationToken.None).ConfigureAwait(false) is null,"过期缓存不能使无额度账号可选");
        handler.AllowB=true;handler.BModels=[];
        Require(await candidates.NextAsync(names["A"],"synthetic-model",visited,CancellationToken.None).ConfigureAwait(false) is null,"无同模型权限不能降级模型");
        handler.BModels=["synthetic-model"];
        Require(await candidates.NextAsync(names["A"],null,visited,CancellationToken.None).ConfigureAwait(false) is null,"压缩请求必须覆盖源账号目录以保证原模型");
        handler.BModels=["synthetic-model","synthetic-extra"];
        Require(await candidates.NextAsync(names["A"],null,visited,CancellationToken.None).ConfigureAwait(false)==names["B"] && handler.ValidDestination,
            "压缩字节不解压改写，使用同目录覆盖来核对模型支持");
        reports.Add("PASS: 候选实时核对官方允许状态及模型目录；无额度、无模型或资料未知均跳过，压缩请求原字节保留。");
        return reports;
    }
    private static string Encode(object value)=>Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static HttpResponseMessage Response(HttpStatusCode status,byte[] bytes,string mediaType)
    {var result=new HttpResponseMessage(status){Content=new ByteArrayContent(bytes)};result.Content.Headers.ContentType=new(mediaType);return result;}
    private static void Require(bool condition,string name){if(!condition)throw new UserFacingException("隔离轮换验证失败："+name+"。");}
    private sealed class MetadataHandler : HttpMessageHandler
    {
        public bool AllowB {get;set;}=true;
        public string[] BModels {get;set;}=["synthetic-model"];
        public bool ValidDestination {get;private set;}=true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var account=request.Headers.GetValues("ChatGPT-Account-Id").Single();
            var name=account[^1..];ValidDestination &= request.Method==HttpMethod.Get && request.RequestUri?.Host=="chatgpt.com";
            string payload;
            if(request.RequestUri?.AbsolutePath=="/backend-api/wham/usage")payload=JsonSerializer.Serialize(new {plan_type="plus",rate_limit=new {allowed=name=="A" || name=="B" && AllowB}});
            else if(request.RequestUri?.AbsolutePath=="/backend-api/codex/models")payload=JsonSerializer.Serialize(new {models=(name=="A"?new[]{"synthetic-model","synthetic-extra"}:BModels).Select(slug=>new{slug})});
            else{ValidDestination=false;payload="{}";}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(payload,Encoding.UTF8,"application/json")});
        }
    }
}
