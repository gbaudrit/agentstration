using Agentstration.Web.Components.Models;
using Microsoft.Extensions.Logging;

namespace Agentstration.Web.Components.State;

public enum PlatformStatusKind
{
    Connecting,
    Operational,
    AttentionRequired,
    PartiallyUnavailable,
    NoActiveDeployments,
    Unavailable
}

public sealed record PlatformStatusResult(UiStatus Status, PlatformStatusKind Kind);

public interface IPlatformStatusProvider
{
    Task<PlatformStatusResult> GetStatusAsync(CancellationToken cancellationToken);
}

public sealed class PlatformStatusState(
    IPlatformStatusProvider provider,
    ILogger<PlatformStatusState> logger)
{
    private readonly object refreshLock = new();
    private long refreshRevision;

    public UiStatus Status { get; private set; } = UiStatus.Neutral;
    public PlatformStatusKind Kind { get; private set; } = PlatformStatusKind.Connecting;
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        long revision;
        lock (refreshLock) revision = ++refreshRevision;
        Set(revision, UiStatus.Neutral, PlatformStatusKind.Connecting);
        try
        {
            var result = await provider.GetStatusAsync(cancellationToken);
            Set(revision, result.Status, result.Kind);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Platform status refresh is unavailable");
            Set(revision, UiStatus.Danger, PlatformStatusKind.Unavailable);
        }
    }

    private void Set(long revision, UiStatus status, PlatformStatusKind kind)
    {
        lock (refreshLock)
        {
            if (revision != refreshRevision) return;
            Status = status;
            Kind = kind;
        }
        Changed?.Invoke();
    }
}

internal sealed class EmptyPlatformStatusProvider : IPlatformStatusProvider
{
    public Task<PlatformStatusResult> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PlatformStatusResult(UiStatus.Danger, PlatformStatusKind.Unavailable));
}

