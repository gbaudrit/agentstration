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
    public UiStatus Status { get; private set; } = UiStatus.Neutral;
    public PlatformStatusKind Kind { get; private set; } = PlatformStatusKind.Connecting;
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await provider.GetStatusAsync(cancellationToken);
            Set(result.Status, result.Kind);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Platform status refresh is unavailable");
            Set(UiStatus.Danger, PlatformStatusKind.Unavailable);
        }
    }

    private void Set(UiStatus status, PlatformStatusKind kind)
    {
        Status = status;
        Kind = kind;
        Changed?.Invoke();
    }
}

internal sealed class EmptyPlatformStatusProvider : IPlatformStatusProvider
{
    public Task<PlatformStatusResult> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PlatformStatusResult(UiStatus.Danger, PlatformStatusKind.Unavailable));
}

