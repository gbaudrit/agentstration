using System.Runtime.CompilerServices;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;

namespace Agentstration.Systems;

public sealed class SystemModelGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities) : ISystemGraphContributor
{
    public string Family => "authored-systems";
    public SystemGraphContributorCoverage Coverage => new(Family, ["authored-resource-membership"], ["membership-is-not-an-executable-dependency"]);
    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<SystemResource>(resources, scope, SystemResourceKinds.System, ct))
        {
            var value = stored.Value;
            var node = ResourceSystemGraph.Node(value) with { DisplayName = value.Definition.DisplayName };
            var relations = new List<SystemGraphRelation>();
            foreach (var member in value.Definition.Members)
            {
                var current = await identities.GetIdentityByUidAsync(member.Uid, ct);
                if (current is not null && current.ScopeRef == member.ScopeRef && current.Namespace == member.Namespace && current.Kind == member.Kind && current.Name == member.Name)
                    relations.Add(new(node.Id, SystemGraphIds.Definition(member.Uid), "system-membership", "definition.members", "authored-uid"));
            }
            yield return new([node], relations);
        }
    }
}
