using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Aep.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed record AepDataSourceProfileBundleInstallResult(
    string BundleId,
    string Version,
    IReadOnlyList<BootstrapResourcePlanDetail> Resources);

public sealed class AepDataSourceProfileBundleInstaller(IEnumerable<IBootstrapResourceHandler> handlers)
{
    public async Task<AepDataSourceProfileBundleInstallResult> InstallAsync(
        AepDataSourceProfileBundleContribution bundle,
        BootstrapApplicationTarget target,
        IReadOnlyDictionary<string, ResourceReference> toolBindings,
        CancellationToken cancellationToken)
    {
        if (target.WorkspaceId is null)
            throw new InvalidOperationException("A Data Source Profile bundle requires a Workspace target.");
        var requiredTools = bundle.RequiredTools ?? [];
        var missing = requiredTools.Where(value => !toolBindings.ContainsKey(value)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Data Source Profile bundle '{bundle.Id}' requires Tool bindings: {string.Join(", ", missing)}.");

        var documents = bundle.ResourceManifests.Select(value => Resolve(value, toolBindings)).ToArray();
        if (!documents.Any(value => string.Equals(value.Kind, "DataSourceProfile", StringComparison.Ordinal)))
            throw new InvalidOperationException($"Data Source Profile bundle '{bundle.Id}' does not declare a DataSourceProfile resource.");

        var operation = new BootstrapResourceOperationContext(
            $"aep:{bundle.Id}:{bundle.Version}",
            $"aep://{bundle.Id}/{bundle.Version}",
            BootstrapProfileScope.Workspace,
            target);
        var planning = new BootstrapPlanningContext();
        var planned = new List<(BootstrapResourceDocument Document, IBootstrapResourceHandler Handler, BootstrapResourcePlanResult Plan)>();
        foreach (var document in documents)
        {
            var handler = handlers.SingleOrDefault(value => string.Equals(value.Kind, document.Kind, StringComparison.Ordinal)
                && value.SupportsProfileScope(BootstrapProfileScope.Workspace))
                ?? throw new InvalidOperationException($"AEP bundle resource kind '{document.Kind}' is not supported.");
            var plan = await handler.PlanAsync(document, operation, planning, cancellationToken);
            if (plan.Disposition is BootstrapResourceDisposition.Invalid or BootstrapResourceDisposition.Conflict or BootstrapResourceDisposition.Failed)
                throw new InvalidOperationException($"AEP bundle resource '{document.Kind}/{document.Metadata.Name}' cannot be installed ({plan.Disposition}).");
            planned.Add((document, handler, plan));
        }

        var results = new List<BootstrapResourcePlanDetail>(planned.Count);
        foreach (var item in planned)
        {
            var disposition = item.Plan.Disposition;
            if (disposition == BootstrapResourceDisposition.Create)
            {
                var applied = await item.Handler.ApplyAsync(item.Document, operation, cancellationToken);
                disposition = applied switch
                {
                    BootstrapResourceApplyResult.Created => BootstrapResourceDisposition.Create,
                    BootstrapResourceApplyResult.Skipped => BootstrapResourceDisposition.Skip,
                    _ => BootstrapResourceDisposition.Conflict
                };
            }
            results.Add(new(item.Document.Kind, item.Document.Metadata.Name, disposition));
        }
        return new(bundle.Id, bundle.Version, results);
    }

    private static BootstrapResourceDocument Resolve(
        string manifest,
        IReadOnlyDictionary<string, ResourceReference> toolBindings)
    {
        var element = ResourceManifestSerializer.FromYaml<JsonElement>(manifest);
        var node = JsonNode.Parse(element.GetRawText())
            ?? throw new InvalidOperationException("The AEP bundle contains an empty resource manifest.");
        ResolveBindings(node, toolBindings);
        return ResourceManifestSerializer.FromJsonStrict<BootstrapResourceDocument>(node.ToJsonString());
    }

    private static void ResolveBindings(JsonNode? node, IReadOnlyDictionary<string, ResourceReference> bindings)
    {
        if (node is JsonObject value)
        {
            if (value.Count == 1 && value["binding"] is JsonValue bindingValue
                && bindingValue.TryGetValue<string>(out var name))
            {
                if (!bindings.TryGetValue(name, out var reference))
                    throw new InvalidOperationException($"AEP bundle Tool binding '{name}' was not supplied.");
                value.Clear();
                foreach (var property in JsonSerializer.SerializeToNode(reference)!.AsObject())
                    value[property.Key] = property.Value?.DeepClone();
                return;
            }
            foreach (var property in value.ToArray()) ResolveBindings(property.Value, bindings);
            return;
        }
        if (node is JsonArray array)
            foreach (var item in array) ResolveBindings(item, bindings);
    }
}
