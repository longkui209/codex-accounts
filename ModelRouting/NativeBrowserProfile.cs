using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.ModelRouting;

internal static class NativeBrowserProfile
{
    internal static string Resolve(string roaming,string? configured,Func<string,bool>? exists=null)
    {
        exists??=Directory.Exists;
        if(!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath=AuthFileSafety.NormalizeDirectory(configured);
            if(!exists(explicitPath))throw Missing();return explicitPath;
        }
        // Owl's appData lives under Codex/web; classic Electron uses the outer Codex directory.
        // Reuse an existing Chromium profile. Never create a new profile to make the pipe start.
        var roots=new[]{Path.Combine(roaming,"Codex","web","Codex"),Path.Combine(roaming,"Codex")};
        var matching=roots.Where(root=>exists(Path.Combine(root,"Default"))).ToArray();
        if(matching.Length!=1)throw Missing();return matching[0];
    }
    private static UserFacingException Missing()=>new("无法唯一确认原生浏览器的既有配置目录，未创建替代配置或更换登录。请保留普通启动入口并检查安装版本。");
}
