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
        return new[]{"PASS: 整机启动对已有插件和个人账号库的用户启用各自路由，不因系统安装为其他用户创建账号库或共享凭据。"};
    }
}
