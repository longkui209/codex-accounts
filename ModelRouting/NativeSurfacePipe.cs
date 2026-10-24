using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.Validation;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Private inherited Chromium protocol pipes. No TCP listener, credentials or native private IPC.</summary>
internal sealed class NativeSurfacePipe:IAsyncDisposable
{
    private readonly AnonymousPipeServerStream input=new(PipeDirection.Out,HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream output=new(PipeDirection.In,HandleInheritability.Inheritable);
    private readonly SemaphoreSlim sending=new(1,1);
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> pending=new();
    private readonly Channel<JsonElement> events=Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions{SingleReader=true,SingleWriter=true});
    private Task? reading;
    private int next;
    private NativeDiagnosticJob? diagnosticJob;
    private NativeDiagnosticChildImage? diagnosticImage;
    private NativeDiagnosticChildImage? diagnosticMainImage;
    private bool diagnosticDebugDetach;
    internal object? DiagnosticDebugDetachStatus {get;private set;}
    internal Process? Desktop {get;private set;}
    internal string? ReadTermination {get;private set;}
    internal ChannelReader<JsonElement> Events=>events.Reader;
    internal IReadOnlyList<int> DiagnosticProcessIds=>diagnosticJob?.ProcessIds??Array.Empty<int>();
    internal bool? DiagnosticCleanupConfirmed=>diagnosticJob?.CleanupConfirmed;
    internal int? DiagnosticCleanupErrorCode=>diagnosticJob?.CleanupErrorCode;
    internal int? DiagnosticLaunchErrorCode {get;private set;}
    internal bool CloseDiagnosticJob()=>diagnosticJob?.Close()??false;
    internal bool IsOwnedDiagnosticProcessHandle(IntPtr process)
    {
        if(diagnosticJob is null||process==IntPtr.Zero||process==new IntPtr(-1))return false;
        try{diagnosticJob.VerifyMembership(process);return true;}catch{return false;}
    }
    internal void Start(ProcessStartInfo start,string? browserProfile=null,bool isolatedDiagnostics=false)
        =>StartCore(start,browserProfile,isolatedDiagnostics,null);
    internal void StartWithRuntime(ProcessStartInfo start,string profile,NativeRuntimeLease runtime,IReadOnlyList<string> originalArguments)
    {
        if(!string.Equals(Path.GetFullPath(start.FileName),runtime.Executable,StringComparison.OrdinalIgnoreCase))throw new IOException("Unverified integrated runtime.");
        StartCore(start,profile,false,null,originalArguments);
    }
    internal void StartIsolatedDiagnostic(ProcessStartInfo start,string browserProfile,NativeDiagnosticChildImage validatedChildImage,bool debugDetach=false,NativeDiagnosticChildImage? validatedMainAlias=null,bool useCopiedMain=false)
    {
        if(Desktop is not null||diagnosticJob is not null)throw new InvalidOperationException("Diagnostic launch already initialized.");
        ArgumentNullException.ThrowIfNull(validatedChildImage);
        if(useCopiedMain&&(!validatedChildImage.UsesCopiedChildImage||debugDetach||validatedMainAlias is not null))
            throw new InvalidOperationException("Copied-main diagnostic requires the fully verified copied image and no alternate launch mode.");
        if(debugDetach&&!validatedChildImage.UsesLinkedChildImage)throw new InvalidOperationException("Debug-detach control requires the verified linked original image.");
        if(validatedMainAlias is not null&&(!debugDetach||!validatedMainAlias.UsesLinkedChildImage||
            !string.Equals(validatedMainAlias.SourceExecutable,validatedChildImage.SourceExecutable,StringComparison.OrdinalIgnoreCase)||
            !string.Equals(validatedMainAlias.IsolatedRoot,validatedChildImage.IsolatedRoot,StringComparison.OrdinalIgnoreCase)||
            string.Equals(validatedMainAlias.ChildExecutable,validatedChildImage.ChildExecutable,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Diagnostic main alias does not match the verified original and isolated root.");
        validatedChildImage.ClaimForStart(start,browserProfile);
        validatedMainAlias?.ClaimForStart(start,browserProfile);
        diagnosticImage=validatedChildImage;
        diagnosticMainImage=validatedMainAlias;
        diagnosticDebugDetach=debugDetach;
        if(validatedMainAlias is not null)start.FileName=validatedMainAlias.ChildExecutable;
        if(useCopiedMain)start.FileName=validatedChildImage.ChildExecutable;
        try
        {
            diagnosticJob=new NativeDiagnosticJob();
            StartCore(start,browserProfile,true,validatedChildImage.ChildExecutable);
        }
        catch(Win32Exception error){DiagnosticLaunchErrorCode=error.NativeErrorCode;CloseDiagnosticJob();diagnosticImage.Dispose();diagnosticMainImage?.Dispose();throw;}
        catch{CloseDiagnosticJob();diagnosticImage.Dispose();diagnosticMainImage?.Dispose();throw;}
    }
    private void StartCore(ProcessStartInfo start,string? browserProfile,bool isolatedDiagnostics,string? diagnosticChildExecutable,IReadOnlyList<string>? originalArguments=null)
    {
        if(Desktop is not null || start.UseShellExecute || start.ArgumentList.Count!=0 || !string.IsNullOrEmpty(start.Arguments) ||
            !Path.GetFileName(start.FileName).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase))throw new UserFacingException("原生前端管道启动参数无效。");
        var handles=new[]{input.ClientSafePipeHandle.DangerousGetHandle(),output.ClientSafePipeHandle.DangerousGetHandle()};
        var command=new StringBuilder('"'+start.FileName+"\" --remote-debugging-pipe --remote-debugging-io-pipes="+
            handles[0].ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)+","+handles[1].ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(browserProfile is not null)
        {
            if(!Path.IsPathFullyQualified(browserProfile)||browserProfile.Contains('"')||browserProfile.Contains('\0')||!Directory.Exists(browserProfile))throw new IOException();
            command.Append(" --user-data-dir=\"").Append(browserProfile).Append('"');
            if(isolatedDiagnostics)command.Append(" --enable-logging=file --log-file=\"").Append(Path.Combine(browserProfile,"chromium-smoke.log")).Append('"');
        }
        if(diagnosticChildExecutable is not null)
        {
            if(!isolatedDiagnostics||diagnosticJob is null||diagnosticImage is null||!string.Equals(diagnosticChildExecutable,diagnosticImage.ChildExecutable,StringComparison.Ordinal))throw new InvalidOperationException("Unverified diagnostic child image.");
            command.Append(" --browser-subprocess-path=\"").Append(diagnosticChildExecutable).Append('"');
        }
        if(originalArguments is not null)
        {
            if(originalArguments.Count>64||originalArguments.Sum(argument=>argument.Length)>24000)throw new IOException("Native activation arguments exceed their bound.");
            foreach(var argument in originalArguments)
            {
                if(argument.Contains('\0')||new[]{"--remote-debugging","--user-data-dir","--browser-subprocess-path","--type="}.Any(prefix=>argument.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))
                    throw new UserFacingException("启动参数与原生订阅连接冲突。");
                command.Append(' ').Append(QuoteArgument(argument));
            }
        }
        var attributeCount=diagnosticJob is null?1:2;
        nuint bytes=0;_ = InitializeProcThreadAttributeList(IntPtr.Zero,attributeCount,0,ref bytes);
        if(bytes==0 || bytes>65536)throw new UserFacingException("无法建立当前用户专用的前端管道。");
        var attributes=Marshal.AllocHGlobal((int)bytes);var inherited=Marshal.AllocHGlobal(IntPtr.Size*2);IntPtr environment=IntPtr.Zero;IntPtr jobList=IntPtr.Zero;var initialized=false;
        try
        {
            if(!InitializeProcThreadAttributeList(attributes,attributeCount,0,ref bytes))ThrowLaunchError();initialized=true;
            Marshal.WriteIntPtr(inherited,0,handles[0]);Marshal.WriteIntPtr(inherited,IntPtr.Size,handles[1]);
            if(!UpdateProcThreadAttribute(attributes,0,(nuint)0x20002,inherited,(nuint)(IntPtr.Size*2),IntPtr.Zero,IntPtr.Zero))ThrowLaunchError();
            if(diagnosticJob is not null)
            {
                jobList=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(jobList,diagnosticJob.DangerousHandle);
                if(!UpdateProcThreadAttribute(attributes,0,(nuint)0x2000D,jobList,(nuint)IntPtr.Size,IntPtr.Zero,IntPtr.Zero))ThrowLaunchError();
            }
            var entries=start.Environment.Where(e=>e.Value is not null).OrderBy(e=>e.Key,StringComparer.OrdinalIgnoreCase).ToArray();
            if(entries.Any(e=>e.Key.Contains('\0')||e.Value!.Contains('\0')||e.Key.Contains('=')))throw new IOException();
            var block=string.Join('\0',entries.Select(e=>e.Key+"="+e.Value))+"\0\0";
            environment=Marshal.StringToHGlobalUni(block);
            var info=new StartupInfoEx {StartupInfo=new(){Size=Marshal.SizeOf<StartupInfoEx>(),
                Flags=start.WindowStyle==ProcessWindowStyle.Hidden?1u:0u,ShowWindow=0},Attributes=attributes};
            if(!CreateProcess(start.FileName,command,IntPtr.Zero,IntPtr.Zero,true,0x00080000|0x00000400|(diagnosticDebugDetach?6u:0u),environment,start.WorkingDirectory,ref info,out var process))ThrowLaunchError();
            try
            {
                diagnosticJob?.VerifyMembership(process.Process);
                if(diagnosticDebugDetach)
                {
                    var detached=NativeDiagnosticDebugDetach.Complete(process.Process,process.Thread,process.ProcessId,process.ThreadId,diagnosticJob!.DangerousHandle);
                    DiagnosticDebugDetachStatus=detached;
                    if(!detached.Passed)throw new IOException("Owned diagnostic debugger detach failed.");
                }
                Desktop=Process.GetProcessById((int)process.ProcessId);
            }
            finally{CloseHandle(process.Thread);CloseHandle(process.Process);}
            input.DisposeLocalCopyOfClientHandle();output.DisposeLocalCopyOfClientHandle();
            reading=ReadAsync();
        }
        catch when(diagnosticChildExecutable is null){throw new UserFacingException("签名桌面的私有前端管道未启动；没有开放网络调试端口或修改原生登录。");}
        finally
        {
            if(initialized)DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);Marshal.FreeHGlobal(inherited);if(jobList!=IntPtr.Zero)Marshal.FreeHGlobal(jobList);if(environment!=IntPtr.Zero)Marshal.ZeroFreeGlobalAllocUnicode(environment);
        }
    }
    private static string QuoteArgument(string value)
    {
        var result=new StringBuilder("\"");var slashes=0;
        foreach(var character in value)
        {
            if(character=='\\'){slashes++;continue;}
            result.Append('\\',character=='"'?slashes*2+1:slashes);slashes=0;result.Append(character);
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    private void ThrowLaunchError()
    {
        var error=Marshal.GetLastWin32Error();
        if(diagnosticJob is not null){DiagnosticLaunchErrorCode=error;throw new Win32Exception(error);}
        throw new IOException();
    }
    internal async Task<JsonElement> SendAsync(string method,object? parameters=null,string? session=null,CancellationToken cancellation=default)
    {
        if(Desktop is null)throw new IOException("Native surface unavailable.");
        var id=Interlocked.Increment(ref next);var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!pending.TryAdd(id,completion))throw new IOException();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var registration=deadline.Token.Register(()=>completion.TrySetCanceled(deadline.Token));
        try
        {
            var packet=JsonSerializer.SerializeToUtf8Bytes(new{id,method,@params=parameters??new{},sessionId=session},
                new JsonSerializerOptions{DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull});
            await sending.WaitAsync(deadline.Token).ConfigureAwait(false);
            try{await input.WriteAsync(packet,deadline.Token).ConfigureAwait(false);await input.WriteAsync(new byte[1],deadline.Token).ConfigureAwait(false);await input.FlushAsync(deadline.Token).ConfigureAwait(false);}
            finally{sending.Release();}
            var result=await completion.Task.ConfigureAwait(false);
            if(result.TryGetProperty("error",out _))throw new IOException("Native surface protocol refused.");
            return result.TryGetProperty("result",out var value)?value:default;
        }
        finally{pending.TryRemove(id,out _);}
    }
    private async Task ReadAsync()
    {
        var chunk=new byte[65536];using var message=new MemoryStream();
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var count=await output.ReadAsync(chunk,stop.Token).ConfigureAwait(false);if(count==0)break;
                var offset=0;
                for(var i=0;i<count;i++)
                {
                    if(chunk[i]!=0)continue;
                    message.Write(chunk,offset,i-offset);offset=i+1;
                    if(message.Length>64*1024*1024)throw new IOException();
                    if(message.Length>0)
                    {
                        using var document=JsonDocument.Parse(message.GetBuffer().AsMemory(0,(int)message.Length),new JsonDocumentOptions{MaxDepth=48});
                        var packet=document.RootElement.Clone();
                        if(packet.TryGetProperty("id",out var id) && id.TryGetInt32(out var number))
                        {if(pending.TryGetValue(number,out var completion))completion.TrySetResult(packet);}
                        else if(packet.TryGetProperty("method",out var method) && method.GetString() is "Target.attachedToTarget" or "Target.detachedFromTarget" or "Fetch.requestPaused")events.Writer.TryWrite(packet);
                    }
                    message.SetLength(0);
                }
                if(offset<count)message.Write(chunk,offset,count-offset);
                if(message.Length>64*1024*1024)throw new IOException();
            }
        }
        catch(Exception error) when(error is IOException or JsonException or OperationCanceledException or ObjectDisposedException){ReadTermination=error.GetType().Name;}
        finally
        {
            ReadTermination??="eof";
            events.Writer.TryComplete();foreach(var entry in pending.Values)entry.TrySetException(new IOException("Native surface disconnected."));
        }
    }
    public async ValueTask DisposeAsync()
    {
        // The production path owns no Job. Only a new isolated diagnostic tree is ever terminated here.
        diagnosticJob?.Dispose();diagnosticImage?.Dispose();diagnosticMainImage?.Dispose();
        stop.Cancel();input.Dispose();output.Dispose();
        if(reading is not null)try{await reading.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);}catch{}
        Desktop?.Dispose();stop.Dispose();sending.Dispose();
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo
    {
        internal int Size;internal string? Reserved;internal string? Desktop;internal string? Title;
        internal uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;internal ushort ShowWindow,ReservedCount;
        internal IntPtr ReservedPointer,StdInput,StdOutput,StdError;
    }
    [StructLayout(LayoutKind.Sequential)]private struct StartupInfoEx{internal StartupInfo StartupInfo;internal IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInformation{internal IntPtr Process,Thread;internal uint ProcessId,ThreadId;}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref nuint size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,nuint attribute,IntPtr value,nuint size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr environment,string cwd,ref StartupInfoEx startup,out ProcessInformation process);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
}
