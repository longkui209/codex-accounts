using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;
using Guard=CodexAccountSwitcher.Validation.NativeBrowserRegistrationGuard;

namespace CodexAccountSwitcher.Validation;

internal static class NativeBrowserRegistrationGuardSelfTests
{
    internal static IReadOnlyList<string> Run(string outputRoot)
    {
        var root=Path.Combine(Path.GetFullPath(outputRoot),"native-registration-synthetic-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);FilePrivacy.ApplyDirectory(root);
        var testRoot=Path.Combine(root,"isolated");var testHome=Path.Combine(testRoot,"home");
        var originalHost=Path.Combine(root,"existing-host","extension-host.exe");
        var testHost=Path.Combine(testHome,"plugins","extension-host.exe");
        var checks=new List<string>();
        void Check(bool value,string label)
        {
            if(!value)throw new UserFacingException("原生浏览器恢复隔离测试失败："+label+"。");
            checks.Add("PASS: "+label+"。");
        }
        static byte[] Bytes(JsonObject value)=>JsonSerializer.SerializeToUtf8Bytes(value);
        static JsonObject Manifest(string path,string description)=>new()
        {
            ["name"]="com.openai.codexextension",["type"]="stdio",["path"]=path,
            ["description"]=description,["allowed_origins"]=new JsonArray("chrome-extension://synthetic/")
        };
        static JsonObject Entry(string home,string id)=>new(){["entryId"]=id,["paths"]=new JsonObject{["codexHome"]=home},["unknownField"]="preserve"};

        var unrelated=Entry(Path.Combine(root,"another-home"),"other");
        var prefix=Entry(testHome+"-other","prefix");
        var descendant=Entry(Path.Combine(testHome,"nested"),"descendant");
        var currentIndex=new JsonObject
        {
            ["schemaVersion"]=2,["futureMetadata"]="keep",
            ["entries"]=new JsonArray(unrelated.DeepClone(),Entry(testHome,"own"),prefix.DeepClone(),
                Entry(testHome.ToUpperInvariant()+"\\","own-case-variant"),descendant.DeepClone(),new JsonObject{["futureUnknownEntry"]=true})
        };
        var indexBytes=Bytes(currentIndex);
        var indexPlan=Guard.PlanIndex(Bytes(new JsonObject{["schemaVersion"]=2,["entries"]=new JsonArray()}),indexBytes,testHome,true);
        var recoveredIndex=JsonNode.Parse(indexPlan.Replacement!)!.AsObject();
        var recoveredEntries=recoveredIndex["entries"]!.AsArray();
        Check(indexPlan.RemovedEntries==2&&recoveredEntries.Count==4&&
            JsonNode.DeepEquals(recoveredEntries[0],unrelated)&&JsonNode.DeepEquals(recoveredEntries[1],prefix)&&
            JsonNode.DeepEquals(recoveredEntries[2],descendant)&&recoveredIndex["futureMetadata"]!.GetValue<string>()=="keep",
            "恢复索引只删除规范化后等于本次home的条目，保留前缀、子目录、其他账号和未知字段");
        Check(Guard.PlanIndex(indexPlan.Replacement,indexPlan.Replacement,testHome,true).Replacement is null,
            "不包含本次home的索引保持原字节且重复恢复无写入");

        var before=Bytes(Manifest(originalHost,"original"));
        var changed=Manifest(testHost,"updated-by-other-component");changed["futureField"]=new JsonObject{["preserve"]=true};
        var current=Bytes(changed);
        var manifestPlan=Guard.PlanManifest(before,current,testRoot,true);
        var recoveredManifest=JsonNode.Parse(manifestPlan.Replacement!)!.AsObject();
        Check(manifestPlan.OtherFieldsChanged&&recoveredManifest["path"]!.GetValue<string>()==originalHost&&
            recoveredManifest["description"]!.GetValue<string>()=="updated-by-other-component"&&
            recoveredManifest["futureField"]!["preserve"]!.GetValue<bool>(),
            "manifest仅恢复原path，保留其他字段并报告并发元数据变化");
        var outside=Bytes(Manifest(Path.Combine(testRoot+"-unrelated","extension-host.exe"),"other"));
        var outsidePlan=Guard.PlanManifest(before,outside,testRoot,true);
        Check(outsidePlan.Replacement is null&&!outsidePlan.Delete&&outsidePlan.OtherFieldsChanged,
            "非本次目录及相同前缀的manifest不被改写");
        Check(Guard.PlanManifest(before,null,testRoot,true).Conflict is not null&&Guard.PlanIndex(indexBytes,null,testHome,true).Conflict is not null,
            "原有manifest或索引消失时拒绝宣称恢复成功");
        Check(Guard.PlanManifest(null,current,testRoot,true).Conflict is not null&&
            Guard.PlanManifest(null,Bytes(Manifest(testHost,"synthetic")),testRoot,true).Delete,
            "新manifest仅在全部字段归属已知时允许删除，未知附加字段会阻止删除");

        var file=Path.Combine(root,"manifest.json");File.WriteAllBytes(file,current);FilePrivacy.ApplyFile(file);
        var security=new FileInfo(file).GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadData,AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
        var aclBefore=new FileInfo(file).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Guard.ApplyFilePlan(file,current,manifestPlan);
        var aclAfter=new FileInfo(file).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(manifestPlan.Replacement)&&aclBefore==aclAfter,
            "真实File.Replace恢复合成文件且保留目标原ACL");

        var concurrent=Encoding.UTF8.GetBytes("synthetic concurrent update");File.WriteAllBytes(file,concurrent);
        Reject(()=>Guard.ApplyFilePlan(file,current,manifestPlan));
        Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(concurrent)&&Directory.GetFiles(root,"*.codex-accounts-recovery-*").Length==0,
            "恢复前内容变化被拒绝且并发文件与临时文件清理结果正确");
        var missing=Path.Combine(root,"missing-manifest.json");
        Reject(()=>Guard.ApplyFilePlan(missing,current,manifestPlan));
        Check(!File.Exists(missing)&&Directory.GetFiles(root,"*.codex-accounts-recovery-*").Length==0,
            "写入前原文件已缺失时不创建替代文件");
        var blocked=Guard.PlanManifest(before,current,testRoot,false);
        Reject(()=>Guard.ApplyFilePlan(file,concurrent,blocked));
        Check(blocked.Conflict is not null&&Guard.PlanIndex(indexBytes,indexBytes,testHome,false).Conflict is not null&&
            File.ReadAllBytes(file).AsSpan().SequenceEqual(concurrent),
            "callerCleanup=false时manifest和索引策略拒绝恢复且真实合成文件不变");

