using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolSecretInputResolver(ToolDefinitionService definitions, ISecretResolver secrets,
    ICurrentRequestContext current, IResourceScopeResolver scopes) : ISecretInputResolver
{
    public async Task<ResolvedSecretInputs> ResolveAsync(JsonElement input,
        IReadOnlyList<SecretInputReference> references, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (references.Count == 0) return new(input, values);
        var used = new HashSet<string>(StringComparer.Ordinal);
        _ = SecretInputs.Rewrite(input, value =>
        {
            if (SecretInputs.TryGetId(value, out var id) && references.Any(reference => reference.Id == id)) used.Add(id!);
            return null;
        });
        if (used.Count == 0) return new(input, values);
        var scope = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
            ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(current.Current.WorkspaceId),
            _ => throw Error()
        };
        var resolvedScope = await scopes.ResolveAsync(scope, cancellationToken) ?? throw Error();
        try
        {
            foreach (var id in used)
            {
                var reference = references.SingleOrDefault(reference => reference.Id == id) ?? throw Error();
                if (reference.Consumer.ConsumerScopeRef != scope
                    && !resolvedScope.Ancestors.Any(ancestor => ancestor.Ref == reference.Consumer.ConsumerScopeRef)) throw Error();
                if (reference.Secret.ScopeRef is not { } owner) throw Error();
                var address = ScopedResourceAddress.Create(owner, reference.Secret.Address.Namespace,
                    reference.Secret.Address.Kind, reference.Secret.Address.Name);
                if (await definitions.BindingIdentityAsync(address, cancellationToken) != reference.Uid) throw Error();
                using var secret = await secrets.ResolveAsync(reference.Secret, reference.Consumer, cancellationToken) ?? throw Error();
                values.Add(id, new UTF8Encoding(false, true).GetString(secret.Value.AccessValue().Span));
            }
            var materialized = SecretInputs.Rewrite(input, value => SecretInputs.TryGetId(value, out var id)
                && values.TryGetValue(id!, out var text) ? JsonSerializer.SerializeToElement(text) : null);
            if (materialized.GetRawText().Length > ToolRelayArguments.MaximumPayloadLength) throw Error();
            return new(materialized, values);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            values.Clear();
            throw Error();
        }
    }

    private static ToolDefinitionInvocationException Error() => new("tool_definition_binding_unavailable",
        "A protected input is missing, replaced, invalid or unauthorized.");
}
