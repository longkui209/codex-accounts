using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexAccountSwitcher.LiveSession;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Validation;

/// <summary>A single-use verified original or byte-compared official copy, read-locked for the diagnostic lifetime.</summary>
internal sealed class NativeDiagnosticChildImage : IDisposable
{
    internal const string EmptyHomeConfiguration = "cli_auth_credentials_store = \"file\"\n\n[analytics]\nenabled = false\n";
    private readonly List<FileStream> imageLocks;
    private readonly List<SafeFileHandle> linkedPathLocks;
    private readonly DateTime validatedUtc;
    private int claimed;
    private int disposed;
    internal string SourceExecutable { get; }
    internal string ChildExecutable { get; }
    internal string IsolatedRoot { get; }
    internal int VerifiedFileCount { get; }
    internal long VerifiedBytes { get; }
    internal bool UsesCopiedChildImage { get; }
    internal bool UsesLinkedChildImage => linkedPathLocks.Count!=0;
    internal string Mode => UsesLinkedChildImage ? "linked-original" : UsesCopiedChildImage ? "copied" : "original-installed";

    private NativeDiagnosticChildImage(string source, string child, string isolatedRoot, List<FileStream> locks, long bytes,bool copied=true,List<SafeFileHandle>? linkedLocks=null)
    {
        SourceExecutable = source; ChildExecutable = child; IsolatedRoot = isolatedRoot;
        UsesCopiedChildImage=copied;
        linkedPathLocks=linkedLocks??new();
        imageLocks = locks; VerifiedFileCount = locks.Count; VerifiedBytes = bytes; validatedUtc = DateTime.UtcNow;
    }

