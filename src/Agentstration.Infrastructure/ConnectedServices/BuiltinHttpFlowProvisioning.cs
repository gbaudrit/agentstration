using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Tools;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.ConnectedServices;

public sealed partial class ConnectedServicePlatformResourceProvisioner
{
    private async Task<ConnectedServiceDiscoveryFlowReference> EnsureHttpFlowAsync(bool discovery, CancellationToken cancellationToken)
    {
        var name = discovery ? BuiltinHttpOperation.DiscoveryFlowName : BuiltinHttpOperation.AcquisitionFlowName;
        var contract = discovery ? ConnectedServiceOperationDiscoveryContracts.V1 : "connected-service-operation-acquisition/v1";
        var toolName = discovery ? ConnectedServiceDiscoveryMcpTool.Name : ConnectedServiceAcquisitionMcpTool.Name;
        var input = discovery
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new
            {
                contract = new { type = "string" }, discoveryId = new { type = "string" },
                service = new { type = "object" }, serviceType = new { type = "object" },
                correlationId = new { type = "string" }, caller = new { type = "object" }
            }, required = new[] { "contract", "discoveryId", "service", "serviceType", "correlationId", "caller" }, additionalProperties = false })
            : JsonSerializer.SerializeToElement(new { type = "object", properties = new
            {
                acquisitionId = new { type = "string" }, operation = new { type = "object" },
                parameters = new { type = "object" }, caller = new { type = "object" }
            }, required = new[] { "acquisitionId", "operation", "parameters", "caller" }, additionalProperties = false });
        var id = new FlowId(name);
        var scope = ResourceScopeRef.Instance;
        var existing = await flows.GetExactAsync(scope, id, cancellationToken);
        if (existing is not null && (existing.Value.Id.Namespace != ResourceNamespace.Default
            || existing.Value.Governance.ManagedBy?.Kind != ResourceManagementAuthorityKind.Core
            || existing.Value.Governance.ManagedBy.Reference != "connected-services.http"))
            throw new InvalidOperationException($"Reserved builtin HTTP Flow '{name}' is already in use.");
        if (existing is null)
        {
            var graph = new FlowGraphDefinition
            {
                EntryStep = "input", InputSchema = input, OutputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                Steps =
                [
                    new InputFlowStepDefinition { Name = "input", Schema = input },
                    new ToolFlowStepDefinition
                    {
                        Name = "execute", Tool = new(AgentstrationToolProvider.ToolResourceName(toolName), ScopeRef: ResourceScopeRef.Instance),
                        ArgumentsMapping = discovery
                            ? JsonSerializer.SerializeToElement(new { discoveryId = "${input.discoveryId}", discoveryNamespace = "${input.service.service.address.namespace}" })
                            : JsonSerializer.SerializeToElement(new { acquisitionId = "${input.acquisitionId}" })
                    },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.execute.output}") },
                    new OutputFlowStepDefinition { Name = "failure", Outcome = FlowOutputOutcome.Error,
                        Code = discovery ? "http_discovery_failed" : "http_acquisition_failed", DetailsExpression = "${transition.output}" }
                ],
                Transitions = [new("input-execute", "input", "completed", "execute"),
                    new("execute-output", "execute", "success", "output"), new("execute-failure", "execute", "error", "failure")]
            };
            var definition = new WorkflowFlowDefinition("input",
                [new("input", FlowNodeKind.Input), new("execute", FlowNodeKind.Function), new("output", FlowNodeKind.Output)],
                [new("input", "execute"), new("execute", "output")], ["output"]);
            try
            {
                existing = await flows.CreateExactAsync(scope, new CreateFlowCommand(name,
                    "Builtin HTTP operation adapter.", BuiltinHttpOperation.Version, true, definition,
                    new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true",
                        ["systemManaged"] = "true", [FlowMetadataKeys.Contract] = contract }, graph,
                    discovery ? "HTTP operation discovery" : "HTTP operation acquisition"), ResourceNamespace.Default, cancellationToken);
            }
            catch (FlowConcurrencyException)
            {
                existing = await flows.GetExactAsync(scope, id, cancellationToken) ?? throw new InvalidOperationException("The builtin HTTP Flow disappeared during provisioning.");
            }
        }
        var version = await flows.GetVersionExactAsync(scope, id, BuiltinHttpOperation.Version, cancellationToken);
        if (version is null)
        {
            try { version = await flows.PublishVersionExactAsync(scope, id, BuiltinHttpOperation.Version, true, cancellationToken); }
            catch (FlowConcurrencyException)
            { version = await flows.GetVersionExactAsync(scope, id, BuiltinHttpOperation.Version, cancellationToken) ?? throw new InvalidOperationException("The builtin HTTP Flow disappeared during provisioning."); }
        }
        return new() { Flow = new(name, scope, ResourceNamespace.Default), FlowUid = existing.Value.Uid,
            Version = version.Value.Version, DefinitionHash = version.Value.DefinitionHash!, Contract = contract };
    }

}
