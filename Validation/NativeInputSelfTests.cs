using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

public static class NativeInputSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        var now=DateTimeOffset.UtcNow;
        var allowed=new AccountUsage("ok","Plus",null,null,null,now,now,"Synthetic only"){UsageAllowed=true};
        Require(InferenceGateService.IsUsable(allowed,now),"explicit official allow");
        foreach(var entry in new[]{allowed with {UsageAllowed=false},allowed with {UsageAllowed=null},allowed with {Stale=true},
            allowed with {Status="reauth-required"},allowed with {UpdatedAtUtc=now.AddSeconds(-91)},allowed with {UpdatedAtUtc=now.AddSeconds(31)},allowed with {UpdatedAtUtc=null}})
            Require(!InferenceGateService.IsUsable(entry,now),"unavailable or old quota rejected");
        Require(!InferenceGateService.IsUsable(null,now),"missing quota is unknown");
        const string fixture="prefix;oXa(),cXa(),lXa=1;dXa=wc(U,(e,{get:t})=>{if(!t(iXa))return uXa;return{hardBlocked:true}},{isEqual:equal});keepNativeIdentityAndModels();";
        var adapted=NativeSendAdapter.TransformAudited(fixture);
        Require(adapted.Contains("__caNativeGateAtom=Nl(U,") && adapted.Contains("if(__caNativeGateAllows(t,e))return uXa;") &&
            adapted.EndsWith("keepNativeIdentityAndModels();",StringComparison.Ordinal),"only audited quota atom altered");
        Reject(()=>NativeSendAdapter.TransformAudited(fixture+fixture));
        Reject(()=>NativeSendAdapter.TransformAudited("unknown official frontend"));
        Reject(()=>NativeSendAdapter.Adapt(fixture));
        const string workFixture="prefix;Dv(),XP=Xl(K,({get:e})=>true);vn=X(bt===`local`&&XP);keepCloudAndRemoteAndIdentity();";
        var work=NativeSendAdapter.TransformWorkAudited(workFixture);
        Require(work.Contains("__caNativeGateAtom=Md(K,") && work.Contains("vn=X(bt===`local`&&__caNativeWorkGateAtom)") &&
            work.Contains("XP=Xl(K,({get:e})=>true)") && work.EndsWith("keepCloudAndRemoteAndIdentity();",StringComparison.Ordinal),"local Work consumer only");
        Reject(()=>NativeSendAdapter.TransformWorkAudited(workFixture+workFixture));
        Reject(()=>NativeSendAdapter.TransformWorkAudited("unknown work frontend"));
        Reject(()=>NativeSendAdapter.Adapt(NativeSendAdapter.WorkAsset,workFixture));
        const string roaming=@"C:\synthetic-roaming";
        var owl=Path.Combine(roaming,"Codex","web","Codex");var classic=Path.Combine(roaming,"Codex");
        Require(NativeBrowserProfile.Resolve(roaming,null,path=>path==Path.Combine(owl,"Default"))==owl,"existing Owl profile");
        Require(NativeBrowserProfile.Resolve(roaming,null,path=>path==Path.Combine(classic,"Default"))==classic,"existing classic profile");
        Reject(()=>NativeBrowserProfile.Resolve(roaming,null,_=>true));Reject(()=>NativeBrowserProfile.Resolve(roaming,null,_=>false));
        Require(NativeBrowserProfile.Resolve(roaming,owl,path=>path==owl)==owl,"explicit existing native profile");
        return ["PASS: 原生发送桥只接受明确、近期、未过期的官方推理账号允许状态；缺失、失败和旧数据均保留原拦截。",
            "PASS: 内存适配仅修改确定的本地额度原子，保留其他原生条件；未知哈希、重复锚点和结构变化均拒绝，原资源不写入。",
            "PASS: 管道复用既有 Owl 或 Electron 浏览器配置；不存在或不唯一时拒绝，不创建新的桌面身份配置。",
            "PASS: 第二套 Work 额度检查仅替换本地输入框消费者，原共用额度对象及云端调用不变；未知哈希、结构重复均拒绝。"];
    }
    private static void Require(bool value,string label){if(!value)throw new UserFacingException("原生发送桥隔离检查失败："+label+"。");}
    private static void Reject(Func<string> action){try{action();}catch(UserFacingException){return;}throw new UserFacingException("不兼容前端未被拒绝。");}
}
