using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

internal static class WindowsStartupIntegrationSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        void Check(bool condition){if(!condition)throw new UserFacingException("整机启动隔离检查未通过。");}
        Check(WindowsStartupIntegration.ShouldEnableRouting(true,false,true,true));
        Check(!WindowsStartupIntegration.ShouldEnableRouting(true,false,false,true));
        Check(!WindowsStartupIntegration.ShouldEnableRouting(true,false,true,false));
        Check(!WindowsStartupIntegration.ShouldEnableRouting(false,false,true,true));
        Check(WindowsStartupIntegration.ShouldEnableRouting(false,true,true,true));
        var current=@"C:\synthetic\OpenAI.Codex_2\app\ChatGPT.exe";
        Check(WindowsStartupIntegration.MatchesCurrentInstallation(current.ToUpperInvariant(),current));
        Check(!WindowsStartupIntegration.MatchesCurrentInstallation(@"C:\synthetic\OpenAI.Codex_1\app\ChatGPT.exe",current));
        Check(!WindowsStartupIntegration.MatchesCurrentInstallation(null,current));
        Check(!WindowsStartupIntegration.MatchesCurrentInstallation("ChatGPT.exe",current));
        Check(WindowsStartupIntegration.VersionFilterName(current)==WindowsStartupIntegration.VersionFilterName(current.ToUpperInvariant()));
        Check(WindowsStartupIntegration.VersionFilterName(current)!=WindowsStartupIntegration.VersionFilterName(@"C:\synthetic\OpenAI.Codex_3\app\ChatGPT.exe"));
        var monitor=Path.Combine(WindowsStartupIntegration.InstallationRoot,"monitor",new string('a',32),"CodexAccountSwitcher.exe");
        Check(StartupVersionService.IsOwnedCommand('"'+monitor+"\" --startup-version-service"));
        Check(!StartupVersionService.IsOwnedCommand(monitor+" --startup-version-service"));
        Check(!StartupVersionService.IsOwnedCommand('"'+monitor+"\" --mcp"));
        Check(!StartupVersionService.IsOwnedCommand('"'+monitor.Replace("monitor","unrelated",StringComparison.Ordinal)+"\" --startup-version-service"));
        var apps=@"C:\Program Files\WindowsApps";
        var image=Path.Combine(apps,@"OpenAI.Codex_26.1000.1.0_x64__2p2nqsd0c76g0\app\resources\codex-windows-sandbox-service.exe");
        Check(OfficialSandboxService.MetadataMatches(OfficialSandboxService.ServiceName,"LocalSystem",4,123,123,image,image,apps));
        Check(!OfficialSandboxService.MetadataMatches("other","LocalSystem",4,123,123,image,image,apps));
        Check(!OfficialSandboxService.MetadataMatches(OfficialSandboxService.ServiceName,"User",4,123,123,image,image,apps));
        Check(!OfficialSandboxService.MetadataMatches(OfficialSandboxService.ServiceName,"LocalSystem",1,123,123,image,image,apps));
        Check(!OfficialSandboxService.MetadataMatches(OfficialSandboxService.ServiceName,"LocalSystem",4,123,124,image,image,apps));
        Check(!OfficialSandboxService.MetadataMatches(OfficialSandboxService.ServiceName,"LocalSystem",4,123,123,image,image+".other",apps));
        Check(!OfficialSandboxService.MetadataMatches(OfficialSandboxService.ServiceName,"LocalSystem",4,123,123,@"C:\synthetic\codex-windows-sandbox-service.exe",@"C:\synthetic\codex-windows-sandbox-service.exe",apps));
        return new[]{"PASS: 整机启动对已有插件和个人账号库的用户启用各自路由，不因系统安装为其他用户创建账号库或共享凭据。",
            "PASS: 旧启动规则不能将已更新安装报告为已接入；路径大小写兼容，缺失及相对路径拒绝。",
            "PASS: 常驻沙箱服务只在 SCM 名称、系统身份、PID、受保护官方路径及运行状态匹配时排除；同名程序不能直接放行。"};
    }
}
