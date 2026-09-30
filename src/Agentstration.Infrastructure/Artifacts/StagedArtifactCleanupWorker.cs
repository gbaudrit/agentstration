using Agentstration.Artifacts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Artifacts;

public sealed record StagedArtifactCleanupOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumPerPass { get; init; } = 100;
}

public sealed partial class StagedArtifactCleanupWorker(
    ArtifactManagementService artifacts,
    StagedArtifactCleanupOptions options,
    TimeProvider timeProvider,
    ILogger<StagedArtifactCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Interval < TimeSpan.FromSeconds(10) || options.MaximumPerPass is < 1 or > 500)
            throw new InvalidOperationException("Staged Artifact cleanup options are outside supported bounds.");
        using var timer = new PeriodicTimer(options.Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var purged = await artifacts.ExpireAndPurgeAsync(options.MaximumPerPass, stoppingToken);
                if (purged > 0) CleanupCompleted(logger, purged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                CleanupFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "Purged {ArtifactCount} expired staged Artifacts.")]
    private static partial void CleanupCompleted(ILogger logger, int artifactCount);

    [LoggerMessage(LogLevel.Error, "Staged Artifact expiration pass failed and will be retried.")]
    private static partial void CleanupFailed(ILogger logger, Exception exception);
}
