using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

internal static class NativeAdapterCatalogSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        void Check(bool condition){if(!condition)throw new UserFacingException("自适应原生资源合成验证失败。");}
        void Refuses(Action action){try{action();}catch(UserFacingException){return;}throw new UserFacingException("不明确的原生资源没有被拒绝。");}
        const string initial="let empty={active:!1,eligible:!1,hardBlocked:!1},quota=derive(Store,(host,{get:read})=>{let a=read(identity),r=read(usage);if(r.user_id!==a.userId||r.account_id!==a.authenticatedAccountId||r.plan_type!==a.plan)return empty;return{active:!1,eligible:!0,hardBlocked:r.rate_limit.allowed===!1}},{isEqual:same});";
        var first=NativeAdapterPlan.Create("app://-/assets/app-initial-12345678.js",initial,"signal",false);
        var adapted=first.Adapt(initial);
        Check(adapted.Contains("if(__caNativeGateAllows(read,host))return empty;",StringComparison.Ordinal));
        Check(adapted.Contains("__caNativeGateAtom=signal(Store,__caNativeGateRead,{onMount:set=>__caNativeGateSubscribe({set})})",StringComparison.Ordinal));
        Check(adapted.Contains("r.user_id!==a.userId||r.account_id!==a.authenticatedAccountId",StringComparison.Ordinal));
        const string work="let quota=derive(Store,({get:read})=>{let a=read(identity),r=read(usage);if(a.authMethod!==`chatgpt`||r.account_id!==a.authenticatedAccountId||r.user_id!==a.userId||r.plan_type!==a.plan||r.rate_limit.allowed!==!1)return!1;return!0});function composer(){let blocked=use(host===`local`&&quota);return blocked||busy||denied}";
        var second=NativeAdapterPlan.Create("app://-/assets/app-primary-87654321.js",work,"signal",true);
        var local=second.Adapt(work);
        Check(local.Contains(second.Anchor+second.Function,StringComparison.Ordinal));
        Check(local.Contains("use(host===`local`&&__caNativeWorkGateAtom)",StringComparison.Ordinal));
        Check(local.Contains("return blocked||busy||denied",StringComparison.Ordinal));
        Refuses(()=>first.Adapt(initial+" "));
        Refuses(()=>NativeAdapterPlan.Create(first.Asset,initial+initial,"signal",false));
        Refuses(()=>NativeAdapterPlan.Create(second.Asset,work+"let other=use(host===`local`&&quota);","signal",true));
        Refuses(()=>NativeAdapterPlan.Create(second.Asset,work.Replace("authenticatedAccountId","differentField",StringComparison.Ordinal),"signal",true));
        Refuses(()=>NativeAdapterPlan.Create(first.Asset,initial,"bad;factory",false));
        Refuses(()=>NativeAdapterPlan.Create(first.Asset,adapted,"signal",false));
        var renamed=initial.Replace("empty","e$",StringComparison.Ordinal).Replace("quota","q$",StringComparison.Ordinal)
            .Replace("derive","d$",StringComparison.Ordinal).Replace("Store","S$",StringComparison.Ordinal);
        Check(NativeAdapterPlan.Create(first.Asset,renamed,"F$",false).Adapt(renamed).Contains("return e$;",StringComparison.Ordinal));
        const string moved="import{x as existing}from`./app-shared-12345678.js`;import{workExport as nativeQuota}from`./app-primary-87654321.js`;function composer(){let blocked=use(host===`local`&&nativeQuota);return blocked||busy||denied}function cloud(){return use(nativeQuota)}";
        var imported=NativeAdapterPlan.CreateImportedWork("app://-/assets/codex-composer-adapter-12345678.js",moved,second,"nativeQuota","app-shared-12345678.js","signalExport","scopeExport","derivedExport");
        var movedAdapted=imported.Adapt(moved);
        Check(movedAdapted.Contains("signalExport as __caNativeSignal,scopeExport as __caNativeScope,derivedExport as __caNativeDerived",StringComparison.Ordinal));
        Check(movedAdapted.Contains("host===`local`&&__caNativeWorkGateAtom",StringComparison.Ordinal)&&movedAdapted.Contains("return blocked||busy||denied",StringComparison.Ordinal));
        Check(movedAdapted.Contains("function cloud(){return use(nativeQuota)}",StringComparison.Ordinal));
        Refuses(()=>imported.Adapt(moved+" "));
        Refuses(()=>NativeAdapterPlan.CreateImportedWork(imported.Asset,moved+moved,second,"nativeQuota","app-shared-12345678.js","signalExport","scopeExport","derivedExport"));
        Refuses(()=>NativeAdapterPlan.CreateImportedWork(imported.Asset,moved,second,"nativeQuota","foreign.js","signalExport","scopeExport","derivedExport"));
        return new[]{"PASS: 自适应资源支持变更文件名及局部标识；保留原始身份条件、Work 共享判断及忙碌/权限表达式。",
            "PASS: 自适应资源拒绝重复结构、非唯一消费者、缺失身份字段、内容哈希变化及重复适配。",
            "PASS: 分离的本地输入模块按实际导出绑定适配；保留云端读取、原始忙碌及权限条件，拒绝重复消费者和非官方模块。"};
    }
}
