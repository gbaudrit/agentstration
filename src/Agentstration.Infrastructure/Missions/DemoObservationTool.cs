using Agentstration.Application;
using Agentstration.Domain;

namespace Agentstration.Infrastructure.Missions;

public sealed class DemoObservationTool : IObservationTool
{
    private static readonly decimal[] Values = [349m, 319m, 299m, 289m, 305m];

    public Task<decimal> ObserveAsync(Mission mission, int priorRunCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mission.Source.Scheme != "demo") throw new InvalidOperationException("The MVP observation tool only permits demo:// sources.");
        return Task.FromResult(Values[priorRunCount % Values.Length]);
    }
}
