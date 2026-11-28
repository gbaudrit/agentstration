using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tools;

public sealed partial class AepEnrollmentToolProvisioner
{
    private async Task ProvisionToolSetsAsync(ExtensionRegistrationResource registration, AepManifest? manifest,
        ResourceScopeRef scope, ResourceGovernance authority, IReadOnlyList<StoredResource<ToolResource>> materialized,
        CancellationToken cancellationToken)
    {
        foreach (var group in manifest?.Contributions.ToolSets ?? [])
        {
            var name = registration.Name + "." + group.Id;
            if (name.Length > 128) name = name[..96] + "." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..24];
            var members = new List<ToolSetMember>();
            foreach (var member in group.Members)
            {
                var tool = materialized.SingleOrDefault(value => value.Value.Definition.ExternalId == member.ToolId)?.Value
                    ?? throw new ToolResourceValidationException($"ToolSet '{group.Id}' references an unmaterialized Tool contribution '{member.ToolId}'.");
                members.Add(new() { Tool = new(tool.Name, scope, tool.Namespace), Capability = member.Capability, Route = member.Route });
            }
            var existing = await toolSets.GetExactAsync(scope, registration.Namespace, name, cancellationToken);
            if (existing is not null && (existing.Value.Governance.ManagedBy?.Kind != authority.ManagedBy?.Kind
                || existing.Value.Governance.ManagedBy?.Reference != authority.ManagedBy?.Reference
                || existing.Value.Governance.Origin.Kind != authority.Origin.Kind
                || existing.Value.Governance.Origin.Reference != authority.Origin.Reference))
                throw new ToolResourceValidationException($"ToolSet '{name}' is already owned by another authority.");
            var definition = new ToolSetProperties
            {
                DisplayName = group.DisplayName, Description = group.Description, Version = group.Version,
                Enabled = existing?.Value.Definition.Enabled ?? true, Publish = false, Members = members
            };
            if (existing is null)
                _ = await toolSets.CreateAsync(new()
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolSet, ScopeRef = scope,
                    Metadata = new() { Name = name, Namespace = registration.Namespace }, Definition = definition
                }, cancellationToken);
            else if (JsonSerializer.Serialize(existing.Value.Definition) != JsonSerializer.Serialize(definition))
                _ = await toolSets.PutExactAsync(scope, registration.Namespace, name, definition, existing.ETag, cancellationToken);
        }
    }
}