    internal static async Task<NativeDiagnosticChildImage> ValidateLinkedOriginalAsync(string installedDesktopExecutable,
        string linkAppDirectory,string isolatedRoot,CancellationToken cancellation=default)
    {
        var stage="linked-path-boundary";
        NativeDiagnosticChildImage? original=null;
        var linkLocks=new List<SafeFileHandle>();
        try
        {
            var source=FullPath(installedDesktopExecutable);var link=FullPath(linkAppDirectory);var root=FullPath(isolatedRoot);
            var owner=Directory.GetParent(link)??throw Refused();
            var validation=owner.Parent??throw Refused();
            var rootValidation=Directory.GetParent(root)?.Parent??throw Refused();
            if(!Path.GetFileName(link).Equals("app",StringComparison.OrdinalIgnoreCase)||
                !Regex.IsMatch(owner.Name,@"\Aofficial-child-link-[0-9a-f]{32}\z",RegexOptions.CultureInvariant)||
                !validation.Name.Equals("validation-output",StringComparison.OrdinalIgnoreCase)||!Same(validation.FullName,rootValidation.FullName)||
                Within(root,owner.FullName)||Same(root,owner.FullName))throw Refused();
            // The one approved junction is the sole exception. All its ancestors, the
            // original package and the isolated profile still reject every reparse point.
            NoReparseAncestors(owner.FullName);NoReparseAncestors(source);NoReparseAncestors(root);
            if((File.GetAttributes(link)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=(FileAttributes.Directory|FileAttributes.ReparsePoint))throw Refused();
            stage="linked-junction-lock";
            linkLocks.Add(OpenDirectoryEntry(owner.FullName));
            linkLocks.Add(OpenDirectoryEntry(link));
            stage="linked-junction-target";
            if(!Same(ReadJunctionTarget(linkLocks[1]),Path.GetDirectoryName(source)!))throw Refused();
            stage="linked-original-validation";
            original=await ValidateOriginalAsync(source,root,cancellation).ConfigureAwait(false);
            stage="linked-executable-file-identity";
            var alias=Path.Combine(link,"ChatGPT.exe");
            VerifyLinkedExecutableIdentity(source,alias,linkLocks[1],original.imageLocks);
            var aliases=EnumerateClosedTree(link);
            var originals=EnumerateClosedTree(Path.GetDirectoryName(source)!);
            if(!aliases.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(originals.Keys.Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))throw Refused();
            var transferred=original.imageLocks.ToList();
            var result=new NativeDiagnosticChildImage(source,alias,root,transferred,original.VerifiedBytes,copied:false,linkedLocks:linkLocks);
            original.imageLocks.Clear();original.Dispose();original=null;
            linkLocks=new();
            return result;
        }
        catch(Exception error)
        {
            original?.Dispose();foreach(var handle in linkLocks)handle.Dispose();
            var failure=new IOException("Isolated linked original child image validation failed.",error.HResult);
            failure.Data["native_child_image_stage"]=error.Data["native_child_image_stage"] is string nested?stage+":"+nested:stage;
            failure.Data["native_child_image_hresult"]=error.HResult;
            if(error is Win32Exception native)failure.Data["native_child_image_win32_error"]=native.NativeErrorCode;
            foreach(var key in new[]{"native_child_image_signature_helper_exit_code","native_child_image_signature_helper_stderr_present"})
                if(error.Data[key] is int or bool)failure.Data[key]=error.Data[key];
            throw failure;
        }
    }

    internal static async Task<NativeDiagnosticChildImage> ValidateOriginalAsync(string installedDesktopExecutable,
        string isolatedRoot,CancellationToken cancellation=default)
    {
        var stage="original-path-boundary";
        var locks=new List<FileStream>();
        try
        {
            var source=FullPath(installedDesktopExecutable);
            var root=FullPath(isolatedRoot);
            var output=Directory.GetParent(root)??throw Refused();
            var validation=output.Parent??throw Refused();
            if(!Path.GetFileName(root).Equals("p",StringComparison.OrdinalIgnoreCase)||
                !validation.Name.Equals("validation-output",StringComparison.OrdinalIgnoreCase)||validation.Parent is null||
                !File.Exists(Path.Combine(validation.Parent.FullName,"CodexAccountSwitcher.csproj"))||!Directory.Exists(root))throw Refused();
            stage="original-ancestor-check";
            NoReparseAncestors(source);NoReparseAncestors(root);
            stage="original-installed-source-discovery";
            if(!Same(source,DesktopLaunch.FindInstalledDesktop())||!Path.GetFileName(source).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase))throw Refused();
            var directory=Path.GetDirectoryName(source)??throw Refused();
            if(Within(root,directory)||Within(directory,root))throw Refused();
            stage="original-file-set";
            var files=EnumerateClosedTree(directory);
            if(files.Count==0)throw Refused();
            long bytes=0;
            stage="original-file-lock-and-read";
            foreach(var file in files.OrderBy(pair=>pair.Key,StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                var stream=new FileStream(file.Value,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
                locks.Add(stream);
                // Read the complete locked original image. There is no copy comparison in this mode.
                _=await SHA256.HashDataAsync(stream,cancellation).ConfigureAwait(false);
                bytes=checked(bytes+stream.Length);
            }
            stage="original-core-signature-query";
            await VerifyCoreSignaturesAsync(directory,cancellation).ConfigureAwait(false);
            stage="original-source-stability";
            if(!Same(DesktopLaunch.FindInstalledDesktop(),source)||
                !files.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(EnumerateClosedTree(directory).Keys.Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))throw Refused();
            return new NativeDiagnosticChildImage(source,source,root,locks,bytes,copied:false);
        }
        catch(Exception error)
        {
            foreach(var stream in locks)stream.Dispose();
            var failure=new IOException("Isolated original child image validation failed.",error.HResult);
            failure.Data["native_child_image_stage"]=stage;
            failure.Data["native_child_image_hresult"]=error.HResult;
            if(error is Win32Exception native)failure.Data["native_child_image_win32_error"]=native.NativeErrorCode;
            foreach(var key in new[]{"native_child_image_signature_helper_exit_code","native_child_image_signature_helper_stderr_present"})
                if(error.Data[key] is int or bool)failure.Data[key]=error.Data[key];
            throw failure;
        }
    }

    internal static async Task<NativeDiagnosticChildImage> ValidateAsync(string installedDesktopExecutable,
        string imageAppDirectory, string isolatedRoot, CancellationToken cancellation = default)
    {
        var stage = "path-boundary";
        try { return await ValidateCoreAsync(installedDesktopExecutable, imageAppDirectory, isolatedRoot, cancellation, value => stage = value).ConfigureAwait(false); }
        catch (Exception error)
        {
            // Preserve only fixed diagnostic labels and numeric metadata, never original paths or helper output.
            var failure = new IOException("Isolated child image validation failed.", error.HResult);
            failure.Data["native_child_image_stage"] = stage;
            failure.Data["native_child_image_hresult"] = error.HResult;
            if (error is Win32Exception native) failure.Data["native_child_image_win32_error"] = native.NativeErrorCode;
            foreach (var key in new[] { "native_child_image_signature_helper_exit_code", "native_child_image_signature_helper_stderr_present" })
                if (error.Data[key] is int or bool) failure.Data[key] = error.Data[key];
            throw failure;
        }
    }

    private static async Task<NativeDiagnosticChildImage> ValidateCoreAsync(string installedDesktopExecutable,
        string imageAppDirectory, string isolatedRoot, CancellationToken cancellation, Action<string> progress)
    {
        var source = FullPath(installedDesktopExecutable);
        var image = FullPath(imageAppDirectory);
        var root = FullPath(isolatedRoot);
        var imageOwner = Directory.GetParent(image) ?? throw Refused();
        var validation = imageOwner.Parent ?? throw Refused();
        if (!Path.GetFileName(image).Equals("app", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(imageOwner.Name, @"\Aofficial-child-copy-[0-9a-f]{32}\z", RegexOptions.CultureInvariant) ||
            !validation.Name.Equals("validation-output", StringComparison.OrdinalIgnoreCase) ||
            !Within(root, validation.FullName) || Same(root, imageOwner.FullName) || Within(root, imageOwner.FullName) || !Directory.Exists(root)) throw Refused();
        progress("ancestor-check");
        NoReparseAncestors(source); NoReparseAncestors(image); NoReparseAncestors(root);
        progress("installed-source-discovery");
        var actualInstalled = DesktopLaunch.FindInstalledDesktop();
        if (!Same(source, actualInstalled) || !Path.GetFileName(source).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) throw Refused();
        var originalDirectory = Path.GetDirectoryName(source) ?? throw Refused();
        if (Within(image, originalDirectory) || Within(originalDirectory, image)) throw Refused();
        progress("file-set");
        var sourceFiles = EnumerateClosedTree(originalDirectory);
        var imageFiles = EnumerateClosedTree(image);
        if (sourceFiles.Count == 0 || sourceFiles.Count != imageFiles.Count ||
            sourceFiles.Keys.Except(imageFiles.Keys, StringComparer.OrdinalIgnoreCase).Any()) throw Refused();
        var locks = new List<FileStream>(imageFiles.Count);
        long bytes = 0;
        try
        {
            progress("file-hash");
            foreach (var pair in sourceFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                await using var original = new FileStream(pair.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var copy = new FileStream(imageFiles[pair.Key], FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                locks.Add(copy);
                if (original.Length != copy.Length) throw Refused();
                var hashes = await Task.WhenAll(SHA256.HashDataAsync(original, cancellation).AsTask(), SHA256.HashDataAsync(copy, cancellation).AsTask()).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(hashes[0], hashes[1])) throw Refused();
                bytes = checked(bytes + copy.Length);
            }
            // The source package is checked by DesktopLaunch. Core copied launch images also require their own valid OpenAI signature.
            progress("core-signature-query");
            await VerifyCoreSignaturesAsync(image, cancellation).ConfigureAwait(false);
            progress("source-stability");
            if (!Same(DesktopLaunch.FindInstalledDesktop(), source) ||
                !sourceFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(EnumerateClosedTree(originalDirectory).Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ||
                !imageFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(EnumerateClosedTree(image).Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw Refused();
            return new NativeDiagnosticChildImage(source, Path.Combine(image, "ChatGPT.exe"), root, locks, bytes);
        }
        catch { foreach (var stream in locks) stream.Dispose(); throw; }
    }

    internal void ClaimForStart(ProcessStartInfo start, string profile)
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref claimed, 1, 0) != 0 ||
            DateTime.UtcNow - validatedUtc > TimeSpan.FromMinutes(2) || !Same(start.FileName, SourceExecutable)) throw Refused();
        var workingDirectory = FullPath(start.WorkingDirectory);
        var wslStub = Path.Combine(workingDirectory, "wsl.exe");
        if (!Within(workingDirectory, IsolatedRoot) || !Path.GetFileName(workingDirectory).Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(workingDirectory) || !File.Exists(wslStub)) throw Refused();
        NoReparseAncestors(workingDirectory); NoReparseAncestors(wslStub);
        NoReparseAncestors(SourceExecutable);
        if(UsesLinkedChildImage)
        {
            if(linkedPathLocks.Count!=2||linkedPathLocks.Any(handle=>handle.IsInvalid||handle.IsClosed))throw Refused();
            NoReparseAncestors(Path.GetDirectoryName(Path.GetDirectoryName(ChildExecutable)!)!);
            VerifyLinkedExecutableIdentity(SourceExecutable,ChildExecutable,linkedPathLocks[1],imageLocks);
            if(!EnumerateClosedTree(Path.GetDirectoryName(ChildExecutable)!).Keys.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(EnumerateClosedTree(Path.GetDirectoryName(SourceExecutable)!).Keys.Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))throw Refused();
        }
        else NoReparseAncestors(ChildExecutable);
        var currentFiles = EnumerateClosedTree(Path.GetDirectoryName(UsesLinkedChildImage?SourceExecutable:ChildExecutable)!);
        if (currentFiles.Count != imageLocks.Count || currentFiles.Values.Except(imageLocks.Select(stream => stream.Name), StringComparer.OrdinalIgnoreCase).Any()) throw Refused();
        var realProfile = FullPath(profile);
        var home = EnvironmentPath(start, "CODEX_HOME");
        var sqlite = EnvironmentPath(start, "CODEX_SQLITE_HOME");
        var assignedProfile = EnvironmentPath(start, "CODEX_ELECTRON_USER_DATA_PATH");
        if (!Same(realProfile, assignedProfile) || Same(realProfile, home) || Same(realProfile, sqlite) || Same(home, sqlite) ||
            !start.Environment.TryGetValue("CODEX_SPARKLE_ENABLED", out var update) || update != "false") throw Refused();
        foreach (var path in new[] { realProfile, home, sqlite })
        {
            if (!Within(path, IsolatedRoot) || !Directory.Exists(path)) throw Refused();
            NoReparseAncestors(path);
            var entries = Directory.EnumerateFileSystemEntries(path).ToArray();
            if (Same(path, home))
            {
                var config = Path.Combine(home, "config.toml");
                if (entries.Length != 1 || !Same(entries[0], config)) throw Refused();
                NoReparseAncestors(config);
                if (!File.ReadAllBytes(config).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(EmptyHomeConfiguration))) throw Refused();
            }
            else if (entries.Length != 0) throw Refused();
        }
        foreach (var name in new[] { "OPENAI_API_KEY", "CHATGPT_API_KEY", "CODEX_API_KEY", "CODEX_CLI_PATH", "CODEX_APP_SERVER_WS_URL", "CODEX_APP_SERVER_FORCE_CLI", "CODEX_WINDOWS_REGISTERED_CORE" })
            if (start.Environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)) throw Refused();
    }

    private static string EnvironmentPath(ProcessStartInfo start, string name) =>
        start.Environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? FullPath(value) : throw Refused();
    private static string FullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || path.Contains('\0')) throw Refused();
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static bool Same(string a, string b) => string.Equals(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string parent) => FullPath(path).StartsWith(FullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void NoReparseAncestors(string path)
    {
        for (string? current = FullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refused();
    }
    private static Dictionary<string, string> EnumerateClosedTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Refused();
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (!result.TryAdd(Path.GetRelativePath(root, entry), entry)) throw Refused();
            }
        }
        return result;
    }

