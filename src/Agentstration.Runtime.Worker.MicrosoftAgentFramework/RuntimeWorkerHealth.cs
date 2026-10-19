using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class RuntimeWorkerReadiness
{
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    public void MarkReady() => Interlocked.Exchange(ref ready, 1);
    public void MarkNotReady() => Interlocked.Exchange(ref ready, 0);
}

internal sealed class RuntimeWorkerLivenessCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        Task.FromResult(HealthCheckResult.Healthy("The Runtime Worker process is alive."));
}

internal sealed class RuntimeWorkerReadinessCheck(RuntimeWorkerReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy("The Runtime Worker is registered and ready to claim work.")
            : HealthCheckResult.Unhealthy("The Runtime Worker is not registered."));
}
