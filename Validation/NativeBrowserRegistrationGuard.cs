using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;

namespace CodexAccountSwitcher.Validation;

/// <summary>Diagnostic-only recovery for the exact native-host locations written by the audited desktop.</summary>
internal sealed class NativeBrowserRegistrationGuard
{
    private const string RegistryParent = @"Software\Google\Chrome\NativeMessagingHosts";
    private static readonly string[] HostNames = {"com.openai.codexextension","com.openai.codexextension.dev","com.openai.codexextension.internal"};
    private sealed record FileBefore(string Path, byte[]? Bytes);
    private sealed record RegistryBefore(string Host, bool KeyExisted, bool DefaultExisted, string? DefaultValue, RegistryValueKind Kind);
    internal sealed record FileRecoveryPlan(byte[]? Replacement=null,bool Delete=false,bool OtherFieldsChanged=false,int RemovedEntries=0,string? Conflict=null);
    internal sealed record RegistryImage(bool KeyExisted,bool DefaultExisted,string? DefaultValue,RegistryValueKind Kind,int OtherValueCount=0,int SubKeyCount=0);
    internal sealed record RegistryRecoveryPlan(bool Change=false,bool RemoveDefault=false,bool RemoveEmptyKey=false,string? Value=null,
        RegistryValueKind Kind=RegistryValueKind.None,bool OtherFieldsChanged=false,string? Conflict=null);
    internal sealed class RecoveryStatus
    {
        public bool BackupPrepared {get;internal set;}
        public bool Launched {get;internal set;}
        public bool TreeStopped {get;internal set;}
        public int RestoredManifestPaths {get;internal set;}
        public int RemovedTestIndexEntries {get;internal set;}
        public int RestoredRegistryValues {get;internal set;}
        public int OtherFieldChangesDetected {get;internal set;}
        public bool VerificationPassed {get;internal set;}
        public List<string> Conflicts {get;}=new();
    }
    private readonly string testHome;
    private readonly string testRoot;
    private readonly List<FileBefore> manifests=new();
    private readonly List<RegistryBefore> registry=new();
    private readonly FileBefore index;
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<string,bool> seenOwnedRegistry=new();
    private Task? observing;
    private volatile bool observationFailed;
    internal RecoveryStatus Status {get;}=new();

