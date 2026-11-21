using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Bootstrap;

namespace Agentstration.Web.Hosting;

internal static class BootstrapStartupRetry
{
    // Runs inside the existing initialization lease. Replays use the existing
    // create-only handlers, never replace already applied resources, and release
    // deferred workers only once initialization finally completes.
    internal static async Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var startup = services.GetRequiredService<BootstrapStartupState>();
        var corrections = services.GetRequiredService<BootstrapStartupModelOverrideStore>();
        while (true)
        {
            try
            {
                await using var execution = services.CreateAsyncScope();
                await execution.ServiceProvider.GetRequiredService<BootstrapStartupCoordinator>().ExecuteAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!await corrections.HasCorrectableModelAsync(cancellationToken)) throw;
                startup.Fail("bootstrap_model_intervention_required");
                var attempt = startup.Read().AttemptId;
                await corrections.WaitForResumeAsync(attempt, cancellationToken);
                startup.BeginRetry();
            }
        }
    }
}
