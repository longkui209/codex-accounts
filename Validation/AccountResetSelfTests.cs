using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.Validation;

public static class AccountResetSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync(string outputRoot)
    {
        var reports = new List<string>();
        var vault = new VaultStore(Path.Combine(outputRoot, "resets-synthetic-" + Guid.NewGuid().ToString("N")));
        var clock = new Clock(); var id = new string('a',64); var otherId = new string('b',64);
        var handler = new Handler();
        var credential = new LiveCredential("synthetic-reset-account", "synthetic-reset-token", "plus",
            new AccountInfo(id,"s***@e***.test","Plus",clock.GetUtcNow().AddHours(2)));
        using var service = new AccountResetService(vault, (_,_) => Task.FromResult(credential), _ => null, handler, clock);
        var details = await service.RefreshAsync(id,true,CancellationToken.None);
        Require(details.Status == "ok" && details.AvailableCount == 5 && details.Cards?.Count == 4, "官方总数不能由返回卡片数替代");
        Require(details.Cards![0].Id == "synthetic-card-1" && details.Cards[0].ExpiresAtUtc == clock.GetUtcNow().AddDays(1) &&
            details.Cards[1].ExpiryKnown && details.Cards[1].ExpiresAtUtc is null && !details.Cards[2].ExpiryKnown, "按实际期限排序且缺失与无到期时间不同");
        Require(!JsonSerializer.Serialize(details).Contains("private-reset") && handler.Posts == 0, "详情查询不返回联系人或使用重置卡");
        reports.Add("PASS: 重置总次数、部分详情、逐张有效期、无到期时间与未知期限正确区分；只读查询不消耗。");
        var preview = await service.PrepareAsync(id,"synthetic-card-1",CancellationToken.None);
        Require(preview.AccountId == id && preview.Card.Id == "synthetic-card-1" && !preview.Recovering && handler.Posts == 0, "准备确认只绑定账号和卡片");
        await Reject(() => service.ConsumeAsync(otherId,preview.ConfirmationId,true,CancellationToken.None));
        await Reject(() => service.ConsumeAsync(id,preview.ConfirmationId,false,CancellationToken.None));
        await Reject(() => service.ConsumeAsync(id,Guid.NewGuid().ToString("N"),true,CancellationToken.None));
        Require(handler.Posts == 0, "跨账号、无确认与错误确认不可触发兑换");
        var used = await service.ConsumeAsync(id,preview.ConfirmationId,true,CancellationToken.None);
        Require(used.Code == "reset" && used.Confirmed && used.WindowsReset == 2 && handler.Posts == 1, "官方成功结果独立判断");
        var repeated = await service.ConsumeAsync(id,preview.ConfirmationId,true,CancellationToken.None);
        Require(repeated == used && handler.Posts == 1, "已完成确认重复提交不再访问兑换接口");
        Require(handler.ValidEndpoint && handler.ValidAuth && handler.LastCredit == "synthetic-card-1" && handler.LastKey == preview.ConfirmationId,
            "固定官方上游、账号认证、明确卡片及幂等标识精确匹配");
        reports.Add("PASS: 账号与卡片确认绑定、明确确认、固定官方兑换接口和重复提交保护通过。");
        var retry = await service.PrepareAsync(id,"synthetic-card-1",CancellationToken.None);
        handler.TimeoutNext = true;
        var unknown = await service.ConsumeAsync(id,retry.ConfirmationId,true,CancellationToken.None);
        Require(!unknown.Confirmed && unknown.Code == "unknown" && service.Pending(id)?.Recovering == true, "网络结果未知保持待核对状态");
        var postsBefore = handler.Posts;
        await Reject(() => service.PrepareAsync(id,"synthetic-card-2",CancellationToken.None));
        var same = await service.PrepareAsync(id,"synthetic-card-1",CancellationToken.None);
        Require(same.ConfirmationId == retry.ConfirmationId && same.Recovering && handler.Posts == postsBefore, "核对前不能另换卡且不自动兑换");
        handler.Code = "already_redeemed";
        var reconciled = await service.ConsumeAsync(id,same.ConfirmationId,true,CancellationToken.None);
        Require(reconciled.Code == "already_redeemed" && reconciled.Confirmed && handler.LastKey == retry.ConfirmationId &&
            handler.Keys.TakeLast(2).Distinct().Count() == 1 && service.Pending(id) is null, "人工核对必须复用同一幂等标识");
        reports.Add("PASS: 超时结果未知时不重试、不换卡；人工核对只复用同一次兑换并识别官方已处理结果。");
        var expired = await service.PrepareAsync(id,"synthetic-card-1",CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(11);
        postsBefore = handler.Posts;
        await Reject(() => service.ConsumeAsync(id,expired.ConfirmationId,true,CancellationToken.None));
        await Reject(() => service.PrepareAsync(id,"synthetic-card-3",CancellationToken.None));
        await Reject(() => service.PrepareAsync(id,"synthetic-card-4",CancellationToken.None));
        Require(handler.Posts == postsBefore, "过期确认、未知到期时间及未知类型不能消耗");
        foreach (var code in new[] {"nothing_to_reset","no_credit"})
        {
            handler.Code = code;
            var next = await service.PrepareAsync(id,"synthetic-card-2",CancellationToken.None);
            var response = await service.ConsumeAsync(id,next.ConfirmationId,true,CancellationToken.None);
            Require(response.Code == code && response.Confirmed, "官方未重置结果不能被改写为成功");
        }
        reports.Add("PASS: 过期确认、未知卡片状态和类型拒绝使用；无需重置、无卡结果忠实显示。");
        handler.DetailStatus = HttpStatusCode.ServiceUnavailable;
        var cached = await service.RefreshAsync(id,true,CancellationToken.None);
        Require(cached.Stale && cached.AvailableCount == 5 && cached.Cards?.Count == 4, "失败不能伪造零次数或清除上次有效期");
        using var noCache = new AccountResetService(vault,(_,_)=>Task.FromResult(credential),_=>null,new Handler { DetailStatus=HttpStatusCode.Forbidden },clock);
        var unavailable = await noCache.RefreshAsync(otherId,true,CancellationToken.None);
        Require(unavailable.AvailableCount is null && unavailable.Cards is null && unavailable.Stale, "从未查询成功显示未知而非零");
        foreach (var file in Directory.EnumerateFiles(vault.DirectoryPath,"*.dpapi",SearchOption.AllDirectories))
        {
            var plain = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Require(!plain.Contains("synthetic-card") && !plain.Contains("synthetic-reset-token") && !plain.Contains("ConfirmationId"), "重置详情与操作状态仅加密保存");
        }
        reports.Add("PASS: 失败不清零；身份不匹配不查询；重置卡与幂等操作均使用 CurrentUser DPAPI 缓存。");
        return reports;
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (UserFacingException) { return; }
        throw new UserFacingException("隔离重置验证失败：应拒绝的操作没有拒绝。");
    }
    private static void Require(bool condition,string name)
    { if(!condition) throw new UserFacingException("隔离重置验证失败："+name+"。"); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now {get;set;} = new(2026,10,22,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode DetailStatus {get;set;} = HttpStatusCode.OK;
        public string Code {get;set;} = "reset";
        public bool TimeoutNext {get;set;}
        public int Posts {get;private set;}
        public bool ValidEndpoint {get;private set;} = true;
        public bool ValidAuth {get;private set;} = true;
        public string? LastCredit {get;private set;}
        public string? LastKey {get;private set;}
        public List<string?> Keys {get;} = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            ValidAuth &= request.Headers.Authorization?.Parameter == "synthetic-reset-token" &&
                request.Headers.TryGetValues("ChatGPT-Account-Id",out var accounts) && accounts.Single() == "synthetic-reset-account";
            if (request.Method == HttpMethod.Get)
            {
                ValidEndpoint &= request.RequestUri?.AbsoluteUri == "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
                return new(DetailStatus) {Content = new StringContent("""
                    {"available_count":5,"credits":[
                      {"id":"synthetic-card-2","reset_type":"codex_rate_limits","status":"available","granted_at":"2026-10-20T00:00:00Z","expires_at":null},
                      {"id":"synthetic-card-1","reset_type":"codex_rate_limits","status":"available","granted_at":"2026-10-20T00:00:00Z","expires_at":"2026-10-23T00:00:00Z","email":"private-reset@example.test"},
                      {"id":"synthetic-card-3","reset_type":"codex_rate_limits","status":"available"},
                      {"id":"synthetic-card-4","reset_type":"future_type","status":"future_status","expires_at":null}]}
                    """,Encoding.UTF8,"application/json")};
            }
            Posts++; ValidEndpoint &= request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume";
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            LastCredit = body.RootElement.GetProperty("credit_id").GetString(); LastKey = body.RootElement.GetProperty("redeem_request_id").GetString(); Keys.Add(LastKey);
            if (TimeoutNext) {TimeoutNext=false;throw new TaskCanceledException();}
            return new(HttpStatusCode.OK) {Content = new StringContent(JsonSerializer.Serialize(new {code=Code,windows_reset=2}),Encoding.UTF8,"application/json")};
        }
    }
}
