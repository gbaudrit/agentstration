using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed partial class BootstrapResourcePreparation
{
    private async Task<bool> PrepareProjectConnectionsAsync(JsonObject root, BootstrapResourceDocument document,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, bool applying,
        List<string> fingerprints, CancellationToken token)
    {
        if (root["connectionAssignments"] is not null and not JsonArray || root["connectionRequirements"] is not null and not JsonArray)
            throw new InvalidOperationException("bootstrap_project_connections_invalid");
        var needs = root["connectionRequirements"]?.Deserialize<WorkProjectConnectionRequirement[]>(Json) ?? [];
        var assignments = root["connectionAssignments"] as JsonArray ?? [];
        if (needs.Length == 0 && assignments.Count == 0) return false;
        if (operation.ProfileScope != BootstrapProfileScope.Workspace || assignments.Count > 32 || needs.Length > 32)
            throw new InvalidOperationException("bootstrap_project_connections_invalid");
        var resolved = new List<WorkProjectConnectionAssignment>(); var deferred = false;
        foreach (var item in assignments)
        {
            if (item is not JsonObject assignment || assignment.Any(property => property.Key is not ("name" or "service" or "describeTool" or "exports"))) throw new InvalidOperationException("bootstrap_project_connections_invalid");
            var service = await PinAsync(assignment["service"], ConnectedServiceResourceKinds.ConnectedService);
            var adapter = await PinAsync(assignment["describeTool"], ToolResourceKinds.Tool);
            if (service is null || adapter is null) { deferred = true; continue; }
            resolved.Add(new(Text(assignment, "name"), service, adapter,
                assignment["exports"]?.Deserialize<WorkProjectConnectionExport[]>(Json) ?? []));
        }
        var definition = new WorkProjectDefinition(document.Metadata.Name, null, [], [])
        { ConnectionRequirements = needs, ConnectionAssignments = resolved };
        var valid = definition.Validate();
        if (!valid.IsSuccess) throw new InvalidOperationException("bootstrap_" + valid.ErrorCode);
        if (deferred) return true;
        var observed = await projectContexts.ValidateAsync(new(operation.Target!.WorkspaceId!.Value), valid.Value!, token);
        if (!observed.IsSuccess) throw new InvalidOperationException("bootstrap_" + observed.ErrorCode);
        root["connectionRequirements"] = Node(valid.Value!.ConnectionRequirements);
        root["connectionAssignments"] = Node(valid.Value.ConnectionAssignments);
        fingerprints.Add("project-connections:" + WorkPlanDefinition.Digest(new { valid.Value.ConnectionRequirements, Bindings = observed.Value }));
        return false;

        async Task<WorkProjectResourceReference?> PinAsync(JsonNode? value, string kind)
        {
            var reference = Selector(value) ?? value as JsonObject ?? throw new InvalidOperationException("bootstrap_reference_invalid");
            if (reference.Any(property => property.Key is not ("name" or "namespace" or "scopeRef" or "kind")))
                throw new InvalidOperationException("bootstrap_reference_invalid");
            if (reference["kind"] is { } supplied && supplied.GetValue<string>() != kind)
                throw new InvalidOperationException("bootstrap_reference_kind_invalid");
            var scope = reference["scopeRef"]?.Deserialize<ResourceScopeRef>(Json) ?? ResourceScopeRef.Workspace(operation.Target!.WorkspaceId!.Value);
            var ns = reference["namespace"]?.Deserialize<ResourceNamespace>(Json) ?? document.Metadata.Namespace;
            var address = new ScopedResourceAddress(scope, ns, kind, Text(reference, "name"));
            Resource? resource = kind == ConnectedServiceResourceKinds.ConnectedService
                ? (await services.GetExactAsync(scope, ns, address.Name, token))?.Value
                : (await resources.GetExactAsync<ToolResource>(address, token))?.Value;
            if (resource is null)
            {
                if (!applying && scope == ResourceScopeRef.Workspace(operation.Target!.WorkspaceId!.Value)
                    && planning.Contains(kind, address.Name, ns.Value)) return null;
                throw Missing(kind, address.Name);
            }
            return new(address, resource.Uid, resource.Generation);
        }
    }
}