    private NativeBrowserRegistrationGuard(string home,string root,string backupDirectory)
    {
        testHome=Path.GetFullPath(home);testRoot=Path.GetFullPath(root);
        if(!Within(testHome,testRoot)||Directory.Exists(backupDirectory))throw new IOException("Invalid diagnostic guard directories.");
        Directory.CreateDirectory(backupDirectory);FilePrivacy.ApplyDirectory(backupDirectory);
        var user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
        foreach(var host in HostNames)
        {
            var path=Path.Combine(user,"AppData","Local","OpenAI","extension",host+".json");
            var before=CaptureFile(path);manifests.Add(before);
            Backup(Path.Combine(backupDirectory,host+".manifest.dpapi"),before.Bytes);
            if(before.Bytes is not null)RequireObject(before.Bytes);
            using var key=hive.OpenSubKey(RegistryParent+"\\"+host);
            var hasDefault=key?.GetValueNames().Contains("")==true;
            var kind=hasDefault?key!.GetValueKind(""):RegistryValueKind.None;
            if(hasDefault&&kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))throw new IOException("Unexpected native-host registry value type.");
            var entry=new RegistryBefore(host,key is not null,hasDefault,hasDefault?(string?)key!.GetValue("",null,RegistryValueOptions.DoNotExpandEnvironmentNames):null,kind);
            registry.Add(entry);
            Backup(Path.Combine(backupDirectory,host+".registry.dpapi"),JsonSerializer.SerializeToUtf8Bytes(entry));
        }
        index=CaptureFile(Path.Combine(local,"OpenAI","Codex","chrome-native-hosts-v2.json"));
        Backup(Path.Combine(backupDirectory,"native-host-index.dpapi"),index.Bytes);
        if(index.Bytes is not null)
        {
            var original=RequireObject(index.Bytes);
            if(original["entries"] is not JsonArray entries||entries.Any(IsTestEntry))throw new IOException("Unexpected native-host index precondition.");
        }
        Status.BackupPrepared=true;
    }

    internal static NativeBrowserRegistrationGuard Capture(string home,string root,string backupDirectory)=>new(home,root,backupDirectory);
    internal void MarkLaunched()
    {
        if(Status.Launched)throw new InvalidOperationException();
        Status.Launched=true;
        ObserveOwnedRegistry();
        observing=Task.Run(async()=>
        {
            while(!stop.IsCancellationRequested)
            {
                try{ObserveOwnedRegistry();await Task.Delay(100,stop.Token).ConfigureAwait(false);}
                catch(OperationCanceledException){break;}
                catch{observationFailed=true;break;}
            }
        });
    }

    private void ObserveOwnedRegistry()
    {
        using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
        foreach(var item in registry)
        {
            using var key=hive.OpenSubKey(RegistryParent+"\\"+item.Host);
            if(key?.GetValue("",null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string path&&Within(path,testRoot))seenOwnedRegistry[item.Host]=true;
        }
    }

    internal async Task RestoreAsync(bool diagnosticTreeStopped)
    {
        stop.Cancel();if(observing is not null)try{await observing.ConfigureAwait(false);}catch{}
        Status.TreeStopped=diagnosticTreeStopped;
        if(!Status.Launched){Status.VerificationPassed=true;return;}
        if(!diagnosticTreeStopped){Status.Conflicts.Add("diagnostic-tree-still-active");return;}
        if(observationFailed)Status.Conflicts.Add("native-host-registry-observation-failed");
        foreach(var before in manifests)
        {
            try{RestoreManifest(before);}
            catch{Status.Conflicts.Add("native-host-manifest-recovery");}
        }
        try{RemoveOwnIndexEntries();}catch{Status.Conflicts.Add("native-host-index-recovery");}
        foreach(var before in registry)
        {
            try{RestoreRegistry(before);}
            catch{Status.Conflicts.Add("native-host-registry-recovery");}
        }
        try
        {
            foreach(var before in manifests)
            {
                var bytes=ReadOptional(before.Path);
                if(bytes is not null&&Within(RequireObject(bytes)["path"]?.GetValue<string>(),testRoot))throw new IOException();
            }
            var afterIndex=ReadOptional(index.Path);
            if(afterIndex is not null&&RequireObject(afterIndex)["entries"] is JsonArray entries&&entries.Any(IsTestEntry))throw new IOException();
            using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
            foreach(var item in registry)
            {
                using var key=hive.OpenSubKey(RegistryParent+"\\"+item.Host);
                if(key?.GetValue("",null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string path&&Within(path,testRoot))throw new IOException();
            }
        }
        catch{Status.Conflicts.Add("native-host-recovery-verification");}
        Status.VerificationPassed=Status.Conflicts.Count==0;
    }

    private void RestoreManifest(FileBefore before)
    {
        var current=ReadOptional(before.Path);
        var plan=PlanManifest(before.Bytes,current,testRoot,Status.TreeStopped);
        if(plan.OtherFieldsChanged)Status.OtherFieldChangesDetected++;
        ApplyFilePlan(before.Path,current,plan);
        if(plan.Delete||plan.Replacement is not null)Status.RestoredManifestPaths++;
    }

    private void RemoveOwnIndexEntries()
    {
        var current=ReadOptional(index.Path);
        var plan=PlanIndex(index.Bytes,current,testHome,Status.TreeStopped);
        ApplyFilePlan(index.Path,current,plan);
        Status.RemovedTestIndexEntries+=plan.RemovedEntries;
    }

    private void RestoreRegistry(RegistryBefore before)
    {
        using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
        var path=RegistryParent+"\\"+before.Host;
        using var key=hive.OpenSubKey(path,true);
        var hasDefault=key?.GetValueNames().Contains("")==true;
        var current=hasDefault?key!.GetValue("",null,RegistryValueOptions.DoNotExpandEnvironmentNames):null;
        var currentKind=hasDefault?key!.GetValueKind(""):RegistryValueKind.None;
        var plan=PlanRegistry(new(before.KeyExisted,before.DefaultExisted,before.DefaultValue,before.Kind),
            new(key is not null,hasDefault,current as string,currentKind,
                key?.GetValueNames().Count(name=>name!="")??0,key?.GetSubKeyNames().Length??0),
            seenOwnedRegistry.ContainsKey(before.Host),testRoot,Status.TreeStopped);
        if(plan.OtherFieldsChanged)Status.OtherFieldChangesDetected++;
        if(plan.Conflict is not null)throw new IOException(plan.Conflict);
        if(!plan.Change)return;
        if(!plan.RemoveDefault)
        {
            using var writable=key is null?hive.CreateSubKey(path):null;
            (key??writable!).SetValue("",plan.Value!,plan.Kind);
        }
        else if(key is not null)
        {
            key.DeleteValue("",false);
            if(plan.RemoveEmptyKey&&key.GetValueNames().Length==0&&key.GetSubKeyNames().Length==0)
            {
                // Exact empty leaf only, never a recursive registry deletion.
                hive.DeleteSubKey(path,false);
            }
        }
        Status.RestoredRegistryValues++;
    }

    internal static FileRecoveryPlan PlanManifest(byte[]? before,byte[]? current,string root,bool callerCleanup)
    {
        if(!callerCleanup)return new(Conflict:"diagnostic-tree-still-active");
        if(current is null)return before is null?new():new(Conflict:"manifest-disappeared-without-owned-path-evidence");
        var value=RequireObject(current);
        if(!Within(value["path"]?.GetValue<string>(),root))return new(OtherFieldsChanged:before is not null&&!current.AsSpan().SequenceEqual(before));
        if(before is null)
        {
            var allowed=new HashSet<string>{"allowed_origins","description","name","path","type"};
            if(value.Any(pair=>!allowed.Contains(pair.Key))||value["type"]?.GetValue<string>()!="stdio"||!HostNames.Contains(value["name"]?.GetValue<string>()))
                return new(Conflict:"new-manifest-has-unattributed-fields");
            return new(Delete:true);
        }
        var original=RequireObject(before);var oldPath=original["path"]?.GetValue<string>();
        if(string.IsNullOrEmpty(oldPath)||!Path.IsPathFullyQualified(oldPath)||Within(oldPath,root))return new(Conflict:"original-manifest-path-invalid");
        var otherCurrent=(JsonObject)value.DeepClone();var otherBefore=(JsonObject)original.DeepClone();
        otherCurrent.Remove("path");otherBefore.Remove("path");
        var changed=!JsonNode.DeepEquals(otherCurrent,otherBefore);
        value["path"]=oldPath;
        return new(Serialize(value),OtherFieldsChanged:changed);
    }

    internal static FileRecoveryPlan PlanIndex(byte[]? before,byte[]? current,string home,bool callerCleanup)
    {
        if(!callerCleanup)return new(Conflict:"diagnostic-tree-still-active");
        if(current is null)return before is null?new():new(Conflict:"native-host-index-disappeared");
        var value=RequireObject(current);
        if(value["entries"] is not JsonArray entries)return new(Conflict:"native-host-index-schema-invalid");
        var count=0;
        for(var i=entries.Count-1;i>=0;i--)if(IsTestEntry(entries[i],home)){entries.RemoveAt(i);count++;}
        return count==0?new():new(Serialize(value),RemovedEntries:count);
    }

    internal static RegistryRecoveryPlan PlanRegistry(RegistryImage before,RegistryImage current,bool observedOwned,string root,bool callerCleanup)
    {
        if(!callerCleanup)return new(Conflict:"diagnostic-tree-still-active");
        if(current.DefaultExisted==before.DefaultExisted&&current.DefaultValue==before.DefaultValue&&current.Kind==before.Kind)return new();
        var owned=current.DefaultExisted&&(current.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString)&&Within(current.DefaultValue,root);
        if(!owned&&!(observedOwned&&!current.DefaultExisted))
            return new(OtherFieldsChanged:true,Conflict:!current.DefaultExisted&&before.DefaultExisted?"registry-disappeared-without-owned-path-evidence":null);
        if(before.DefaultExisted)return new(Change:true,Value:before.DefaultValue,Kind:before.Kind);
        return new(Change:current.DefaultExisted,RemoveDefault:true,
            RemoveEmptyKey:!before.KeyExisted&&current.OtherValueCount==0&&current.SubKeyCount==0);
    }

    internal static void ApplyFilePlan(string path,byte[]? expected,FileRecoveryPlan plan)
    {
        if(plan.Conflict is not null)throw new IOException(plan.Conflict);
        if(!plan.Delete&&plan.Replacement is null)return;
        if(expected is null)throw new IOException("Recovery has no current file image.");
        if(plan.Delete)DeleteUnchanged(path,expected);else ReplaceUnchanged(path,expected,plan.Replacement!);
    }
    private static byte[] Serialize(JsonObject value)=>JsonSerializer.SerializeToUtf8Bytes(value,new JsonSerializerOptions{WriteIndented=true});
    private bool IsTestEntry(JsonNode? node)=>IsTestEntry(node,testHome);
    private static bool IsTestEntry(JsonNode? node,string home)
    {
        if(node is not JsonObject entry||entry["paths"] is not JsonObject paths||paths["codexHome"] is not JsonValue raw||!raw.TryGetValue<string>(out var path))return false;
        return path is not null&&Path.IsPathFullyQualified(path)&&Path.GetFullPath(path).TrimEnd('\\').Equals(Path.GetFullPath(home).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
    }
    private static bool Within(string? path,string root)
    {
        if(string.IsNullOrEmpty(path)||!Path.IsPathFullyQualified(path))return false;
        var full=Path.GetFullPath(path).TrimEnd('\\');var basis=Path.GetFullPath(root).TrimEnd('\\');
        return full.Equals(basis,StringComparison.OrdinalIgnoreCase)||full.StartsWith(basis+"\\",StringComparison.OrdinalIgnoreCase);
    }
    private static FileBefore CaptureFile(string path)=>new(path,ReadOptional(path));
    private static byte[]? ReadOptional(string path)
    {
        var info=new FileInfo(path);
        for(var directory=info.Directory;directory is not null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unexpected registration directory link.");
        if(!info.Exists)return null;
        if(info.Length>8*1024*1024||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unexpected registration file.");
        return File.ReadAllBytes(path);
    }
    private static JsonObject RequireObject(byte[] bytes)=>JsonNode.Parse(bytes) as JsonObject??throw new IOException("Unexpected native registration data.");
    private static void Backup(string path,byte[]? bytes)
    {
        var envelope=JsonSerializer.SerializeToUtf8Bytes(new{existed=bytes is not null,bytes});
        try{File.WriteAllBytes(path,SecretProtection.Protect(envelope));FilePrivacy.ApplyFile(path);}
        finally{CryptographicOperations.ZeroMemory(envelope);}
    }
    private static void ReplaceUnchanged(string path,byte[] expected,byte[] replacement)
    {
        var temporary=path+".codex-accounts-recovery-"+Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary,replacement);
            FilePrivacy.ApplyFile(temporary);
            if(!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))throw new IOException("Registration changed concurrently.");
            File.Replace(temporary,path,null);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static void DeleteUnchanged(string path,byte[] expected)
    {
        if(!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))throw new IOException("Registration changed concurrently.");
        File.Delete(path);
    }
}
