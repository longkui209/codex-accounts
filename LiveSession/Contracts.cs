using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.LiveSession;

public sealed record LiveStatus(bool Enabled, bool Connected, bool Busy, string? CurrentAccountId, string Message)
{
    public string VerificationState { get; init; } = "unverified";
    public string? LastCompletedAccountId { get; init; }
    public bool NativeConnectionObserved { get; init; }
    public RoutingFailure? Failure { get; init; }
    public RoutingResponseEvidence? LastResponse { get; init; }
    public bool RemovalSupported { get; init; }
    public bool AutoRotationSupported { get; init; }
    public bool AutoRotationEnabled { get; init; }
    public AutoRotationEvent? LastAutoRotation { get; init; }
    public CodexAccountSwitcher.ModelRouting.NativeInputStatus? NativeInput {get;init;}
}
public sealed record AutoRotationEvent(string FromAccountId,string ToAccountId,string State,DateTimeOffset OccurredAtUtc);
public sealed record RoutingResponseEvidence(string Format, string Encoding, int HttpStatus, bool Completed, DateTimeOffset ObservedAtUtc);
public sealed record RoutingFailure(string Code, string Message, int? HttpStatus, DateTimeOffset OccurredAtUtc);
public sealed record LiveDescriptor(string InstanceId, int OwnerPid, long OwnerStartTicks, string ControlPipe, string DesktopPipe, string CliVersion);

// Tokens remain inside the host process and its authenticated backend transport.
public sealed class LiveCredential(string accountId, string accessToken, string plan, AccountInfo info)
{
    public string AccountId { get; } = accountId;
    public string AccessToken { get; } = accessToken;
    public string Plan { get; } = plan;
    public AccountInfo Info { get; } = info;
}

public interface ILiveCredentials
{
    Task<LiveCredential> GetAsync(string profileId, bool forceRefresh, CancellationToken cancellation);
}

public interface ILiveSwitchController
{
    Task<LiveStatus> StatusAsync(CancellationToken cancellation);
    Task<LiveStatus> SwitchAsync(string profileId, CancellationToken cancellation);
    Task<LiveCredential> RefreshAsync(string? previousAccountId, CancellationToken cancellation);
}
