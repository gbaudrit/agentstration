using Agentstration.Extensions.Aep;

namespace Agentstration.Web.Hosting;

internal sealed class AepAutomaticEnrollmentWorker(AepAutomaticEnrollmentService enrollment, TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await enrollment.ProcessPendingAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
    }
}
