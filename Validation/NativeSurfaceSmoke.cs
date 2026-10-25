using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

internal static class NativeSurfaceSmoke
{
    // The old entry inherited almost the whole user environment and lacked browser recovery.
    internal static Task RunAsync(string outputRoot)=>Task.FromException(new UserFacingException(
        "旧前端诊断入口已停用。请使用带完整隔离和恢复保护的 --native-child-surface-smoke。"));

    internal static async Task RunChildImageAsync(string outputRoot,string? childExecutable,bool originalChildControl=false,bool linkedChild=false,bool debugDetach=false,string? mainAliasExecutable=null,bool requireForwardedChild=false,bool useCopiedMain=false)
    {
        if(originalChildControl&&linkedChild)throw new ArgumentException("Ambiguous diagnostic child mode.");
        if(useCopiedMain&&(originalChildControl||linkedChild||debugDetach||mainAliasExecutable is not null||requireForwardedChild))throw new ArgumentException("Copied runtime requires a single verified copy for every official process.");
        if(debugDetach&&!linkedChild)throw new ArgumentException("Diagnostic detach requires the linked original mode.");
        if(requireForwardedChild&&mainAliasExecutable is null)throw new ArgumentException("Forwarded child observation requires the isolated main alias.");
        if(mainAliasExecutable is not null&&(!debugDetach||!linkedChild||!Path.IsPathFullyQualified(mainAliasExecutable)||!Path.GetFileName(mainAliasExecutable).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Diagnostic main alias requires verified linked images and detach.");
        using var isolated=new IsolatedDesktopEnvironment(outputRoot);
        var steps=new List<string>();
        var observations=new HashSet<int>();
        NativeBrowserRegistrationGuard? registrations=null;
        NativeDiagnosticChildImage? image=null;
        NativeDiagnosticChildImage? mainImage=null;
        NativeDiagnosticProcessObservation? processMetadata=null;
        IReadOnlyList<NativeDiagnosticLaunchRoles.Sample>? launchRoles=null;
        NativeDiagnosticForwardedChild.Result? forwardedChild=null;
        var forwardedAliveSamples=0;
        DateTime? forwardedFirstAlive=null;
        var forwardedStable=false;
        var sandboxHistogram=new NativeDiagnosticSandboxHistogram();
        using var histogramStop=new CancellationTokenSource(TimeSpan.FromSeconds(55));
        Task? histogramObservation=null;
        string? failure=null;
        string? imageValidationStage=null;
        int? imageValidationHResult=null;
        int? imageValidationWin32Error=null;
        int? imageSignatureHelperExitCode=null;
        bool? imageSignatureHelperStderrPresent=null;
        var stage="validate-installed-desktop";
        var verified=false;
        var privatePipeResponded=false;
        var frontendVerified=false;
        var renderingVerified=false;
        var childPathsVerified=false;
        var launchAttempted=false;
        var cleanup=false;
        var pages=0;
        var cleanupFailures=new List<string>();
        var reportWritten=false;
        object? adapterBeforeClose=null;
        await using var pipe=new NativeSurfacePipe();
        await using var bridge=new NativeInputBridge(pipe,_=>Task.FromResult(InferenceGateService.Denied())){CaptureValidationFixture=true};
        try
        {
            var installed=DesktopLaunch.FindInstalledDesktop();
            if(!originalChildControl&&(childExecutable is null||!Path.IsPathFullyQualified(childExecutable)||!Path.GetFileName(childExecutable).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase)))
                throw new UserFacingException("诊断子映像路径无效。");
            stage="verify-module-directories-have-no-diagnostic-name-conflicts";
            foreach(var directory in new[]{Path.GetDirectoryName(installed)!,Path.Combine(Path.GetDirectoryName(installed)!,"resources"),childExecutable is null?null:Path.GetDirectoryName(childExecutable)})
                if(directory is not null)
                    foreach(var name in new[]{"wsl.exe","wsl.exe.com","wsl.exe.exe","reg","reg.com","reg.exe"})
                        if(File.Exists(Path.Combine(directory,name)))throw new UserFacingException("官方模块目录存在与隔离诊断命令冲突的文件。");
            stage="verify-isolated-wsl-resolution-and-fixture";
            using(var wslTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await isolated.VerifyUnavailableWslAsync(installed,wslTimeout.Token).ConfigureAwait(false);
            steps.Add("bare WSL name resolved to isolated fixture; only its verified absolute path executed");
            stage="verify-isolated-registry-noop-fixture";
            using(var registryTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await isolated.VerifyRegistryNoOpAsync(installed,registryTimeout.Token).ConfigureAwait(false);
            steps.Add("registry command resolved to isolated no-op fixture; registration is simulated only");
            stage="verify-official-child-image";
            using(var validationTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                image=originalChildControl
                    ?await NativeDiagnosticChildImage.ValidateOriginalAsync(installed,isolated.Root,validationTimeout.Token).ConfigureAwait(false)
                    :linkedChild?await NativeDiagnosticChildImage.ValidateLinkedOriginalAsync(installed,Path.GetDirectoryName(childExecutable)!,isolated.Root,validationTimeout.Token).ConfigureAwait(false)
                    :await NativeDiagnosticChildImage.ValidateAsync(installed,Path.GetDirectoryName(childExecutable)!,isolated.Root,validationTimeout.Token).ConfigureAwait(false);
            steps.Add(originalChildControl?"original installed child image verified and read-locked for control":linkedChild?"linked original child image identity verified and read-locked":"official child image complete file set compared and read-locked");
            if(mainAliasExecutable is not null)
            {
                stage="verify-linked-main-image";
                using var mainValidationTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
                mainImage=await NativeDiagnosticChildImage.ValidateLinkedOriginalAsync(installed,Path.GetDirectoryName(mainAliasExecutable)!,isolated.Root,mainValidationTimeout.Token).ConfigureAwait(false);
                steps.Add("separate main alias verified against the same original installed image");
            }
            stage="prepare-native-browser-recovery";
            registrations=NativeBrowserRegistrationGuard.Capture(isolated.Home,isolated.Root,Path.Combine(isolated.Root,"registration-backup"));
            steps.Add("exact browser registration snapshots protected with CurrentUser DPAPI");
            var start=isolated.BuildStartInfo(installed);
            stage="prepare-current-signed-resource-adapter";
            bridge.Prepare();
            stage="start-owned-isolated-desktop";
            registrations.MarkLaunched();launchAttempted=true;
            var launchStartedUtc=DateTime.UtcNow;
            pipe.StartIsolatedDiagnostic(start,isolated.Profile,image,debugDetach,mainImage,useCopiedMain);
            processMetadata=new NativeDiagnosticProcessObservation(image,launchStartedUtc,useCopiedMain?image:mainImage);
            steps.Add(originalChildControl?"original signed main and installed children started in owned isolated job":linkedChild?"original signed main desktop started in owned job with verified linked original children":"original signed main desktop started in owned job with isolated signed child image");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
            stage="private-pipe-version";
            await pipe.SendAsync("Browser.getVersion",cancellation:timeout.Token).ConfigureAwait(false);
            privatePipeResponded=true;
            steps.Add("private pipe Browser.getVersion answered");
            histogramObservation=sandboxHistogram.ObserveAsync(pipe,histogramStop.Token);
            stage="attach-empty-profile-adapter";
            await bridge.StartAsync(timeout.Token).ConfigureAwait(false);
            for(var attempt=0;attempt<55;attempt++)
            {
                foreach(var processId in pipe.DiagnosticProcessIds)observations.Add(processId);
                var targets=await pipe.SendAsync("Target.getTargets",cancellation:timeout.Token).ConfigureAwait(false);
                pages=targets.TryGetProperty("targetInfos",out var infos)?infos.EnumerateArray().Count(info=>info.GetProperty("type").GetString()=="page"):0;
                stage="observe-owned-chromium-processes";
                await processMetadata.CaptureAsync(pipe,timeout.Token).ConfigureAwait(false);
                frontendVerified=bridge.Status.FrontendAdapted;
                childPathsVerified=processMetadata.ExpectedPathsVerified;
                if(attempt==8&&!frontendVerified)
                    await bridge.LoadValidationResourcesAsync(timeout.Token).ConfigureAwait(false);
                if(requireForwardedChild)
                {
                    forwardedChild=NativeDiagnosticForwardedChild.ReadSnapshot(pipe,image,isolated.Output);
                    if(forwardedChild.ObservedAlive)
                    {
                        forwardedFirstAlive??=DateTime.UtcNow;
                        forwardedAliveSamples++;
                        forwardedStable=forwardedAliveSamples>=2&&DateTime.UtcNow-forwardedFirstAlive.Value>=TimeSpan.FromMilliseconds(200);
                    }
                    else{forwardedAliveSamples=0;forwardedFirstAlive=null;forwardedStable=false;}
                }
                if(processMetadata.DefiniteMismatchObserved)throw new UserFacingException("观察到隔离子进程的路径或保护状态与要求不符。");
                if(frontendVerified&&childPathsVerified&&(!requireForwardedChild||forwardedStable))break;
                await Task.Delay(500,timeout.Token).ConfigureAwait(false);
            }
            if(bridge.ValidationFixture is not null)File.WriteAllText(Path.Combine(isolated.Output,"native-quota-fixture.json"),JsonSerializer.Serialize(bridge.ValidationFixture));
            if(bridge.WorkValidationFixture is not null)File.WriteAllText(Path.Combine(isolated.Output,"native-work-quota-fixture.json"),JsonSerializer.Serialize(bridge.WorkValidationFixture));
            frontendVerified=bridge.Status.FrontendAdapted;
            childPathsVerified=processMetadata.ExpectedPathsVerified;
            launchRoles=NativeDiagnosticLaunchRoles.Capture(pipe,image,mainImage);
            verified=frontendVerified&&privatePipeResponded&&childPathsVerified&&(!requireForwardedChild||forwardedStable);
            if(!frontendVerified)throw new UserFacingException("空白客户端未完成两套前端适配验证。");
            if(!childPathsVerified)throw new UserFacingException(originalChildControl?"尚未实际核验原安装路径 renderer/GPU 的最终保护状态。":"尚未实际核验原目录主进程及保留签名保护的副本 renderer/GPU 进程。");
            if(requireForwardedChild&&!forwardedStable)throw new UserFacingException("尚未核验真实转发的隔离崩溃处理进程稳定存活。");
            stage="verify-owned-empty-client-rendering";
            renderingVerified=await bridge.CaptureValidationRenderingAsync(Path.Combine(isolated.Output,"native-render.png"),timeout.Token).ConfigureAwait(false);
            if(!renderingVerified)throw new IOException("Owned empty client rendering was not observed.");
            steps.Add(originalChildControl?"original browser plus original renderer/GPU observed with signature flags 5; network-service coverage remains unverified":linkedChild?"original browser plus linked original renderer/GPU observed with signature flags 5; network-service coverage remains unverified":"original browser plus copied renderer/GPU observed with signature flags 5; network-service coverage remains unverified");
            steps.Add("empty-profile adapter observed; no account, inference or remote feature was tested");
            adapterBeforeClose=bridge.Status;
            stage="close-owned-isolated-desktop";
            try{await pipe.SendAsync("Browser.close",cancellation:timeout.Token).ConfigureAwait(false);}catch{}
        }
        catch(Exception error)
        {
            failure=error is UserFacingException known?known.Message:error.GetType().Name;
            imageValidationStage=error.Data["native_child_image_stage"] as string;
            imageValidationHResult=error.Data["native_child_image_hresult"] is int hr?hr:null;
            imageValidationWin32Error=error.Data["native_child_image_win32_error"] is int native?native:null;
            imageSignatureHelperExitCode=error.Data["native_child_image_signature_helper_exit_code"] is int code?code:null;
            imageSignatureHelperStderrPresent=error.Data["native_child_image_signature_helper_stderr_present"] is bool stderr?stderr:null;
        }
        finally
        {
            histogramStop.Cancel();
            try{foreach(var processId in pipe.DiagnosticProcessIds)observations.Add(processId);}
            catch{cleanupFailures.Add("owned-process-observation-unavailable");failure??="OwnedProcessObservationUnavailable";}
            try
            {
                cleanup=!launchAttempted||pipe.CloseDiagnosticJob();
                if(!cleanup){cleanupFailures.Add("owned-job-cleanup-unconfirmed");failure??="OwnedJobCleanupUnconfirmed";}
            }
            catch{cleanup=false;cleanupFailures.Add("owned-job-cleanup-failed");failure??="OwnedJobCleanupFailed";}
            if(histogramObservation is not null)
                try{await histogramObservation.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);}
                catch{cleanupFailures.Add("histogram-observer-completion-unconfirmed");}
            if(registrations is not null)
            {
                try{await registrations.RestoreAsync(cleanup).ConfigureAwait(false);}
                catch{cleanupFailures.Add("native-browser-registration-recovery-failed");failure??="NativeBrowserRegistrationRecoveryFailed";}
            }
            try{image?.Dispose();}
            catch{cleanupFailures.Add("child-image-lock-release-failed");failure??="ChildImageLockReleaseFailed";}
            try{mainImage?.Dispose();}
            catch{cleanupFailures.Add("main-image-lock-release-failed");failure??="MainImageLockReleaseFailed";}
            var recovery=registrations?.Status;
            object? adapter=null;int? intercepted=null;
            try{adapter=adapterBeforeClose??bridge.Status;intercepted=bridge.InterceptedCount;}
            catch{cleanupFailures.Add("adapter-final-state-unavailable");failure??="AdapterFinalStateUnavailable";}
            var passed=verified&&renderingVerified&&failure is null&&cleanup&&cleanupFailures.Count==0&&recovery?.VerificationPassed==true;
            try
            {
                File.WriteAllText(Path.Combine(isolated.Output,"native-child-surface-smoke-result.json"),JsonSerializer.Serialize(new
                {
                passed,diagnostic_only=true,private_pipe=true,network_debug_port_opened=false,
                debugger_detach_requested=debugDetach,debugger_detach_status=pipe.DiagnosticDebugDetachStatus,
                main_alias_requested=mainAliasExecutable is not null,copied_main_requested=useCopiedMain,crashpad_forwarding_independently_verified=false,
                forwarded_child_required=requireForwardedChild,forwarded_child_observation=forwardedChild,
                forwarded_child_stable_alive=forwardedStable,forwarded_child_alive_samples=forwardedAliveSamples,
                private_pipe_responded=privatePipeResponded,frontend_loading_verified=frontendVerified,empty_client_rendering_verified=renderingVerified,
                child_path_mode=originalChildControl?"original-install-control":linkedChild?"linked-original":"physical-copy",
                copied_renderer_gpu_verified=!originalChildControl&&!linkedChild&&childPathsVerified,
                linked_renderer_gpu_verified=linkedChild&&childPathsVerified,
                original_renderer_gpu_verified=originalChildControl&&childPathsVerified,
                sandbox_launch_histogram=sandboxHistogram.Latest,sandbox_histogram_queries=sandboxHistogram.SuccessfulQueries,sandbox_histogram_termination=sandboxHistogram.Termination,
                saved_auth_read=false,credentials_inherited=false,model_request_executed=false,
                actual_account_switch_verified=false,remote_connection_verified=false,ordinary_icon_integration_verified=false,
                wsl_bare_name_resolution_verified=isolated.WslBareNameResolved,
                wsl_absolute_fixture_execution_verified=isolated.WslUnavailableVerified,
                registry_installation_simulated=true,registry_noop_fixture_verified=isolated.RegistryNoOpVerified,
                registry_bare_name_resolution_verified=isolated.RegistryBareNameResolved,
                registration_recovery_cas_verified=false,
                owned_loopback_deny_service=true,denied_requests=isolated.DeniedRequestCount,
                empty_model_lists=isolated.EmptyModelListCount,complete_network_isolation_claimed=false,
                system_wsl_executed=(bool?)null,system_wsl_execution_completely_observed=false,launch_attempted=launchAttempted,
                launch_error_code=pipe.DiagnosticLaunchErrorCode,owned_job_cleanup_confirmed=cleanup,
                owned_job_cleanup_error_code=pipe.DiagnosticCleanupErrorCode,
                observed_owned_process_ids=observations.Order().ToArray(),
                owned_cdp_process_metadata=processMetadata?.Samples,owned_cdp_capture_count=processMetadata?.Captures,
                owned_launch_roles=launchRoles,
                owned_cdp_transition_samples=processMetadata?.TransitionSamples,
                browser_original_path_verified=processMetadata?.BrowserVerified??false,
                renderer_expected_path_and_signature_verified=processMetadata?.RendererVerified??false,
                gpu_expected_path_and_signature_verified=processMetadata?.GpuVerified??false,
                generic_utility_observed=processMetadata?.UtilityObserved??false,
                network_service_independently_verified=false,full_chromium_process_coverage_verified=false,
                child_image_files_verified=image?.VerifiedFileCount,child_image_bytes_verified=image?.VerifiedBytes,
                pages,intercepted,adapter,recovery,cleanup_failures=cleanupFailures,
                failure_stage=failure is null?null:stage,failure,steps,
                child_image_validation_stage=imageValidationStage,child_image_validation_hresult=imageValidationHResult,
                child_image_validation_win32_error=imageValidationWin32Error,child_image_signature_helper_exit_code=imageSignatureHelperExitCode,
                child_image_signature_helper_stderr_present=imageSignatureHelperStderrPresent
                },new JsonSerializerOptions{WriteIndented=true}));
                reportWritten=true;
            }
            catch{failure??="DiagnosticResultWriteFailed";}
        }
        if(!verified||failure is not null||!cleanup||!reportWritten||cleanupFailures.Count!=0||registrations?.Status.VerificationPassed!=true)
            throw new UserFacingException("隔离客户端诊断未通过；请查看本次脱敏结果和恢复状态。");
    }
}
