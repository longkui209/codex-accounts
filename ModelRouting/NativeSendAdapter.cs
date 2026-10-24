using System.Security.Cryptography;
using System.Text;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Transforms only the audited local-quota atom, in memory. Original assets are never overwritten or distributed.</summary>
internal static class NativeSendAdapter
{
    internal const string Asset="app://-/assets/app-initial-5851df3f6cb1.js";
    internal const string OriginalHash="c70d95da34ba7e5962a559dbf68d0102d10b4b47b877f5d9933629bb5a1f3a10";
    internal const string WorkAsset="app://-/assets/app-primary-fe8a450b3b04.js";
    internal const string WorkOriginalHash="2a05e25229bd7a38c12c55d90f82fb07087e80b4e16070116b215484498478f2";
    internal const string EventName="codex-accounts-inference-gate-v1";
    internal const string SnapshotName="__codexAccountsInferenceGateV1";
    private const string Init="oXa(),cXa(),lXa=";
    private const string Gate="dXa=wc(U,(e,{get:t})=>{if(!t(iXa)";
    private const string WorkInit="Dv(),XP=Xl(K,({get:e})=>";
    private const string WorkComposerUse="vn=X(bt===`local`&&XP)";
    internal const string RuntimePrelude="""
        var __caNativeGateAtom;
        function __caNativeGateRead(){
          const s=globalThis.__codexAccountsInferenceGateV1;
          return s&&s.version===1&&s.connected===true&&s.allowed===true&&Number.isFinite(s.expiresAtMs)&&s.expiresAtMs>Date.now()&&s.expiresAtMs<=Date.now()+30000
            ? {allowed:true,expiresAtMs:s.expiresAtMs,tick:Date.now()} : {allowed:false,tick:Date.now()};
        }
        function __caNativeGateAllows(get,host){
          if(host!=='local'||!__caNativeGateAtom)return false;
          const s=get(__caNativeGateAtom);
          return s?.allowed===true&&s.expiresAtMs>Date.now();
        }
        function __caNativeGateSubscribe({set}){
          const update=()=>set(__caNativeGateRead());
          globalThis.addEventListener('codex-accounts-inference-gate-v1',update);
          const timer=setInterval(update,1000);update();
          return()=>{clearInterval(timer);globalThis.removeEventListener('codex-accounts-inference-gate-v1',update)};
        }
        globalThis.__codexAccountsNativeAdapterV1=true;
        """;
    private const string LocalWorkCallback="({get:e})=>{const native=e(XP),allowed=__caNativeGateAllows(e,'local'),blocked=native&&!allowed;globalThis.__codexAccountsLocalWorkQuotaV1={nativeBlocked:native,effectiveBlocked:blocked,checkedAtMs:Date.now()};return blocked}";
    internal static string Adapt(string source)
        =>Adapt(Asset,source);
    internal static string Adapt(string asset,string source)
    {
        var hash=asset==Asset?OriginalHash:asset==WorkAsset?WorkOriginalHash:null;
        if(hash is null || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()!=hash)
            throw new UserFacingException("当前官方前端版本尚未通过适配核验，保持原生发送判断，未替换代码。");
        return asset==Asset?TransformAudited(source):TransformWorkAudited(source);
    }
    internal static string TransformWorkAudited(string source)
    {
        if(Count(source,WorkInit)!=1 || Count(source,WorkComposerUse)!=1)
            throw new UserFacingException("原生 Work 发送额度结构不匹配，未应用这部分适配。");
        // XP is shared with cloud views. Leave XP untouched and adapt only its local-composer consumer.
        return RuntimePrelude.Replace("globalThis.__codexAccountsNativeAdapterV1=true;","globalThis.__codexAccountsNativeWorkAdapterV1=true;",StringComparison.Ordinal)+
            "\nvar __caNativeWorkGateAtom;\n"+source.Replace(WorkInit,
                "Dv(),__caNativeGateAtom=Md(K,__caNativeGateRead,__caNativeGateSubscribe),__caNativeWorkGateAtom=Xl(K,"+LocalWorkCallback+"),XP=Xl(K,({get:e})=>",StringComparison.Ordinal)
            .Replace(WorkComposerUse,"vn=X(bt===`local`&&__caNativeWorkGateAtom)",StringComparison.Ordinal);
    }
    internal static string TransformAudited(string source)
    {
        if(Count(source,Init)!=1 || Count(source,Gate)!=1)throw new UserFacingException("原生前端结构不匹配，未应用发送额度适配。");
        return RuntimePrelude+"\n"+source.Replace(Init,"oXa(),cXa(),__caNativeGateAtom=Nl(U,__caNativeGateRead,__caNativeGateSubscribe),lXa=",StringComparison.Ordinal)
            .Replace(Gate,"dXa=wc(U,(e,{get:t})=>{if(__caNativeGateAllows(t,e))return uXa;if(!t(iXa)",StringComparison.Ordinal);
    }
    private static int Count(string source,string value)
    {var count=0;for(var at=source.IndexOf(value,StringComparison.Ordinal);at>=0;at=source.IndexOf(value,at+value.Length,StringComparison.Ordinal))count++;return count;}
    internal static string DecisionFunctionForValidation(string source)
    {
        const string beginning="dXa=wc(U,";var at=source.IndexOf(beginning,StringComparison.Ordinal);
        if(at<0)throw new IOException();at+=beginning.Length;
        var end=source.IndexOf(",{isEqual:",at,StringComparison.Ordinal);
        if(end<at || end-at>8192)throw new IOException();return source[at..end];
    }
    internal static string WorkDecisionForValidation(string source)
    {
        const string beginning="XP=Xl(K,";var at=source.IndexOf(beginning,StringComparison.Ordinal);
        if(at<0)throw new IOException();at+=beginning.Length;
        var end=source.IndexOf("})})))()}",at,StringComparison.Ordinal);
        if(end<at || end-at>8192)throw new IOException();return source[at..(end+1)];
    }
    internal static string ComposerDisabledForValidation(string source)
    {
        const string beginning="let Zn=";var at=source.IndexOf(beginning,StringComparison.Ordinal);
        if(at<0)throw new IOException();at+=beginning.Length;
        var end=source.IndexOf(",Qn;",at,StringComparison.Ordinal);
        if(end<at || end-at>512)throw new IOException();return source[at..end];
    }
    internal static string LocalWorkDecisionForValidation(string source)
    {
        const string beginning="__caNativeWorkGateAtom=Xl(K,";var at=source.IndexOf(beginning,StringComparison.Ordinal);
        if(at<0)throw new IOException();at+=beginning.Length;
        var end=source.IndexOf("),XP=Xl(K,",at,StringComparison.Ordinal);
        if(end<at || end-at>512)throw new IOException();return source[at..end];
    }
}
