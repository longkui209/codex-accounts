using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.ModelRouting;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Validation;

/// <summary>Explicit diagnostic-only directories and child environment. Does not edit user settings.</summary>
internal sealed class IsolatedDesktopEnvironment : IDisposable
{
    internal const string WslMarker = "codex-accounts-isolated-wsl-unavailable-v1";
    private const string ReviewedWslStubSha256 = "d27bbd0626f33e9a4bb2fbd3ca5d22112d46d1031bb7ef9518edfec95013e09a";
    internal const string RegistryMarker="codex-accounts-isolated-registry-noop-v1";
    private const string ReviewedRegistryStubSha256="3ae84bad187caea7befb56aceb87e916e3ef88b6aa6afa8e3099dbf2f580568a";
    private static readonly object DiagnosticLaunchLock = new();
    private readonly List<FileStream> locks = new();
    private readonly CancellationTokenSource networkStop = new();
    private TcpListener? denyListener;
    private Task? denyLoop;
    private int deniedRequests,modelsRequests;
    internal int DeniedRequestCount=>Volatile.Read(ref deniedRequests);
    internal int EmptyModelListCount=>Volatile.Read(ref modelsRequests);
    internal string Output { get; }
    internal string Root { get; }
    internal string Home { get; }
    internal string Sqlite { get; }
    internal string Profile { get; }
    internal string WorkingDirectory { get; }
    internal string ProjectRoot { get; }
    internal bool WslUnavailableVerified { get; private set; }
    internal bool WslBareNameResolved { get; private set; }
    internal bool RegistryBareNameResolved {get;private set;}
    internal bool RegistryNoOpVerified {get;private set;}
    internal int RegistrySyntheticCheckCount {get;private set;}
    private readonly Dictionary<string,string> environment = new(StringComparer.OrdinalIgnoreCase);

