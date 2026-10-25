using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Plans from the current signed installation. Each response must match its exact locally read bytes.</summary>
internal sealed class NativeAdapterCatalog
{
    private readonly Dictionary<string, NativeAdapterPlan> plans;
    private NativeAdapterCatalog(IEnumerable<NativeAdapterPlan> values) => plans=values.ToDictionary(p=>p.Asset,StringComparer.Ordinal);
    internal IEnumerable<string> Assets=>plans.Keys;
    internal bool TryPlan(string? asset,out NativeAdapterPlan? plan)=>plans.TryGetValue(asset??"",out plan);
    internal static NativeAdapterCatalog Installed(Action<string>? progress=null)
    {
        var executable=DesktopLaunch.FindInstalledDesktop();
        var archive=Path.Combine(Path.GetDirectoryName(executable)!,"resources","app.asar");
        NativeRuntimeLease.Plain(archive);
        using var input=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var reader=new BinaryReader(input,Encoding.UTF8,true);
        var prefix=reader.ReadBytes(16);
        if(prefix.Length!=16)throw Refused();
        var pickle=BitConverter.ToUInt32(prefix,4);var jsonLength=BitConverter.ToUInt32(prefix,12);
        if(pickle<jsonLength+8||jsonLength>32*1024*1024)throw Refused();
        using var document=JsonDocument.Parse(reader.ReadBytes(checked((int)jsonLength)));
        var assets=document.RootElement.GetProperty("files").GetProperty("webview").GetProperty("files").GetProperty("assets").GetProperty("files");
        var offset=8L+pickle;
        string Read(string name)
        {
            if(!Regex.IsMatch(name,@"\A(?:app-(?:initial|primary|shared)|codex-composer-adapter)-[a-f0-9]{8,32}\.js\z",RegexOptions.CultureInvariant))throw Refused();
            var entry=assets.GetProperty(name);
            if(entry.TryGetProperty("unpacked",out _)||entry.TryGetProperty("link",out _))throw Refused();
            var size=entry.GetProperty("size").GetInt32();
            var at=long.Parse(entry.GetProperty("offset").GetString()!,System.Globalization.CultureInfo.InvariantCulture)+offset;
            if(size<=0||size>32*1024*1024||at<offset||at>input.Length-size)throw Refused();
            input.Position=at;var bytes=reader.ReadBytes(size);if(bytes.Length!=size)throw Refused();
            return new UTF8Encoding(false,true).GetString(bytes);
        }
        var result=new List<NativeAdapterPlan>();
        foreach(var category in new[]{"initial","primary"})
        {
            progress?.Invoke(category+"-asset-discovery");
            var names=assets.EnumerateObject().Select(p=>p.Name).Where(n=>Regex.IsMatch(n,@"\Aapp-"+category+@"-[a-f0-9]{8,32}\.js\z")).ToArray();
            if(names.Length!=1)throw Refused();
            var name=names[0];var source=Read(name);
            var imports=Regex.Matches(source,"import\\{(?<spec>[^}]+)\\}from[\"`](?:\\./)(?<module>app-shared-[a-f0-9]{8,32}\\.js)[\"`]",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
            var candidates=new List<string>();
            foreach(Match import in imports)
            {
                progress?.Invoke(category+"-signal-contract");
                var shared=Read(import.Groups["module"].Value);
                // A family requires a key and cannot be read as a single signal. Verify the
                // scalar factory, including its onMount(set, scope) contract, before importing it.
                var signals=Regex.Matches(shared,@"function (?<name>[\w$]+)\(e,t,n\)\{let r=[\w$]+\(`signal`,e,\(r,i\)=>\{let a=[\w$]+\(n=>typeof t==`function`\?t\([\w$]+\(e,i,r,n\)\):t,n\?\.isEqual,n\?\.getServerSnapshot\),o=n\?\.onMount;return o!=null&&\([\w$]+\(r,a\),a\.onMount=t=>o\(e=>\{t\(e\)\},[\w$]+\(e,i,r\)\)\?\?void 0\),a\},\(e,t\)=>\{[\w$]+\(t,e,`signal`,\{debugLabel:r\.debugLabel,writeValue:n=>\{[\w$]+\(t\.store,e,n\)\}\}\)\}\);return r\}",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
                foreach(Match helper in signals)
                {
                    var exported=Regex.Matches(shared,Regex.Escape(helper.Groups["name"].Value)+@" as (?<export>[\w$]+)(?=,|\})",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
                    foreach(Match symbol in exported)
                    {
                        var alias=Regex.Match(import.Groups["spec"].Value,@"(?:\A|,)"+Regex.Escape(symbol.Groups["export"].Value)+@" as (?<alias>[\w$]+)(?=,|\z)");
                        if(alias.Success)candidates.Add(alias.Groups["alias"].Value);
                    }
                }
            }
            var distinct=candidates.Distinct(StringComparer.Ordinal).ToArray();if(distinct.Length!=1)throw Refused();
            progress?.Invoke(category+"-gate-structure");
            var plan=NativeAdapterPlan.Create("app://-/assets/"+name,source,distinct[0],category=="primary",requireWorkConsumer:false);
            if(category=="primary")
            {
                if(NativeAdapterPlan.WorkConsumerCount(source,plan.NativeAtom)==1)
                    plan=NativeAdapterPlan.Create(plan.Asset,source,distinct[0],true);
                else
                {
                    // Newer builds move the local Codex composer to a separate chunk. Follow
                    // the actual export/import binding; never replace the shared Work atom.
                    var exports=Regex.Matches(source,@"(?<![\w$])"+Regex.Escape(plan.NativeAtom)+@" as (?<export>"+NativeAdapterPlan.Identifier+@")(?>,|\})");
                    if(exports.Count!=1)throw Refused();
                    var bindings=new List<(string Module,string Signal,string Store,string Derived)>();
                    foreach(Match import in imports)
                    {
                        string? Exported(string alias)
                        {
                            var match=Regex.Match(import.Groups["spec"].Value,@"(?:\A|,)(?<export>"+NativeAdapterPlan.Identifier+@") as "+Regex.Escape(alias)+@"(?=,|\z)");
                            return match.Success?match.Groups["export"].Value:null;
                        }
                        var signal=Exported(plan.Factory);var store=Exported(plan.Store);var derived=Exported(plan.Derived);
                        if(signal is not null&&store is not null&&derived is not null)bindings.Add((import.Groups["module"].Value,signal,store,derived));
                    }
                    if(bindings.Count!=1)throw Refused();var binding=bindings[0];
                    var moved=new List<(NativeAdapterPlan Plan,string Source)>();
                    foreach(var candidate in assets.EnumerateObject().Select(p=>p.Name).Where(n=>Regex.IsMatch(n,@"\Acodex-composer-adapter-[a-f0-9]{8,32}\.js\z")))
                    {
                        var chunk=Read(candidate);
                        var nativeImport=Regex.Matches(chunk,"import\\{(?<spec>[^}]+)\\}from[\"`]\\./"+Regex.Escape(name)+"[\"`]");
                        foreach(Match import in nativeImport)
                        {
                            var alias=Regex.Match(import.Groups["spec"].Value,@"(?:\A|,)"+Regex.Escape(exports[0].Groups["export"].Value)+@" as (?<alias>"+NativeAdapterPlan.Identifier+@")(?>,|\z)");
                            if(!alias.Success||NativeAdapterPlan.WorkConsumerCount(chunk,alias.Groups["alias"].Value)!=1)continue;
                            moved.Add((NativeAdapterPlan.CreateImportedWork("app://-/assets/"+candidate,chunk,plan,alias.Groups["alias"].Value,binding.Module,binding.Signal,binding.Store,binding.Derived),chunk));
                        }
                    }
                    if(moved.Count!=1)throw Refused();plan=moved[0].Plan;source=moved[0].Source;
                }
            }
            _=plan.Adapt(source); // Validate the complete replacement before either resource can be intercepted.
            result.Add(plan);
        }
        progress?.Invoke("installation-stability");
        if(!string.Equals(executable,DesktopLaunch.FindInstalledDesktop(),StringComparison.OrdinalIgnoreCase))throw Refused();
        return new NativeAdapterCatalog(result);
    }
    private static UserFacingException Refused()=>new("当前官方资源结构未通过自适应校验，保留原生发送判断。");
}

internal sealed record NativeAdapterPlan(string Asset,string SourceHash,bool Work,string Factory,string Store,string Derived,
    string NativeAtom,string Inactive,string Anchor,string Consumer,string Host,string Getter,string Function,string WorkCallback)
{
    internal const string Identifier=@"[A-Za-z_$][A-Za-z0-9_$]*";
    private const string Id=Identifier;
    internal string? ImportAnchor {get;init;}
    internal string? ImportReplacement {get;init;}
    internal string? ImportedConsumerReplacement {get;init;}
    internal string? ImportedSignalExport {get;init;}
    internal string? ImportedDerivedExport {get;init;}
    internal string? DefinitionAsset {get;init;}
    internal string SignalInitialization=>"__caNativeGateAtom="+Factory+"("+Store+",__caNativeGateRead,{onMount:set=>__caNativeGateSubscribe({set})})";
    internal static NativeAdapterPlan Create(string asset,string source,string factory,bool work,bool requireWorkConsumer=true)
    {
        if(!Regex.IsMatch(factory,"\\A"+Id+"\\z")||source.Contains("__caNativeGateAtom",StringComparison.Ordinal))throw Refused();
        var pattern=work
            ?@"(?<atom>"+Id+@")=(?<derived>"+Id+@")\((?<store>"+Id+@"),(?<function>\(\{get:(?<get>"+Id+@")\}\)=>\{)let [^{};]{1,160};if\("+Id+@"\.authMethod!==`chatgpt`"
            :@"(?<inactive>"+Id+@")=\{active:!1,eligible:!1,hardBlocked:!1\},(?<atom>"+Id+@")=(?<derived>"+Id+@")\((?<store>"+Id+@"),(?<function>\((?<host>"+Id+@"),\{get:(?<get>"+Id+@")\}\)=>\{.{1,7000}?)\},\{isEqual:";
        var matches=Regex.Matches(source,pattern,RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
        if(matches.Count!=1)throw Refused();var m=matches[0];
        string G(string name)=>m.Groups[name].Value;
        var function=work?ReadFunction(source,m.Groups["function"].Index):G("function")+"}";
        if(!new[]{"user_id","account_id","plan_type","rate_limit"}.All(w=>function.Contains(w,StringComparison.Ordinal))||
            !function.Contains("authenticatedAccountId",StringComparison.Ordinal))throw Refused();
        var anchor=G("atom")+"="+G("derived")+"("+G("store")+",";
        if(Count(source,anchor)!=1)throw Refused();
        var consumer="";var host=G("host");
        if(work&&requireWorkConsumer)
        {
            var consumers=Regex.Matches(source,@"(?<local>"+Id+@")=(?<read>"+Id+@")\((?<host>"+Id+@")==="+"`local`&&"+Regex.Escape(G("atom"))+@"\)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
            if(consumers.Count!=1)throw Refused();consumer=consumers[0].Value;host=consumers[0].Groups["host"].Value;
        }
        var callback="({get:e})=>{const native=e("+G("atom")+"),allowed=__caNativeGateAllows(e,'local'),blocked=native&&!allowed;globalThis.__codexAccountsLocalWorkQuotaV1={nativeBlocked:native,effectiveBlocked:blocked,checkedAtMs:Date.now()};return blocked}";
        return new(asset,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(),work,factory,G("store"),G("derived"),G("atom"),G("inactive"),anchor,consumer,host,G("get"),function,callback);
    }
    private static MatchCollection WorkConsumers(string source,string atom)=>Regex.Matches(source,@"(?<local>"+Id+@")=(?<read>"+Id+@")\((?<host>"+Id+@")==="+"`local`&&"+Regex.Escape(atom)+@"\)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
    internal static int WorkConsumerCount(string source,string atom)=>WorkConsumers(source,atom).Count;
    internal static NativeAdapterPlan CreateImportedWork(string asset,string source,NativeAdapterPlan definition,string atom,string shared,string signalExport,string storeExport,string derivedExport)
    {
        if(!definition.Work||source.Contains("__caNative",StringComparison.Ordinal)||
            !new[]{atom,signalExport,storeExport,derivedExport}.All(value=>Regex.IsMatch(value,"\\A"+Id+"\\z"))||
            !Regex.IsMatch(shared,@"\Aapp-shared-[a-f0-9]{8,32}\.js\z"))throw Refused();
        var consumers=WorkConsumers(source,atom);if(consumers.Count!=1)throw Refused();var consumer=consumers[0];
        var imports=Regex.Matches(source,"import\\{[^}]+\\}from[\"`]\\./"+Regex.Escape(shared)+"[\"`]");
        if(imports.Count!=1)throw Refused();var import=imports[0].Value;
        var callback="({get:e})=>{const native=e("+atom+"),allowed=__caNativeGateAllows(e,'local'),blocked=native&&!allowed;globalThis.__codexAccountsLocalWorkQuotaV1={nativeBlocked:native,effectiveBlocked:blocked,checkedAtMs:Date.now()};return blocked}";
        var plan=definition with{Asset=asset,SourceHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(),
            Factory="__caNativeSignal",Store="__caNativeScope",Derived="__caNativeDerived",NativeAtom=atom,Anchor=consumer.Value,Consumer=consumer.Value,Host=consumer.Groups["host"].Value,WorkCallback=callback,
            ImportedSignalExport=signalExport,ImportedDerivedExport=derivedExport,DefinitionAsset=definition.Asset,
            ImportAnchor=import,ImportReplacement=import.Replace("import{","import{"+signalExport+" as __caNativeSignal,"+storeExport+" as __caNativeScope,"+derivedExport+" as __caNativeDerived,",StringComparison.Ordinal)};
        var prefix=consumer.Value[..(consumer.Value.IndexOf('(')+1)];
        return plan with{ImportedConsumerReplacement=prefix+"("+plan.SignalInitialization.Replace("__caNativeGateAtom=","__caNativeGateAtom??=",StringComparison.Ordinal)+
            ",__caNativeWorkGateAtom??="+plan.Derived+"("+plan.Store+","+callback+"),"+plan.Host+"===`local`&&__caNativeWorkGateAtom))"};
    }
    internal string Adapt(string source)
    {
        if(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()!=SourceHash||Count(source,Anchor)!=1)throw Refused();
        var prelude=Work?NativeSendAdapter.RuntimePrelude.Replace("__codexAccountsNativeAdapterV1=true","__codexAccountsNativeWorkAdapterV1=true",StringComparison.Ordinal):NativeSendAdapter.RuntimePrelude;
        var added=SignalInitialization+",";
        if(Work)
        {
            if(Count(source,Consumer)!=1)throw Refused();
            if(ImportAnchor is not null)
            {
                if(ImportReplacement is null||ImportedConsumerReplacement is null||Count(source,ImportAnchor)!=1)throw Refused();
                return prelude+"\nvar __caNativeWorkGateAtom;\n"+source.Replace(ImportAnchor,ImportReplacement,StringComparison.Ordinal).Replace(Consumer,ImportedConsumerReplacement,StringComparison.Ordinal);
            }
            added+="__caNativeWorkGateAtom="+Derived+"("+Store+","+WorkCallback+"),";
            return prelude+"\nvar __caNativeWorkGateAtom;\n"+source.Replace(Anchor,added+Anchor,StringComparison.Ordinal)
                .Replace(Consumer,Consumer.Replace("&&"+NativeAtom+")","&&__caNativeWorkGateAtom)",StringComparison.Ordinal),StringComparison.Ordinal);
        }
        var body=Function[..(Function.IndexOf("=>{",StringComparison.Ordinal)+3)];
        if(Count(source,Anchor+body)!=1)throw Refused();
        return prelude+"\n"+source.Replace(Anchor+body,added+Anchor+body+"if(__caNativeGateAllows("+Getter+","+Host+"))return "+Inactive+";",StringComparison.Ordinal);
    }
    private static int Count(string text,string value)=>text.Split(value,StringSplitOptions.None).Length-1;
    private static string ReadFunction(string source,int start)
    {
        var opening=source.IndexOf("=>{",start,StringComparison.Ordinal)+2;
        if(opening<start||opening-start>128)throw Refused();
        var depth=0;char quote='\0';var escaped=false;
        for(var i=opening;i<source.Length&&i<opening+8192;i++)
        {
            var c=source[i];
            if(quote!='\0'){if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c==quote)quote='\0';continue;}
            if(c is '\'' or '"' or '`'){quote=c;continue;}
            if(c=='{')depth++;else if(c=='}'&&--depth==0)return source[start..(i+1)];
        }
        throw Refused();
    }
    private static UserFacingException Refused()=>new("原生额度结构不唯一或内容已改变，未启用这部分发送适配。");
}
