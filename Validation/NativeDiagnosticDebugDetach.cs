using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexAccountSwitcher.Validation;

/// <summary>
/// Diagnostic-only synchronous completion for a process the caller just created with
/// CREATE_SUSPENDED | DEBUG_ONLY_THIS_PROCESS in its owned Job. Never opens or attaches
/// to an existing process, reads memory, changes mitigation policy, or owns caller handles.
/// Must run on the same native thread immediately after CreateProcess returns.
/// </summary>
internal static class NativeDiagnosticDebugDetach
{
    internal sealed record Result
    {
        public bool Passed {get;init;}
        public int Stage {get;init;}
        public int Win32Error {get;init;}
        public uint ProcessId {get;init;}
        public uint ThreadId {get;init;}
        public uint CallerThreadId {get;init;}
        public long ElapsedMilliseconds {get;init;}
        public bool DeadlineExceeded {get;init;}
        public bool HandleIdsVerified {get;init;}
        public bool JobMembershipVerified {get;init;}
        public bool ExactJobVerified {get;init;}
        public bool InitiallyDebugged {get;init;}
        public uint? InitialResumePrevious {get;init;}
        public bool CreateEventSeen {get;init;}
        public bool EventFilePresent {get;init;}
        public bool EventFileClosed {get;init;}
        public uint? ExplicitSuspendPrevious {get;init;}
        public bool EventContinued {get;init;}
        public bool Detached {get;init;}
        public bool NoDebuggerBeforeResume {get;init;}
        public uint? FinalResumePrevious {get;init;}
        public bool FailureDetachAttempted {get;init;}
        public int? FailureCleanupError {get;init;}
        public bool CallerMustCloseDiagnosticJob {get;init;}
    }

