using System.Text;
using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed partial class WorkProjectConnectedServiceResolver
{
    public const string TransportMetadata = "agentstration.work.connected-service.http.v1";
    public const string TransportHeader = "X-Agentstration-Connected-Service";
    public async Task<ResolvedSecretInputs> ResolveHeadersAsync(WorkspaceId workspace, WorkProjectConnectionSelection selection,
        WorkProjectConnectionEvidence evidence, CancellationToken token)
    {
        try { return await ResolveHeadersCoreAsync(workspace, selection.Assignment.Service, selection.Assignment.DescribeTool, selection.Requirement.Name, evidence, token); }
        catch (Exception error) when (error is ConnectedServiceValidationException or ConnectedServiceAccessDeniedException
            or ConnectedServiceUnavailableException or SecretResolutionException or Agentstration.Parameters.ParameterAccessDeniedException
            or Agentstration.Parameters.ParameterManagementException or KeyNotFoundException or JsonException or InvalidOperationException)
        { throw InvalidHeaders(); }
    }
    private async Task<ResolvedSecretInputs> ResolveHeadersCoreAsync(WorkspaceId workspace, WorkProjectResourceReference service, WorkProjectResourceReference adapter, string name,
        WorkProjectConnectionEvidence evidence, CancellationToken token)
    {
        var tool = (await resources.GetExactAsync<ToolResource>(adapter.Address, token))?.Value;
        if (tool is null) throw InvalidHeaders();
        if (!tool.Definition.Metadata.TryGetValue(TransportMetadata, out var declaration)) return new(JsonSerializer.SerializeToElement(new Dictionary<string, string>()), new Dictionary<string, string>());
        if (serviceResolver is null) throw InvalidHeaders();
        var typeId = await ValidateTransportAsync(declaration, evidence, token);
        var address = service.Address;
        using var resolved = await serviceResolver.ResolveAsync(new(new(address.Namespace, address.Kind, address.Name), address.ScopeRef),
            new(ResourceScopeRef.Workspace(workspace.Value), new(address.Namespace, "Project", name)), token);
        if (resolved is null || resolved.Type.TypeUid != evidence.Type.Uid || resolved.Type.Version != evidence.TypeVersion
            || resolved.Type.SchemaDigest != evidence.TypeSchemaDigest) throw InvalidHeaders();
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var protectedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in resolved.Inputs)
        {
            if (input.Secret is { } secret)
            {
                var value = Encoding.UTF8.GetString(secret.AccessValue().Span);
                inputs.Add(input.Id, value); protectedValues.Add("provider." + input.Id, value);
            }
            else if (input.Value is { ValueKind: JsonValueKind.String } value) inputs.Add(input.Id, value.GetString()!);
            else throw InvalidHeaders();
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { typeId, version = evidence.TypeVersion, schemaDigest = evidence.TypeSchemaDigest, options = resolved.Options, inputs }, JsonSerializerOptions.Web);
        if (bytes.Length > 18_432) throw InvalidHeaders();
        var header = Convert.ToBase64String(bytes);
        protectedValues.Add("provider.configuration", header);
        return new(JsonSerializer.SerializeToElement(new Dictionary<string, string> { [TransportHeader] = header }), protectedValues);
    }
    private async Task<string> ValidateTransportAsync(JsonElement declaration, WorkProjectConnectionEvidence evidence, CancellationToken token)
    {
        if (declaration.ValueKind != JsonValueKind.Object || declaration.GetProperty("version").GetString() != "1.0"
            || declaration.GetProperty("serviceTypeVersion").GetString() != evidence.TypeVersion
            || declaration.GetProperty("schemaDigest").GetString() != evidence.TypeSchemaDigest) throw InvalidHeaders();
        var typeId = declaration.GetProperty("serviceTypeId").GetString();
        if (!WorkProjectContextRules.Text(typeId)) throw InvalidHeaders();
        ConnectedServiceTypeResource type;
        using (scopes.PushSystem()) type = (await resources.GetExactAsync<ConnectedServiceTypeResource>(evidence.Type.Address, token))?.Value ?? throw InvalidHeaders();
        if (Reference(type) != evidence.Type) throw InvalidHeaders();
        var version = type.Definition.Versions.Single(value => value.Version == evidence.TypeVersion && value.SchemaDigest == evidence.TypeSchemaDigest);
        var inputContract = JsonSerializer.SerializeToElement(version.Inputs.OrderBy(value => value.Id, StringComparer.Ordinal).Select(value => new
        { id = value.Id, required = value.Required, type = value.Type.ToString(), protection = value.Protection.ToString(), format = value.Format }).ToArray());
        var exportContract = JsonSerializer.SerializeToElement(version.Exports.OrderBy(value => value.Id, StringComparer.Ordinal).Select(value => new
        { id = value.Id, type = value.Type.ToString(), protection = value.Protection.ToString(), format = value.Format, sourceInputId = value.SourceInputId }).ToArray());
        if (!JsonElement.DeepEquals(inputContract, declaration.GetProperty("inputContract")) || !JsonElement.DeepEquals(exportContract, declaration.GetProperty("exportContract"))) throw InvalidHeaders();
        return typeId!;
    }
    private static ToolResolutionException InvalidHeaders() => new("work_project_connection_incompatible", "The selected service cannot configure this adapter.");
}
