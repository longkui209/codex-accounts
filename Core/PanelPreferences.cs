using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.Core;

internal sealed class PanelPreferences(VaultStore vault)
{
    private sealed record Preference(string Language);
    private readonly PrivateJobStore<Preference> store=new(Path.Combine(vault.DirectoryPath,"preferences"),"panel.dpapi");
    internal static readonly string[] Languages=["zh-CN","zh-TW","en","ja","ko","es","fr","de","pt-BR","ru"];
    internal string? ReadLanguage()
    {
        try{return store.Locked(record=>((Preference?)null,record is not null&&Languages.Contains(record.Language,StringComparer.Ordinal)?record.Language:null));}
        catch(UserFacingException){return null;} // A display preference must never block account management.
    }
    internal string SetLanguage(string language)
    {
        if(!Languages.Contains(language,StringComparer.Ordinal))throw new UserFacingException("Unsupported display language.");
        return store.Locked(_=>(new Preference(language),language));
    }
}
