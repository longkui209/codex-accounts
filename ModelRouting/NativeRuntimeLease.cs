using System.Security.Cryptography;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>A local byte-identical native runtime. No vendor file is included in the plugin package.</summary>
internal sealed class NativeRuntimeLease:IDisposable
{
    internal sealed record Manifest(string SourceExecutable,string RuntimeExecutable,string SourceHash,string ComponentVersion);
    private readonly List<FileStream> locks=new();
    internal string Executable {get;}
    internal string BundledCli=>Path.Combine(Path.GetDirectoryName(Executable)!,"resources","codex.exe");
    internal Manifest Metadata {get;}
    private NativeRuntimeLease(Manifest metadata){Metadata=metadata;Executable=metadata.RuntimeExecutable;}
    internal static NativeRuntimeLease Open(string componentDirectory,bool allUsers=false)
    {
        Plain(componentDirectory);
        var manifestFile=Path.Combine(componentDirectory,"integration.json");Plain(manifestFile);
        if(new FileInfo(manifestFile).Length is <=0 or >8192)throw Refused();
        var metadata=JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestFile))??throw Refused();
        var installed=allUsers?DesktopLaunch.FindInstalledDesktops(true):new[]{DesktopLaunch.FindInstalledDesktop()};
        var source=installed.SingleOrDefault(path=>Same(metadata.SourceExecutable,path))??throw Refused();
        var runtime=Path.Combine(componentDirectory,"native","app","ChatGPT.exe");
        if(!Same(metadata.SourceExecutable,source)||!Same(metadata.RuntimeExecutable,runtime)||
            !string.Equals(metadata.SourceHash,Hash(source),StringComparison.OrdinalIgnoreCase))throw Refused();
        var result=new NativeRuntimeLease(metadata);
        try
        {
            var original=Tree(Path.GetDirectoryName(source)!);
            var copy=Tree(Path.GetDirectoryName(runtime)!);
            if(!original.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(copy.Keys.Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))throw Refused();
            foreach(var pair in original.OrderBy(pair=>pair.Key,StringComparer.OrdinalIgnoreCase))
            {
                using var input=new FileStream(pair.Value,FileMode.Open,FileAccess.Read,FileShare.Read);
                var output=new FileStream(copy[pair.Key],FileMode.Open,FileAccess.Read,FileShare.Read);
                result.locks.Add(output);
                if(input.Length!=output.Length||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(input),SHA256.HashData(output)))throw Refused();
            }
            foreach(var name in new[]{"ChatGPT.exe","chrome.dll","chrome_elf.dll"})DesktopLaunch.ValidateSignedRuntimeFile(Path.Combine(Path.GetDirectoryName(runtime)!,name));
            if(!(allUsers?DesktopLaunch.FindInstalledDesktops(true):new[]{DesktopLaunch.FindInstalledDesktop()}).Any(path=>Same(path,source)))throw Refused();
            return result;
        }
        catch{result.Dispose();throw;}
    }
    internal static Manifest Create(string componentDirectory,string componentVersion,string? sourceExecutable=null,CancellationToken cancellation=default)
    {
        Plain(componentDirectory);
        var source=sourceExecutable??DesktopLaunch.FindInstalledDesktop();
        if(sourceExecutable is not null&&!DesktopLaunch.FindInstalledDesktops(true).Any(path=>Same(path,source)))throw Refused();
        var originalDirectory=Path.GetDirectoryName(source)!;
        var runtimeDirectory=Path.Combine(componentDirectory,"native","app");
        if(Directory.Exists(Path.Combine(componentDirectory,"native")))throw Refused();
        Directory.CreateDirectory(runtimeDirectory);
        foreach(var pair in Tree(originalDirectory).OrderBy(pair=>pair.Key,StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            var destination=Path.Combine(runtimeDirectory,pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input=new FileStream(pair.Value,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            input.CopyTo(output);output.Flush(true);
        }
        var metadata=new Manifest(source,Path.Combine(runtimeDirectory,"ChatGPT.exe"),Hash(source),componentVersion);
        File.WriteAllText(Path.Combine(componentDirectory,"integration.json"),JsonSerializer.Serialize(metadata));
        using var verified=Open(componentDirectory,sourceExecutable is not null);
        return metadata;
    }
    internal static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    internal static void Plain(string path)
    {
        for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw Refused();
    }
    private static Dictionary<string,string> Tree(string root)
    {
        Plain(root);
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))foreach(var item in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(item);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw Refused();
            if((attributes&FileAttributes.Directory)!=0)pending.Push(item);
            else
            {
                var relative=Path.GetRelativePath(root,item);
                // Chromium can write an early non-executable diagnostic beside its executable.
                if(relative.Equals("debug.log",StringComparison.OrdinalIgnoreCase))continue;
                if(!files.TryAdd(relative,item))throw Refused();
            }
        }
        return files;
    }
    private static bool Same(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    private static UserFacingException Refused()=>new("本地原生运行文件与当前官方安装不一致；没有启用普通启动接入。");
    public void Dispose(){foreach(var file in locks)file.Dispose();locks.Clear();}
}
