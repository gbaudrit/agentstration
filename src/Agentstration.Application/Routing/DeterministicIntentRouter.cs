using Agentstration.Domain;

namespace Agentstration.Application.Routing;

public sealed class DeterministicIntentRouter : IIntentRouter
{
    private static readonly RoutingDecision ProcessingDecision = new("content-processing", false, "All accepted MVP content is normalized and indexed.");

    public ValueTask<RoutingDecision> RouteAsync(RoutingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProcessingDecision);
    }
}
