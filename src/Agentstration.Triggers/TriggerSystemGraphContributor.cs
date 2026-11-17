using System.Runtime.CompilerServices;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Triggers;

public sealed class TriggerSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities) : ISystemGraphContributor
{
    public string Family => "triggers";
    public SystemGraphContributorCoverage Coverage => new(Family, ["flow-uid-publication-targets"], ["entry-family-without-stable-resource-uids"]);
    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<TriggerResource>(resources, scope, TriggerResourceKinds.Trigger, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(value), identities);
            if (value.Definition.Target.Flow is { Uid: { } uid, Version: { } version })
                await builder.PinAsync(uid, "definition.target.flow", ct, version: version);
            yield return builder.Build();
        }
    }
}
