using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Agents;

public sealed class DeferredModelProfileReferenceValidator : IModelProfileReferenceValidator
{
    public Task ValidateAsync(ResourceReference profileReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
