using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Aep;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Aep;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using ModelContextProtocol;

namespace Agentstration.Infrastructure.Work;

public sealed class WorkProjectContractResolver(IResourceStore resources, IAepExtensionRegistrationResolver extensions,
    AepMcpTransport transport, IToolExecutionPipeline pipeline, TimeProvider clock)
{
    public async Task<Result<WorkProjectContractEvidence>> ResolveAsync(WorkProjectContextBinding binding, RequestContext caller,
        Func<WorkProjectResourceReference, CancellationToken, Task<Result<WorkProjectResolvedBinding>>> authorize, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { return await ResolveCoreAsync(binding, caller, authorize, linked.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure("work_project_contract_timeout"); }
        catch (AtcException) { return Failure("work_project_contract_invalid"); }
        catch (AfcException) { return Failure("work_project_contract_invalid"); }
        catch (AepProtocolException) { return Failure("work_project_contract_unavailable"); }
        catch (ToolExecutionDeniedException) { return Failure("work_project_binding_denied"); }
        catch (ToolInputValidationException) { return Failure("work_project_contract_invalid"); }
        catch (ToolResolutionException) { return Failure("work_project_contract_changed"); }
        catch (McpException) { return Failure("work_project_contract_unavailable"); }
        catch (HttpRequestException) { return Failure("work_project_contract_unavailable"); }
        catch (JsonException) { return Failure("work_project_contract_invalid"); }
    }

    private async Task<Result<WorkProjectContractEvidence>> ResolveCoreAsync(WorkProjectContextBinding binding, RequestContext caller,
        Func<WorkProjectResourceReference, CancellationToken, Task<Result<WorkProjectResolvedBinding>>> authorize, CancellationToken token)
    {
        var selection = binding.Contract!;
        var tracking = binding.Purpose == WorkProjectBindingPurpose.Tracking;
        var contractId = tracking ? AtcContract.Id : AfcContract.Id;
        var operations = tracking ? AtcContract.Tools : AfcContract.Tools;
        var provider = (await resources.GetExactAsync<ToolProviderResource>(selection.Provider.Address, token))?.Value;
        var selected = (await resources.GetExactAsync<ToolResource>(binding.Target.Address, token))?.Value;
        if (provider is null || Reference(provider) != selection.Provider || !provider.Definition.Enabled
            || provider.Definition.ProviderType != ToolProviderType.Aep || selected is null || Reference(selected) != binding.Target
            || !BelongsTo(selected, provider)) return Failure("work_project_contract_changed");
        var extension = await extensions.ResolveAsync(provider, token);
        if (Reference(extension.Registration) != selection.Extension) return Failure("work_project_contract_changed");
        var manifest = await transport.DiscoverAsync(extension, extension.Registration.Definition.ExpectedExtensionId, token);
        if (manifest.Extension.Id != extension.Registration.Definition.ExpectedExtensionId
            || (tracking ? AtcAepContributions.Validate(manifest) : AfcAepContributions.Validate(manifest)).Count != 0)
            return Failure("work_project_contract_invalid");
        var contribution = manifest.Contributions.Tools?.SingleOrDefault(value => value.Id == selected.Definition.ExternalId);
        if (contribution is null || contribution.Metadata?.ContainsKey(contractId) != true) return Failure("work_project_contract_missing");
        string prefix, version;
        JsonElement declaration;
        if (tracking)
        {
            var value = AtcAepContributions.Read(contribution); prefix = value.ToolPrefix; version = value.Version;
            declaration = JsonSerializer.SerializeToElement(value, AtcContract.Json);
        }
        else
        {
            var value = AfcAepContributions.Read(contribution); prefix = value.ToolPrefix; version = value.Version;
            declaration = JsonSerializer.SerializeToElement(value, AfcContract.Json);
        }
        if (version != selection.Version || contribution.Mcp.Tool != prefix + "_describe") return Failure("work_project_contract_invalid");
        var page = await resources.ListExactAsync<ToolResource>(selection.Provider.Address.ScopeRef, ToolResourceKinds.Tool, 0, 1_001, token);
        if (page.Count > 1_000) return Failure("work_project_contract_discovery_limit");
        var candidates = page.Select(value => value.Value).Where(tool => BelongsTo(tool, provider)).ToArray();
        var pins = new List<WorkProjectContributionPin>();
        foreach (var operation in operations)
        {
            var mappings = (manifest.Contributions.Tools ?? []).Where(value => value.Mcp.Server == contribution.Mcp.Server
                && value.Mcp.Tool == prefix + "_" + operation).ToArray();
            if (mappings.Length != 1) return Failure("work_project_contract_invalid");
            var mapping = mappings[0];
            var tools = candidates.Where(tool => tool.Definition.ExternalId == mapping.Id).ToArray();
            if (tools.Length != 1) return Failure("work_project_contract_missing");
            var tool = tools[0];
            var allowed = await authorize(Reference(tool), token);
            if (!allowed.IsSuccess) return Failure(allowed.ErrorCode!);
            if (!tool.Definition.Enabled || tool.Definition.Discovery?.Available != true) return Failure("work_project_contract_unavailable");
            if (!tool.Definition.Metadata.TryGetValue(contractId, out var advertised)
                || !mapping.Metadata!.TryGetValue(contractId, out var mapped)
                || !JsonElement.DeepEquals(advertised, declaration) || !JsonElement.DeepEquals(mapped, declaration)
                || tool.Definition.Schema is not { Output: { } output } schema
                || !JsonElement.DeepEquals(schema.Input, tracking ? AtcSchemas.Input(operation) : AfcSchemas.Input(operation))
                || !JsonElement.DeepEquals(output, tracking ? AtcSchemas.Output(operation) : AfcSchemas.Output(operation)))
                return Failure("work_project_contract_invalid");
            pins.Add(new(Reference(tool), ToolInvocationFingerprint.AepRoute(extension.Registration, manifest, mapping)));
        }
        var selectedPins = pins.Where(pin => pin.Resource == binding.Target).ToArray();
        if (selectedPins.Length != 1) return Failure("work_project_contract_changed");
        if (selected.Definition.RequiresApproval) return Failure("work_project_contract_approval_required");
        var requestId = Guid.NewGuid().ToString("N");
        var input = tracking
            ? JsonSerializer.SerializeToElement(new AtcInvocation<AtcDescribeRequest>(new(selection.ProviderId, selection.TargetId, selection.BindingId, selection.Revision), new(requestId)), AtcContract.Json)
            : JsonSerializer.SerializeToElement(new AfcInvocation<AfcDescribeRequest>(new(selection.ProviderId, selection.TargetId, selection.BindingId, selection.Revision), new(requestId)), AfcContract.Json);
        var result = await pipeline.ExecuteAsync(new ToolExecutionContext
        {
            OwnerKind = ToolExecutionOwnerKind.WorkOperation,
            ToolCallId = requestId,
            InvocationId = "project-contract:" + requestId,
            ToolId = selected.Name,
            ToolName = selected.Definition.ExternalId!,
            ToolUid = selected.Uid,
            ToolGeneration = selected.Generation,
            ToolScopeRef = selected.ScopeRef,
            ToolNamespace = selected.Namespace,
            ExternalToolId = selected.Definition.ExternalId,
            ToolProviderId = provider.Name,
            ToolProviderUid = provider.Uid,
            ToolProviderGeneration = provider.Generation,
            ToolProviderScopeRef = provider.ScopeRef,
            ToolProviderNamespace = provider.Namespace,
            ExpectedProviderRouteDigest = selectedPins[0].RouteDigest,
            ExpectedToolSchemaDigest = ToolInvocationFingerprint.Schemas(selected.Definition.Schema!.Input, selected.Definition.Schema.Output),
            ExpectedProviderToolContracts = operations.ToDictionary(operation => prefix + "_" + operation,
                operation => ToolInvocationFingerprint.Schemas(tracking ? AtcSchemas.Input(operation) : AfcSchemas.Input(operation),
                    tracking ? AtcSchemas.Output(operation) : AfcSchemas.Output(operation)), StringComparer.Ordinal),
            TenantId = caller.TenantId,
            WorkspaceId = new(caller.WorkspaceId),
            PrincipalId = caller.PrincipalId,
            CorrelationId = "project-contract:" + requestId,
            PersistArguments = false,
            Arguments = input
        }, token);
        if (result is not { ValueKind: JsonValueKind.Object } envelope || Encoding.UTF8.GetByteCount(envelope.GetRawText()) > 512 * 1024
            || envelope.TryGetProperty("isError", out var error) && error.ValueKind != JsonValueKind.False
            || !envelope.TryGetProperty("structuredContent", out var content) || Encoding.UTF8.GetByteCount(content.GetRawText()) > 262_144)
            return Failure("work_project_contract_invalid");
        string implementationId, implementationVersion, capabilitiesDigest;
        IReadOnlyList<string> available;
        if (tracking)
        {
            var reply = AtcValidation.Parse<AtcReply<AtcDescription>>(content);
            if (reply.ContractId != contractId || reply.Version != version || reply.RequestId != requestId || reply.Error is not null || reply.Data is not { } description)
                return Failure("work_project_contract_invalid");
            AtcSchemas.Validate(description.Declaration); AtcValidation.Capabilities(description.Capabilities);
            if (description.Target != new AtcTargetRef(selection.ProviderId, selection.TargetId, selection.BindingId, selection.Revision)
                || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(description.Declaration, AtcContract.Json), declaration)) return Failure("work_project_contract_invalid");
            available = description.Capabilities.Operations; implementationId = description.ImplementationId; implementationVersion = description.ImplementationVersion;
            capabilitiesDigest = WorkPlanDefinition.Digest(description.Capabilities);
        }
        else
        {
            var reply = AfcValidation.Parse<AfcReply<AfcDescription>>(content);
            if (reply.ContractId != contractId || reply.Version != version || reply.RequestId != requestId || reply.Error is not null || reply.Data is not { } description)
                return Failure("work_project_contract_invalid");
            AfcSchemas.Validate(description.Declaration); AfcValidation.Capabilities(description.Capabilities);
            if (description.Target != new AfcTargetRef(selection.ProviderId, selection.TargetId, selection.BindingId, selection.Revision)
                || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(description.Declaration, AfcContract.Json), declaration)) return Failure("work_project_contract_invalid");
            available = description.Capabilities.Operations; implementationId = description.ImplementationId; implementationVersion = description.ImplementationVersion;
            capabilitiesDigest = WorkPlanDefinition.Digest(description.Capabilities);
        }
        if (!WorkProjectContextRules.Text(implementationId) || !WorkProjectContextRules.Text(implementationVersion)) return Failure("work_project_contract_invalid");
        if (!selection.RequiredOperations.All(available.Contains)) return Failure("work_project_contract_operation_unsupported");
        return Result<WorkProjectContractEvidence>.Success(new(version, implementationId, implementationVersion,
            selection.Provider, selection.Extension, WorkPlanDefinition.Digest(pins), capabilitiesDigest, available.ToArray(), pins));
    }

    private static bool BelongsTo(ToolResource tool, ToolProviderResource provider) => tool.ScopeRef == provider.ScopeRef
        && tool.Namespace == provider.Namespace && tool.Definition.Provider is { } reference && reference.Name == provider.Name
        && (reference.ScopeRef ?? tool.ScopeRef) == provider.ScopeRef && (reference.Namespace ?? tool.Namespace) == provider.Namespace;
    private static WorkProjectResourceReference Reference(Resource resource) => new(new(resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Name), resource.Uid, resource.Generation);
    private static Result<WorkProjectContractEvidence> Failure(string code) => Result<WorkProjectContractEvidence>.Failure(code);
}
