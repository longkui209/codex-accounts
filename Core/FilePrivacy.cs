using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexAccountSwitcher.Core;

public static class FilePrivacy
{
    public static void ApplyDirectory(string directory)
    {
        var info = new DirectoryInfo(Path.GetFullPath(directory));
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UserFacingException("账号库目录是链接，请选择本机普通目录。");
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new UserFacingException("无法确定 Windows 用户身份。");
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public static void ApplyFile(string file)
    {
        var info = new FileInfo(Path.GetFullPath(file));
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UserFacingException("账号文件是链接，操作已取消。");
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new UserFacingException("无法确定 Windows 用户身份。");
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
