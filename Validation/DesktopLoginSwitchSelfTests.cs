using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.Validation;

public static class DesktopLoginSwitchSelfTests
{
    private static readonly RuntimeStatus Closed=new(true,"Synthetic closed",[]);
    private static readonly RuntimeStatus Busy=new(false,"Synthetic busy",[]);
    public static async Task<IReadOnlyList<string>> RunAsync(string outputRoot)
    {
        var root=Path.Combine(Path.GetFullPath(outputRoot),"desktop-switch-synthetic-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);FilePrivacy.ApplyDirectory(root);
        var reports=new List<string>();
        using(var data=new Scenario(root,"cancel"))
        {
            var service=data.Service();
            await Rejected(()=>service.RequestAsync(data.TargetId,false,CancellationToken.None));
            var request=await service.RequestAsync(data.TargetId,true,CancellationToken.None);
            Require(request.State=="queued" && service.Status()?.Id==request.Id,"persistent intent");data.Unchanged();
            await Rejected(()=>service.RequestAsync(data.TargetId,true,CancellationToken.None));
            Require(!service.Cancel(Guid.NewGuid().ToString("N")) && service.Cancel(request.Id) && !service.Cancel(request.Id),"exact cancellation");
            Require((await service.ApplyAtStartupAsync(CancellationToken.None))?.State=="cancelled","cancelled not applied");data.Unchanged();
            reports.Add("PASS: 确认具体目标后仅登记 DPAPI 意图；旧 ID、重复登记及取消后的执行均拒绝，当前认证保持不变。");
        }
        using(var data=new Scenario(root,"quota"))
        {
            foreach(var bad in new[]{data.Usage() with {UsageAllowed=false},data.Usage() with {UsageAllowed=null},
                data.Usage() with {Stale=true},data.Usage() with {Status="unavailable"},data.Usage() with {UpdatedAtUtc=data.Clock.GetUtcNow().AddMinutes(-3)}})
                await Rejected(()=>data.Service((_,_)=>Task.FromResult(bad)).RequestAsync(data.TargetId,true,CancellationToken.None));
            await Rejected(()=>new DesktopLoginSwitchService(data.Vault,data.Home).RequestAsync(data.TargetId,true,CancellationToken.None));
            data.Unchanged();reports.Add("PASS: 无额度、未知、过期、查询失败或无查询能力时不能登记桌面登录切换。");
        }
        using(var data=new Scenario(root,"apply"))
        {
            var service=data.Service();await service.RequestAsync(data.TargetId,true,CancellationToken.None);
            var done=await service.ApplyAtStartupAsync(CancellationToken.None);
            Require(done?.State=="completed" && File.ReadAllBytes(data.AuthPath).AsSpan().SequenceEqual(data.Target),"synthetic switch bytes");
            Require(new LiveState(data.Vault,"routing").SelectedProfile()==data.TargetId,"model and desktop cache selected together");
            using var recovery=data.Vault.ReadBackup(data.Vault.LatestBackupFileName!);var bytes=recovery.CopyBytes();
            try{Require(bytes.AsSpan().SequenceEqual(data.Original),"encrypted prior login backup");}finally{CryptographicOperations.ZeroMemory(bytes);}
            Require(!service.Cancel(done!.Id) && done.Message.Contains("仍以"),"cache completion not native proof");
            reports.Add("PASS: 全部客户端关闭后核对额度再写入合成认证、原字节恢复备份及同一模型订阅；缓存完成不冒充原生发送成功。");
        }
        using(var data=new Scenario(root,"busy"))
        {
            var service=data.Service(guard:()=>Busy);await service.RequestAsync(data.TargetId,true,CancellationToken.None);
            await Rejected(()=>service.ApplyAtStartupAsync(CancellationToken.None));data.Unchanged();
            Require(service.Status()?.State=="failed","busy application failure visible");
            reports.Add("PASS: 登记不会中断当前任务；启动时客户端仍存在则拒绝写入，不强制关闭。");
        }
        using(var data=new Scenario(root,"expired"))
        {
            var service=data.Service();await service.RequestAsync(data.TargetId,true,CancellationToken.None);
            data.Clock.Advance(TimeSpan.FromMinutes(16));
            Require(service.Status()?.State=="expired" && (await service.ApplyAtStartupAsync(CancellationToken.None))?.State=="expired","intent expires");
            data.Unchanged();reports.Add("PASS: 十五分钟过期后不触碰认证，不在下次无关启动时执行旧切换。");
        }
        using(var data=new Scenario(root,"changed-login"))
        {
            var service=data.Service();await service.RequestAsync(data.TargetId,true,CancellationToken.None);
            var third=Scenario.Auth("synthetic-third");
            try{
                File.WriteAllBytes(data.AuthPath,third);
                await Rejected(()=>service.ApplyAtStartupAsync(CancellationToken.None));
                RejectedSync(()=>new AccountSwitchService(data.Vault,data.Home,()=>Closed).Switch(data.TargetId,data.OriginalId));
                Require(File.ReadAllBytes(data.AuthPath).AsSpan().SequenceEqual(third),"new login not overwritten");
            }finally{CryptographicOperations.ZeroMemory(third);}
            reports.Add("PASS: 登记之后桌面登录变化时拒绝覆盖；最终认证写入也校验原身份，防止迟到的切换覆盖新登录。");
        }
        using(var data=new Scenario(root,"cancelled-during-query"))
        using(var stop=new CancellationTokenSource())
        {
            var service=data.Service((_,_)=>{stop.Cancel();return Task.FromResult(data.Usage());});
            try{await service.RequestAsync(data.TargetId,true,stop.Token);throw new UserFacingException("隔离取消检查失败。");}
            catch(OperationCanceledException){}
            Require(service.Status() is null,"cancel before registration");data.Unchanged();
            reports.Add("PASS: 官方额度查询期间取消后，不登记迟到请求或写入认证。");
        }
        using(var data=new Scenario(root,"recovery"))
        {
            var service=data.Service();var request=await service.RequestAsync(data.TargetId,true,CancellationToken.None);
            var jobs=new PrivateJobStore<DesktopLoginSwitchRequest>(data.Vault.DirectoryPath,"desktop-login-switch.dpapi");
            jobs.Locked(_=>(request with {State="applying"},true));
            new AccountSwitchService(data.Vault,data.Home,()=>Closed).Switch(data.TargetId,data.OriginalId);
            Require(!service.Cancel(request.Id),"partial commit cannot be cancelled as unchanged");
            Require((await service.ApplyAtStartupAsync(CancellationToken.None))?.State=="completed","recover selection after cache commit");
            Require(new LiveState(data.Vault,"routing").SelectedProfile()==data.TargetId,"recovered same selected identity");
            reports.Add("PASS: 写入缓存后意外退出可在有效期内恢复后续选择；已开始写入不允许显示为取消且登录未变。");
        }
        using(var data=new Scenario(root,"reserve"))
        {
            var service=data.Service();await service.RequestAsync(data.TargetId,true,CancellationToken.None);
            RejectedSync(()=>new AccountRemovalService(data.Vault,data.Home).RemoveInactive(data.TargetId));
            Require(data.Vault.List().Any(a=>a.Id==data.TargetId),"queued target protected");data.Unchanged();
            reports.Add("PASS: 待同步切换期间的账号移除与登记共用状态锁，不能移除已确认目标。");
        }
        return reports;
    }
    private static async Task Rejected(Func<Task> action)
    {try{await action();}catch(UserFacingException){return;}throw new UserFacingException("隔离桌面切换检查未按预期拒绝。");}
    private static void RejectedSync(Action action)
    {try{action();}catch(UserFacingException){return;}throw new UserFacingException("隔离桌面切换检查未按预期拒绝。");}
    private static void Require(bool value,string name){if(!value)throw new UserFacingException("隔离桌面切换验证失败："+name+"。");}
    private sealed class Clock:TimeProvider
    {private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;public void Advance(TimeSpan value)=>now+=value;}
    private sealed class Scenario:IDisposable
    {
        public readonly string Home,AuthPath,TargetId,OriginalId;
        public readonly VaultStore Vault;
        public readonly byte[] Original=Auth("synthetic-original"),Target=Auth("synthetic-target");
        public readonly Clock Clock=new();
        public Scenario(string root,string name)
        {
            Home=Path.Combine(root,name,"home");Directory.CreateDirectory(Home);FilePrivacy.ApplyDirectory(Home);
            AuthPath=Path.Combine(Home,"auth.json");File.WriteAllBytes(AuthPath,Original);FilePrivacy.ApplyFile(AuthPath);
            Vault=new VaultStore(Path.Combine(root,name,"vault"));
            using var original=AuthSnapshot.FromBytes(Original);OriginalId=Vault.Save(original,"Synthetic native A").Id;
            using var target=AuthSnapshot.FromBytes(Target);TargetId=Vault.Save(target,"Synthetic target B").Id;
        }
        public AccountUsage Usage()=>new("ok","Plus",null,null,null,Clock.GetUtcNow(),Clock.GetUtcNow(),"Synthetic only"){UsageAllowed=true};
        public DesktopLoginSwitchService Service(Func<string,CancellationToken,Task<AccountUsage>>? check=null,Func<RuntimeStatus>? guard=null)
            =>new(Vault,Home,check??((_,_)=>Task.FromResult(Usage())),guard??(()=>Closed),Clock);
        public void Unchanged()=>Require(File.ReadAllBytes(AuthPath).AsSpan().SequenceEqual(Original),"original unchanged");
        public static byte[] Auth(string id)
        {
            var payload=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {sub=id,email="synthetic@example.test",exp=DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()}));
            try{
                var jwt="eyJhbGciOiJSUzI1NiJ9."+Convert.ToBase64String(payload).TrimEnd('=').Replace('+','-').Replace('/','_')+".syntheticSignature";
                return JsonSerializer.SerializeToUtf8Bytes(new {auth_mode="chatgpt",tokens=new {account_id=id,access_token=jwt,id_token=jwt,refresh_token="synthetic-refresh-only"}});
            }finally{CryptographicOperations.ZeroMemory(payload);}
        }
        public void Dispose(){CryptographicOperations.ZeroMemory(Original);CryptographicOperations.ZeroMemory(Target);}
    }
}
