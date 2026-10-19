using Agentstration.Work;

namespace Agentstration.Application.Work;

public sealed record EntryExecutionResolution(
    EntryExecutionAvailability Availability,
    string? ReasonCode = null)
{
    public bool CanInvoke => Availability == EntryExecutionAvailability.Executable;

    public static EntryExecutionResolution Executable { get; } = new(EntryExecutionAvailability.Executable);
}

public interface IEntryExecutionResolver
{
    Task<EntryExecutionResolution> ResolveAsync(
        EntryResource entry,
        CancellationToken cancellationToken);
}
