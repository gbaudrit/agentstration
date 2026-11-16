using System.Runtime.CompilerServices;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Models;

public sealed class ModelSystemGraphContributor(IResourceStore resources) : ISystemGraphContributor
{
    public string Family => "models-and-runtime-profiles";

    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ModelProfileResource>(resources, scope, ModelResourceKinds.ModelProfile, cancellationToken))
            yield return new([ResourceSystemGraph.Node(stored.Value)], []);
        await foreach (var stored in ResourceSystemGraph.ReadAsync<RuntimeProfileResource>(resources, scope, RuntimeProfileResourceKinds.RuntimeProfile, cancellationToken))
            yield return new([ResourceSystemGraph.Node(stored.Value)], []);
    }
}