        var registryBefore=new Guard.RegistryImage(true,true,Path.Combine(root,"original-manifest.json"),RegistryValueKind.String);
        var registryOwned=new Guard.RegistryImage(true,true,Path.Combine(testRoot,"manifest.json"),RegistryValueKind.String,1,1);
        var registryPlan=Guard.PlanRegistry(registryBefore,registryOwned,true,testRoot,true);
        Check(registryPlan.Change&&!registryPlan.RemoveDefault&&!registryPlan.RemoveEmptyKey&&registryPlan.Value==registryBefore.DefaultValue,
            "合成registry模型只恢复本次默认值，不删除其他值或子键");
        var foreign=new Guard.RegistryImage(true,true,Path.Combine(root,"foreign-manifest.json"),RegistryValueKind.String);
        Check(!Guard.PlanRegistry(registryBefore,foreign,true,testRoot,true).Change&&
            Guard.PlanRegistry(registryBefore,foreign,true,testRoot,true).OtherFieldsChanged,
            "即使曾观察到本次注册，当前外部registry路径仍保留");
        var vanished=new Guard.RegistryImage(false,false,null,RegistryValueKind.None);
        Check(Guard.PlanRegistry(registryBefore,vanished,false,testRoot,true).Conflict is not null&&
            Guard.PlanRegistry(registryBefore,vanished,true,testRoot,true).Value==registryBefore.DefaultValue,
            "registry消失恢复必须有本次归属观察证据");
        var absentBefore=new Guard.RegistryImage(false,false,null,RegistryValueKind.None);
        Check(Guard.PlanRegistry(absentBefore,registryOwned,true,testRoot,true) is {RemoveDefault:true,RemoveEmptyKey:false}&&
            Guard.PlanRegistry(absentBefore,registryOwned with {OtherValueCount=0,SubKeyCount=0},true,testRoot,true) is {RemoveDefault:true,RemoveEmptyKey:true},
            "新registry键只有在无其他值和子键时才计划删除精确空叶子");
        Check(Guard.PlanRegistry(registryBefore,registryOwned,true,testRoot,false) is {Change:false,Conflict:not null},
            "callerCleanup=false同时阻止registry恢复计划");
        File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,synthetic_files_only=true,real_registry_read_or_written=false,checks},new JsonSerializerOptions{WriteIndented=true}));
        return checks;
    }

    private static void Reject(Action action)
    {
        try{action();}catch(IOException){return;}
        throw new UserFacingException("恢复保护没有拒绝不安全的合成状态。");
    }
}
