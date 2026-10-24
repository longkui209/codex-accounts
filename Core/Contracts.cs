namespace CodexAccountSwitcher.Core;

public sealed record AccountSummary(
    string Id,
    string Label,
    string MaskedEmail,
    string Plan,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record AccountInfo(
    string IdentityKey,
    string MaskedEmail,
    string Plan,
    DateTimeOffset? ExpiresAtUtc);

public sealed record RemovedAccountSummary(string RemovalId, string AccountId, string Label, string MaskedEmail,
    DateTimeOffset RemovedAtUtc, bool Restored = false);

public sealed record RuntimeStatus(
    bool CanSwitch,
    string Message,
    IReadOnlyList<string> BlockingProcesses);

public sealed class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message) { }
}
