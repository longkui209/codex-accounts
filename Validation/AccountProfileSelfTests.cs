using System.Security.Cryptography;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.Validation;

public static class AccountProfileSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        using(var profile=Snapshot(new {name="Synthetic display",preferred_username="synthetic-user",picture="https://cdn.openai.com/synthetic-avatar.png"}))
            Require(profile.Profile.DisplayName=="Synthetic display" && profile.Profile.Username=="synthetic-user" && profile.Profile.AvatarUrl is not null,"returned display claims");
        using(var missing=Snapshot(new {email="synthetic@example.test"}))
            Require(!missing.Profile.HasDetails,"missing is absent, email and sub are not invented usernames");
        using(var nested=Snapshot(new Dictionary<string,object>{["https://api.openai.com/profile"]=new {name="Synthetic nested",username="synthetic-nested"}}))
            Require(nested.Profile.DisplayName=="Synthetic nested" && nested.Profile.Username=="synthetic-nested","namespaced claims");
        using(var invalid=Snapshot(new {name="invalid\nname",preferred_username=new[]{"invalid"},picture="file:///C:/private"}))
            Require(!invalid.Profile.HasDetails,"bad optional profile claims cannot invalidate an otherwise usable login");
        foreach(var url in new[]{"http://cdn.openai.com/photo.png","https://cdn.openai.com.evil.test/photo.png","https://user:pass@cdn.openai.com/photo.png",
            "https://cdn.openai.com:8443/photo.png","https://127.0.0.1/photo.png","https://cdn.openai.com/photo.png#fragment","data:image/svg+xml,<svg/>"})
            Require(AccountProfile.SafeAvatar(url) is null,"unsafe avatar URL");
        return ["PASS: 仅显示登录返回的可选名称、用户名和头像；缺失不推断，坏的展示字段不破坏登录解析。",
            "PASS: 头像仅接受固定 HTTPS 图像站点，拒绝凭据、伪造后缀、本机地址、端口及脚本/文件地址。"];
    }
    private static AuthSnapshot Snapshot(object profile)
    {
        var values=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(profile))!;
        var claims=new Dictionary<string,object>{["sub"]="synthetic-subject",["exp"]=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()};
        foreach(var pair in values)claims[pair.Key]=pair.Value;
        var payload=JsonSerializer.SerializeToUtf8Bytes(claims);byte[]? bytes=null;
        try{
            var jwt="eyJhbGciOiJSUzI1NiJ9."+Convert.ToBase64String(payload).TrimEnd('=').Replace('+','-').Replace('/','_')+".syntheticSignature";
            bytes=JsonSerializer.SerializeToUtf8Bytes(new{auth_mode="chatgpt",tokens=new{account_id="synthetic-account",id_token=jwt,access_token=jwt,refresh_token="synthetic-only"}});
            return AuthSnapshot.FromBytes(bytes);
        }finally{CryptographicOperations.ZeroMemory(payload);if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}
    }
    private static void Require(bool value,string message)
    {if(!value)throw new UserFacingException("合成账号展示资料验证失败："+message+"。");}
}
