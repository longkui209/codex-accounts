namespace CodexAccountSwitcher.Core;

/// <summary>Optional display claims returned at login. They are neither live profile data nor authentication proof.</summary>
public sealed record AccountProfile(string? DisplayName,string? Username,string? AvatarUrl)
{
    public static readonly string[] AvatarOrigins =
    ["https://cdn.openai.com","https://images.openai.com","https://files.oaiusercontent.com","https://cdn.auth0.com",
     "https://lh3.googleusercontent.com","https://lh4.googleusercontent.com","https://lh5.googleusercontent.com","https://lh6.googleusercontent.com"];
    public bool HasDetails=>DisplayName is not null || Username is not null || AvatarUrl is not null;
    internal static string? SafeAvatar(string? value)
    {
        if(value is null || value.Length>2048 || value.Any(char.IsControl) || !Uri.TryCreate(value,UriKind.Absolute,out var uri) ||
            uri.Scheme!="https" || uri.UserInfo.Length!=0 || !uri.IsDefaultPort || uri.Fragment.Length!=0 ||
            !AvatarOrigins.Contains(uri.GetLeftPart(UriPartial.Authority),StringComparer.OrdinalIgnoreCase))return null;
        return uri.AbsoluteUri;
    }
    public override string ToString()=>"账号展示资料（不写入日志）";
}