    internal IsolatedDesktopEnvironment(string output)
    {
        if (!Path.IsPathFullyQualified(output)) throw new UserFacingException("诊断输出必须是新的绝对路径。");
        Output = Path.GetFullPath(output);
        var validation = Directory.GetParent(Output);
        if (validation is null || validation.Name != "validation-output" || validation.Parent is null ||
            !File.Exists(Path.Combine(validation.Parent.FullName,"CodexAccountSwitcher.csproj")) ||
            Directory.Exists(Output) || File.Exists(Output)) throw new UserFacingException("诊断输出必须是项目 validation-output 中尚不存在的目录。");
        ProjectRoot = validation.Parent.FullName;
        for (var current = validation; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("诊断目录不能经过链接。");
        Create(Output);
        Root = Create(Path.Combine(Output,"p"));
        Home = Create(Path.Combine(Root,"home"));
        Sqlite = Create(Path.Combine(Root,"sqlite"));
        Profile = Create(Path.Combine(Root,"electron-profile"));
        WorkingDirectory = Create(Path.Combine(Root,"bin"));
        var userProfile = Create(Path.Combine(Root,"user-profile"));
        var local = Create(Path.Combine(userProfile,"AppData","Local"));
        var roaming = Create(Path.Combine(userProfile,"AppData","Roaming"));
        var temporary = Create(Path.Combine(Root,"temp"));
        File.WriteAllText(Path.Combine(Home,"config.toml"),NativeDiagnosticChildImage.EmptyHomeConfiguration,new UTF8Encoding(false));
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Path.IsPathFullyQualified(system) || !Path.IsPathFullyQualified(windows)) throw new UserFacingException("无法确定 Windows 系统目录。");
        environment["SystemRoot"] = windows;
        environment["WINDIR"] = windows;
        environment["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\');
        environment["COMSPEC"] = Path.Combine(system,"cmd.exe");
        environment["OS"] = "Windows_NT";
        environment["PROCESSOR_ARCHITECTURE"] = "AMD64";
        environment["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        environment["PATH"] = string.Join(';',WorkingDirectory,system,windows,Path.Combine(system,"Wbem"),Path.Combine(system,"WindowsPowerShell","v1.0"));
        environment["USERPROFILE"] = userProfile;
        environment["HOME"] = userProfile;
        environment["HOMEDRIVE"] = Path.GetPathRoot(userProfile)!.TrimEnd('\\');
        environment["HOMEPATH"] = userProfile[environment["HOMEDRIVE"].Length..];
        environment["LOCALAPPDATA"] = local;
        environment["APPDATA"] = roaming;
        environment["TEMP"] = temporary;
        environment["TMP"] = temporary;
        environment["CODEX_HOME"] = Home;
        environment["CODEX_SQLITE_HOME"] = Sqlite;
        environment["CODEX_ELECTRON_USER_DATA_PATH"] = Profile;
        environment["CODEX_ELECTRON_START_IN_BACKGROUND"] = "1";
        environment["CODEX_SPARKLE_ENABLED"] = "false";
        environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        denyListener=new TcpListener(IPAddress.Loopback,0);
        denyListener.Start(8);
        var endpoint="http://127.0.0.1:"+((IPEndPoint)denyListener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["CODEX_APP_SERVER_OPENAI_BASE_URL"]=endpoint+"/backend-api/codex";
        environment["HTTP_PROXY"]=endpoint;
        environment["HTTPS_PROXY"]=endpoint;
        environment["ALL_PROXY"]=endpoint;
        denyLoop=RunDenyListenerAsync();
    }

    private static string Create(string path)
    {
        Directory.CreateDirectory(path);
        FilePrivacy.ApplyDirectory(path);
        return path;
    }

    internal ProcessStartInfo BuildStartInfo(string executable)
    {
        var start = new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=WorkingDirectory};
        start.Environment.Clear();
        foreach (var entry in environment) start.Environment.Add(entry.Key,entry.Value);
        return start;
    }

    internal async Task VerifyUnavailableWslAsync(string installedDesktop, CancellationToken cancellation)
    {
        // The fixed product directory is searched before cwd by Windows; refuse a conflicting image.
        if (File.Exists(Path.Combine(Path.GetDirectoryName(installedDesktop)!,"wsl.exe")) ||
            File.Exists(Path.Combine(AppContext.BaseDirectory,"wsl.exe")))
            throw new UserFacingException("启动目录存在其他 WSL 程序，诊断已停止。");
        var source = Path.Combine(AppContext.BaseDirectory,"WslUnavailableStub.exe");
        var destination = Path.Combine(WorkingDirectory,"wsl.exe");
        var info = new FileInfo(source);
        if (!info.Exists || info.Length is < 512 or > 2*1024*1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UserFacingException("请先构建项目内的 WSL 不可用诊断桩；未启动真实桌面。");
        var sourceLock = File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        locks.Add(sourceLock);
        var expected = SHA256.HashData(sourceLock);sourceLock.Position=0;
        if(!Convert.ToHexString(expected).Equals(ReviewedWslStubSha256,StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("WSL 诊断桩与本次审阅构建不一致；没有执行任何 WSL 程序。");
        using (var target = File.Open(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None)) sourceLock.CopyTo(target);
        FilePrivacy.ApplyFile(destination);
        var destinationLock = File.Open(destination,FileMode.Open,FileAccess.Read,FileShare.Read);
        locks.Add(destinationLock);
        if (!CryptographicOperations.FixedTimeEquals(expected,SHA256.HashData(destinationLock))) throw new IOException("Diagnostic WSL copy mismatch.");
        destinationLock.Position=0;
        lock (DiagnosticLaunchLock)
        {
            var previous=Environment.CurrentDirectory;
            try
            {
                Directory.SetCurrentDirectory(WorkingDirectory);
                var resolved=new StringBuilder(32768);
                var size=SearchPath(null,"wsl.exe",null,(uint)resolved.Capacity,resolved,IntPtr.Zero);
                if(size==0||size>=resolved.Capacity||!Path.GetFullPath(resolved.ToString()).Equals(destination,StringComparison.OrdinalIgnoreCase))
                    throw new UserFacingException("裸 WSL 名称未解析到本次诊断桩；没有执行系统 WSL 或启动桌面。");
                WslBareNameResolved=true;
            }
            finally{Directory.SetCurrentDirectory(previous);}
        }
        // Execute only the already verified absolute fixture path. The report records
        // name resolution and fixture execution separately, not a bare-spawn experiment.
        var start = BuildStartInfo(destination);
        start.ArgumentList.Add("--status");start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        using var process = Process.Start(start) ?? throw new IOException("Diagnostic WSL process unavailable.");
        try
        {
            var path = new StringBuilder(32768);var length=(uint)path.Capacity;
            if (!QueryFullProcessImageName(process.SafeHandle,0,path,ref length) ||
                !Path.GetFullPath(path.ToString()).Equals(destination,StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException("诊断未命中本次 WSL 桩，未启动真实桌面。");
            var stdout=process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr=process.StandardError.ReadToEndAsync(cancellation);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode!=1 || (await stdout.ConfigureAwait(false)).Trim()!=WslMarker || !string.IsNullOrEmpty(await stderr.ConfigureAwait(false)))
                throw new UserFacingException("WSL 隔离验证未通过，未启动真实桌面。");
            WslUnavailableVerified=true;
        }
        finally
        {
            if (!process.HasExited) {process.Kill();await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
        }
    }

    internal async Task VerifyRegistryNoOpAsync(string installedDesktop,CancellationToken cancellation)
    {
        foreach(var directory in new[]{Path.GetDirectoryName(installedDesktop)!,AppContext.BaseDirectory})
            foreach(var name in new[]{"reg.exe","reg.com","reg.cmd","reg.bat"})
                if(File.Exists(Path.Combine(directory,name)))throw new UserFacingException("启动目录有其他注册表命令，诊断已停止。");
        var source=Path.Combine(AppContext.BaseDirectory,"RegistryNoOpStub.exe");
        var destination=Path.Combine(WorkingDirectory,"reg.exe");
        var info=new FileInfo(source);
        if(!info.Exists||info.Length is <512 or >2*1024*1024||(info.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new UserFacingException("缺少本次注册表隔离诊断桩，未启动桌面。");
        var sourceLock=File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read);locks.Add(sourceLock);
        var digest=SHA256.HashData(sourceLock);sourceLock.Position=0;
        if(!Convert.ToHexString(digest).Equals(ReviewedRegistryStubSha256,StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("注册表诊断桩与审阅构建不一致，未执行注册表命令。");
        using(var target=File.Open(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))sourceLock.CopyTo(target);
        FilePrivacy.ApplyFile(destination);
        var copyLock=File.Open(destination,FileMode.Open,FileAccess.Read,FileShare.Read);locks.Add(copyLock);
        if(!CryptographicOperations.FixedTimeEquals(digest,SHA256.HashData(copyLock)))throw new IOException("Registry fixture copy mismatch.");
        copyLock.Position=0;
        lock(DiagnosticLaunchLock)
        {
            var previous=Environment.CurrentDirectory;
            try
            {
                Directory.SetCurrentDirectory(WorkingDirectory);
                foreach(var name in new[]{"reg","reg.exe"})
                {
                    var resolved=new StringBuilder(32768);
                    var size=SearchPath(null,name,name=="reg"?".exe":null,(uint)resolved.Capacity,resolved,IntPtr.Zero);
                    if(size==0||size>=resolved.Capacity||!Path.GetFullPath(resolved.ToString()).Equals(destination,StringComparison.OrdinalIgnoreCase))
                        throw new UserFacingException("注册表命令未解析到本次隔离桩；没有执行系统 reg 或启动桌面。");
                }
                RegistryBareNameResolved=true;
            }
            finally{Directory.SetCurrentDirectory(previous);}
        }
        await RunRegistryFixtureAsync(destination,new[]{"--codex-isolated-registry-probe"},0,RegistryMarker,true,cancellation).ConfigureAwait(false);
        const string prefix=@"HKCU\Software\Google\Chrome\NativeMessagingHosts\";
        var hosts=new[]{"com.openai.codexextension","com.openai.codexextension.dev","com.openai.codexextension.internal"};
        string[] Add(string key,string value,string type="REG_SZ")=>new[]{"add",key,"/ve","/t",type,"/d",value,"/f"};
        foreach(var host in hosts)
        {
            var value=Path.Combine(environment["USERPROFILE"],"AppData","Local","OpenAI","extension",host+".json");
            await RunRegistryFixtureAsync(destination,Add(prefix+host,value),0,"",false,cancellation).ConfigureAwait(false);
        }
        var key=prefix+hosts[0];
        var allowed=Path.Combine(environment["USERPROFILE"],"AppData","Local","OpenAI","extension",hosts[0]+".json");
        var rejected=new[]{
            new[]{"query",key},new[]{"delete",key,"/f"},
            Add(key,Path.Combine(Root,"outside-user-profile.json")),
            Add(key,Path.Combine(environment["USERPROFILE"]+"-other","manifest.json")),
            Add(key,allowed,"REG_EXPAND_SZ"),Add(prefix+"not-our-host",allowed)
        };
        foreach(var arguments in rejected)
            await RunRegistryFixtureAsync(destination,arguments,1,"",false,cancellation).ConfigureAwait(false);
        RegistryNoOpVerified=true;
    }

    private async Task RunRegistryFixtureAsync(string executable,IReadOnlyList<string> arguments,int expectedExit,string expectedOutput,
        bool verifyProcessPath,CancellationToken cancellation)
    {
        var start=BuildStartInfo(executable);start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Registry fixture process unavailable.");
        try
        {
            if(verifyProcessPath)
            {
                var actual=new StringBuilder(32768);var length=(uint)actual.Capacity;
                if(!QueryFullProcessImageName(process.SafeHandle,0,actual,ref length)||
                    !Path.GetFullPath(actual.ToString()).Equals(executable,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Registry fixture process path mismatch.");
            }
            var stdout=process.StandardOutput.ReadToEndAsync(cancellation);var stderr=process.StandardError.ReadToEndAsync(cancellation);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if(process.ExitCode!=expectedExit||(await stdout.ConfigureAwait(false)).Trim()!=expectedOutput||
                !string.IsNullOrEmpty(await stderr.ConfigureAwait(false)))throw new UserFacingException("注册表隔离桩的允许或拒绝规则未通过；未启动桌面。");
            RegistrySyntheticCheckCount++;
        }
        finally
        {
            if(!process.HasExited){process.Kill();await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
        }
    }

    private async Task RunDenyListenerAsync()
    {
        while(!networkStop.IsCancellationRequested)
        {
            TcpClient client;
            try{client=await denyListener!.AcceptTcpClientAsync(networkStop.Token).ConfigureAwait(false);}
            catch(OperationCanceledException){break;}
            catch(SocketException)when(networkStop.IsCancellationRequested){break;}
            using(client)
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(networkStop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var stream=client.GetStream();var single=new byte[1];var line=new StringBuilder();
                    while(line.Length<2048)
                    {
                        if(await stream.ReadAsync(single,timeout.Token).ConfigureAwait(false)!=1)break;
                        if(single[0]=='\n')break;
                        if(single[0]!='\r')line.Append((char)single[0]);
                    }
                    // Read only the request line. Never retain headers, body, or proxy credentials.
                    var parts=line.ToString().Split(' ',StringSplitOptions.RemoveEmptyEntries);
                    var models=parts.Length==3&&parts[0]=="GET"&&parts[1].Split('?')[0]=="/backend-api/codex/models";
                    if(models)Interlocked.Increment(ref modelsRequests);else Interlocked.Increment(ref deniedRequests);
                    var body=models?"{\"models\":[]}":"{\"error\":\"isolated_diagnostic_network_denied\"}";
                    var reply=Encoding.ASCII.GetBytes("HTTP/1.1 "+(models?"200 OK":"403 Forbidden")+"\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\n\r\n"+body);
                    await stream.WriteAsync(reply,timeout.Token).ConfigureAwait(false);
                }
                catch(Exception error)when(error is IOException or OperationCanceledException or SocketException){}
            }
        }
    }
    public void Dispose()
    {
        networkStop.Cancel();denyListener?.Stop();
        if(denyLoop is not null)try{denyLoop.Wait(TimeSpan.FromSeconds(2));}catch{}
        networkStop.Dispose();foreach(var item in locks)item.Dispose();locks.Clear();
    }
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll",EntryPoint="SearchPathW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint SearchPath(string? path,string file,string? extension,uint bufferLength,StringBuilder buffer,IntPtr filePart);
}