    // Supplying the exact owned Job allows this helper to independently verify it.
    // The four-argument form relies on the caller's already verified Job ownership.
    internal static Result Complete(IntPtr process,IntPtr thread,uint pid,uint tid,IntPtr ownedJob=default)
    {
        const int invalidHandle=6,invalidData=13,invalidState=5023,timeout=1460;
        var watch=Stopwatch.StartNew();var callerThread=GetCurrentThreadId();
        var stage=0;var error=0;var passed=false;var handleIds=false;var inJob=false;
        var initiallyDebugged=false;var createSeen=false;var filePresent=false;var fileClosed=false;
        var continued=false;var detached=false;var clean=false;var failureDetach=false;
        int? cleanupError=null;uint? initialResume=null,explicitSuspend=null,finalResume=null;
        var eventPending=false;var threadHeld=true;var debugEvent=new DebugEvent();
        IntPtr eventFile=IntPtr.Zero;
        void Fail(int value){if(error==0)error=value==0?invalidState:value;}
        bool Step(int value)
        {
            if(error!=0)return false;
            stage=value;
            if(watch.ElapsedMilliseconds>=5000){Fail(timeout);return false;}
            return true;
        }
        bool CloseEventFile()
        {
            if(eventFile==IntPtr.Zero||eventFile==new IntPtr(-1))return true;
            // Never close either PROCESS_INFORMATION handle supplied by the caller.
            if(eventFile==process||eventFile==thread){Fail(invalidData);return false;}
            var value=eventFile;eventFile=IntPtr.Zero;
            if(!CloseHandle(value)){Fail(Marshal.GetLastWin32Error());return false;}
            fileClosed=true;return true;
        }
        Thread.BeginThreadAffinity();
        try
        {
            do
            {
                if(!Step(1))break;
                if(IntPtr.Size!=8||process==IntPtr.Zero||process==new IntPtr(-1)||thread==IntPtr.Zero||thread==new IntPtr(-1)||
                    pid==0||tid==0||GetProcessId(process)!=pid||GetThreadId(thread)!=tid||GetProcessIdOfThread(thread)!=pid)
                {Fail(invalidHandle);break;}
                handleIds=true;
                if(!IsProcessInJob(process,ownedJob,out inJob)){Fail(Marshal.GetLastWin32Error());break;}
                if(!inJob){Fail(invalidState);break;}
                if(!CheckRemoteDebuggerPresent(process,out initiallyDebugged)){Fail(Marshal.GetLastWin32Error());break;}
                if(!initiallyDebugged){Fail(invalidState);break;}
                if(!Step(2))break;
                if(!DebugSetProcessKillOnExit(false)){Fail(Marshal.GetLastWin32Error());break;}
                if(!Step(3))break;
                initialResume=ResumeThread(thread);
                if(initialResume is 0 or 1)threadHeld=false;
                if(initialResume!=1){Fail(initialResume==uint.MaxValue?Marshal.GetLastWin32Error():invalidState);break;}
                if(!Step(4))break;
                var remaining=(uint)Math.Clamp(5000-watch.ElapsedMilliseconds,1,5000);
                if(!WaitForDebugEvent(out debugEvent,remaining)){Fail(Marshal.GetLastWin32Error());break;}
                eventPending=true;
                // These are handles to files owned by this debugger, never image pointers.
                if(debugEvent.Code is 3 or 6){eventFile=debugEvent.CreateProcess.File;filePresent=eventFile!=IntPtr.Zero&&eventFile!=new IntPtr(-1);}
                if(!Step(5))break;
                createSeen=debugEvent.Code==3&&debugEvent.ProcessId==pid&&debugEvent.ThreadId==tid;
                if(!createSeen||debugEvent.CreateProcess.Process==IntPtr.Zero||debugEvent.CreateProcess.Thread==IntPtr.Zero||
                    GetProcessId(debugEvent.CreateProcess.Process)!=pid||GetThreadId(debugEvent.CreateProcess.Thread)!=tid)
                {Fail(invalidData);break;}
                if(!CloseEventFile())break;
                if(!Step(6))break;
                explicitSuspend=SuspendThread(thread);
                if(explicitSuspend!=uint.MaxValue)threadHeld=true;
                if(explicitSuspend!=0){Fail(explicitSuspend==uint.MaxValue?Marshal.GetLastWin32Error():invalidState);break;}
                if(!Step(7))break;
                if(!ContinueDebugEvent(pid,tid,0x00010002)){Fail(Marshal.GetLastWin32Error());break;}
                continued=true;eventPending=false;
                if(!Step(8))break;
                if(!DebugActiveProcessStop(pid)){Fail(Marshal.GetLastWin32Error());break;}
                detached=true;
                if(!Step(9))break;
                if(!CheckRemoteDebuggerPresent(process,out var attached)){Fail(Marshal.GetLastWin32Error());break;}
                clean=!attached;if(!clean){Fail(invalidState);break;}
                if(!Step(10))break;
                finalResume=ResumeThread(thread);
                if(finalResume==1)threadHeld=false;
                if(finalResume!=1){Fail(finalResume==uint.MaxValue?Marshal.GetLastWin32Error():invalidState);break;}
                if(!Step(11))break;
                passed=GetCurrentThreadId()==callerThread;
                if(!passed)Fail(invalidState);
            }while(false);
        }
        catch{Fail(31);}
        finally
        {
            // On failure, keep this owned initial thread explicitly suspended before
            // detaching. Never acknowledge an event belonging to a different PID/TID.
            // The caller must close its Job on every failed result, including timeout.
            if(!passed&&handleIds&&inJob&&initiallyDebugged&&!detached&&watch.ElapsedMilliseconds<5000)
            {
                if(!threadHeld)
                {
                    var previous=SuspendThread(thread);
                    if(previous!=uint.MaxValue)threadHeld=true;else cleanupError=Marshal.GetLastWin32Error();
                }
                if(threadHeld&&watch.ElapsedMilliseconds<5000)
                {
                    if(eventPending&&debugEvent.ProcessId==pid&&debugEvent.ThreadId==tid)
                    {
                        if(ContinueDebugEvent(pid,tid,0x00010002)){continued=true;eventPending=false;}
                        else cleanupError??=Marshal.GetLastWin32Error();
                    }
                    if(watch.ElapsedMilliseconds<5000)
                    {
                        failureDetach=true;
                        if(DebugActiveProcessStop(pid))detached=true;else cleanupError??=Marshal.GetLastWin32Error();
                    }
                }
            }
            if(eventFile!=IntPtr.Zero&&!CloseEventFile())passed=false;
            Thread.EndThreadAffinity();
        }
        var elapsed=watch.ElapsedMilliseconds;
        if(elapsed>=5000){passed=false;Fail(timeout);}
        return new Result
        {
            Passed=passed&&error==0,Stage=stage,Win32Error=error,ProcessId=pid,ThreadId=tid,CallerThreadId=callerThread,
            ElapsedMilliseconds=elapsed,DeadlineExceeded=elapsed>=5000,HandleIdsVerified=handleIds,
            JobMembershipVerified=inJob,ExactJobVerified=inJob&&ownedJob!=IntPtr.Zero,InitiallyDebugged=initiallyDebugged,
            InitialResumePrevious=initialResume,CreateEventSeen=createSeen,EventFilePresent=filePresent,EventFileClosed=fileClosed,
            ExplicitSuspendPrevious=explicitSuspend,EventContinued=continued,Detached=detached,NoDebuggerBeforeResume=clean,
            FinalResumePrevious=finalResume,FailureDetachAttempted=failureDetach,FailureCleanupError=cleanupError,
            CallerMustCloseDiagnosticJob=!passed||error!=0
        };
    }

    // x64 DEBUG_EVENT: three DWORDs then an aligned 160-byte union (largest member
    // EXCEPTION_DEBUG_INFO). Only CREATE_PROCESS/LOAD_DLL hFile metadata is examined.
    [StructLayout(LayoutKind.Explicit,Size=176)]private struct DebugEvent
    {
        [FieldOffset(0)]internal uint Code;
        [FieldOffset(4)]internal uint ProcessId;
        [FieldOffset(8)]internal uint ThreadId;
        [FieldOffset(16)]internal CreateProcessDebugInfo CreateProcess;
    }
    [StructLayout(LayoutKind.Sequential)]private struct CreateProcessDebugInfo
    {
        internal IntPtr File,Process,Thread,BaseOfImage;
        internal uint DebugInfoFileOffset,DebugInfoSize;
        internal IntPtr ThreadLocalBase,StartAddress,ImageName;
        internal ushort Unicode;
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetThreadId(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process,IntPtr job,[MarshalAs(UnmanagedType.Bool)]out bool result);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process,[MarshalAs(UnmanagedType.Bool)]out bool present);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)]bool kill);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitForDebugEvent(out DebugEvent debugEvent,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ContinueDebugEvent(uint processId,uint threadId,uint status);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugActiveProcessStop(uint processId);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
