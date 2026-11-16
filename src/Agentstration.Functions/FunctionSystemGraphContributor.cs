using System.Runtime.CompilerServices;
using Agentstration.Functions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Functions;

public sealed class FunctionSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities) : ISystemGraphContributor
{
    public string Family => "functions";
    public SystemGraphContributorCoverage Coverage => new(Family, ["function-kind-and-transformation-versions"], ["opaque-native-implementation-internals"]);
    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<FunctionKindResource>(resources, scope, FunctionResourceKinds.FunctionKind, ct))
        {
            var value = stored.Value;
            yield return new([ResourceSystemGraph.Node(value)], []);
            foreach (var version in value.Definition.Versions)
            {
                var node = ResourceSystemGraph.Publication(value, value.Uid, value.Kind, value.Name, version.Version,
                    FunctionDigests.Compute(version), hasNativeUid: false);
                yield return new([node], [new(SystemGraphIds.Definition(value.Uid), node.Id, "publication")]);
            }
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<TransformationFunctionResource>(resources, scope, FunctionResourceKinds.TransformationFunction, ct))
        {
            var value = stored.Value;
            yield return new([ResourceSystemGraph.Node(value)], []);
            foreach (var version in value.Definition.Versions)
            {
                var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(value, value.Uid, value.Kind, value.Name,
                    version.Version, FunctionDigests.Compute(version), hasNativeUid: false), identities);
                builder.PublicationOf(value.Uid);
                if (version.FunctionKind.Uid is { } uid) await builder.PinAsync(uid, "versions.functionKind", ct, version: version.FunctionKind.Version);
                yield return builder.Build();
            }
        }
    }
}
