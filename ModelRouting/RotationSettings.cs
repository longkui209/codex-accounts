using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.ModelRouting;

public sealed record RotationSettings(bool Enabled, DateTimeOffset UpdatedAtUtc);
public sealed class RotationSettingsStore(VaultStore vault)
{
    private PrivateJobStore<RotationSettings> Store() => new(Path.Combine(vault.DirectoryPath,"routing"),"auto-rotation.dpapi");
    public RotationSettings Read()
    {
        try { return Store().Locked(value => ((RotationSettings?)null,value ?? new(false,DateTimeOffset.MinValue))); }
        catch { return new(false,DateTimeOffset.MinValue); }
    }
    public void Set(bool enabled) => Store().Locked(_ => (new RotationSettings(enabled,DateTimeOffset.UtcNow),true));
}
