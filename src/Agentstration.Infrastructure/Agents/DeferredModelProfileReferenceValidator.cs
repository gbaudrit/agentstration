using Agentstration.Agents;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Agents;

public sealed class DeferredModelProfileReferenceValidator : IModelProfileReferenceValidator
{
    public Task ValidateAsync(ResourceReference profileReference, ResourceNamespace ownerNamespace, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