    private static SafeFileHandle OpenDirectoryEntry(string path)
    {
        // OPEN_REPARSE_POINT locks the junction object, not the target directory.
        // Denying WRITE and DELETE sharing also pins its unique owner directory.
        var handle=CreateFile(path,0x80000000,1,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if(handle.IsInvalid){var error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
        return handle;
    }
    private static string ReadJunctionTarget(SafeFileHandle handle)
    {
        var bytes=new byte[16384];
        if(!DeviceIoControl(handle,0x000900A8,IntPtr.Zero,0,bytes,(uint)bytes.Length,out var count,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(count<16||BitConverter.ToUInt32(bytes,0)!=0xA0000003)throw Refused(); // MOUNT_POINT only; never symbolic links.
        var offset=BitConverter.ToUInt16(bytes,8);var length=BitConverter.ToUInt16(bytes,10);
        if(length==0||(length&1)!=0||(offset&1)!=0||16L+offset+length>count)throw Refused();
        return NormalizeFinalPath(Encoding.Unicode.GetString(bytes,16+offset,length));
    }
    private static void VerifyLinkedExecutableIdentity(string source,string alias,SafeFileHandle junction,IReadOnlyList<FileStream> originals)
    {
        if(!Same(ReadJunctionTarget(junction),Path.GetDirectoryName(source)!))throw Refused();
        var attributes=File.GetAttributes(alias);
        if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw Refused();
        var sourceFile=originals.SingleOrDefault(file=>Same(file.Name,source))??throw Refused();
        using var aliasFile=new FileStream(alias,FileMode.Open,FileAccess.Read,FileShare.Read);
        var a=FileIdentity(sourceFile.SafeFileHandle);var b=FileIdentity(aliasFile.SafeFileHandle);
        if(a.VolumeSerial!=b.VolumeSerial||a.FileIdLow!=b.FileIdLow||a.FileIdHigh!=b.FileIdHigh||
            !Same(FinalPath(sourceFile.SafeFileHandle),source)||!Same(FinalPath(aliasFile.SafeFileHandle),source))throw Refused();
    }
    private static NativeFileIdentity FileIdentity(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandleEx(handle,18,out var info,(uint)Marshal.SizeOf<NativeFileIdentity>()))throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer=new StringBuilder(32768);var length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0);
        if(length==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        if(length>=buffer.Capacity)throw Refused();
        return NormalizeFinalPath(buffer.ToString());
    }
    private static string NormalizeFinalPath(string value)
    {
        if(value.StartsWith(@"\??\",StringComparison.Ordinal)||value.StartsWith(@"\\?\",StringComparison.Ordinal))value=value[4..];
        if(value.Length<3||value[1]!=':'||value[2]!='\\')throw Refused();
        return FullPath(value);
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeFileIdentity{internal ulong VolumeSerial,FileIdLow,FileIdHigh;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string file,uint access,uint share,IntPtr attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file,uint code,IntPtr input,uint inputLength,[Out]byte[] output,uint outputLength,out uint returned,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int infoClass,out NativeFileIdentity info,uint size);
    [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint size,uint flags);
    private static async Task VerifyCoreSignaturesAsync(string image, CancellationToken cancellation)
    {
        const string script = """
            $ErrorActionPreference='Stop'
            $ProgressPreference='SilentlyContinue'
            $paths=$env:CODEX_DIAGNOSTIC_SIGNATURE_PATHS | ConvertFrom-Json
            if($paths -isnot [array] -or $paths.Count -ne 3){exit 12}
            $records=@(foreach($path in $paths){
              $s=Get-AuthenticodeSignature -LiteralPath $path
              [pscustomobject]@{valid=([string]$s.Status -eq 'Valid');official=($s.SignerCertificate -and $s.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false) -eq 'OpenAI OpCo, LLC')}
            })
            ConvertTo-Json -InputObject $records -Compress
            """;
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["CODEX_DIAGNOSTIC_SIGNATURE_PATHS"] = JsonSerializer.Serialize(new[] { "ChatGPT.exe", "chrome.dll", "chrome_elf.dll" }.Select(name => Path.Combine(image, name)));
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw Refused();
        var output = process.StandardOutput.ReadToEndAsync(cancellation); var error = process.StandardError.ReadToEndAsync(cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        bool? stderrPresent = null;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false); var errors = await error.ConfigureAwait(false);
            stderrPresent = errors.Length != 0;
            if (process.ExitCode != 0 || errors.Length != 0 || text.Length > 2048) throw Refused();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != 3 ||
                doc.RootElement.EnumerateArray().Any(row => !row.GetProperty("valid").GetBoolean() || !row.GetProperty("official").GetBoolean())) throw Refused();
        }
        catch (Exception failure)
        {
            if (process.HasExited) failure.Data["native_child_image_signature_helper_exit_code"] = process.ExitCode;
            if (stderrPresent.HasValue) failure.Data["native_child_image_signature_helper_stderr_present"] = stderrPresent.Value;
            throw;
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } }
    }
    private static IOException Refused() => new("The isolated diagnostic child image or launch boundary was not verified.");
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try{foreach (var stream in imageLocks) stream.Dispose();}
        finally{foreach(var handle in linkedPathLocks)handle.Dispose();}
    }
}
