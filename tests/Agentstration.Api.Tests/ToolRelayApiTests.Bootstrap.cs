using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Notifications;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Bootstrap;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class ToolRelayApiTests
{
    [TestMethod]
    public async Task BootstrapRelayPreviewDoesNotMutateAndPublicationReplayRepairsMissingMaterialization()
    {
        var fixture = new SourceHandler();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.AddSingleton<IInternalMcpToolDefinitionProvider>(fixture); services.AddSingleton<IInternalMcpToolHandler>(fixture); }));
        var actor = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(actor);
        var owner = ResourceScopeRef.Workspace(actor.WorkspaceId);
        await factory.Services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(owner, default, default);
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        var source = (await resources.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(owner, default,
            ToolResourceKinds.Tool, "agentstration.relay-test.source"), default))!.Value;
        var handler = ActivatorUtilities.CreateInstance<ToolDefinitionBootstrapHandler>(factory.Services);
        var operation = new BootstrapResourceOperationContext("fixture", Path.GetTempPath(), BootstrapProfileScope.Workspace,
            new(actor.TenantId, actor.WorkspaceId))
        { ActorPrincipalId = actor.PrincipalId };
        var document = RelayBootstrapDocument(source, Relay(source, "1.0.0", "hr") with { Publish = true });
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        var plan = new BootstrapPlanningContext();
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await handler.PlanAsync(document, operation, plan, default)).Disposition);
        Assert.IsTrue(plan.Contains(ToolResourceKinds.Tool, "agentstration.bootstrap-relay.v1.0.0", "default"));
        Assert.IsNull(await definitions.GetExactAsync(document.Metadata.Name, default, owner, default));
        Assert.AreEqual(0, fixture.Calls);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await handler.ApplyAsync(document, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await handler.ApplyAsync(document, operation, default));
        var publication = (await definitions.GetPublicationAsync("bootstrap-relay.v1.0.0", default, owner, default))!;
        var address = ScopedResourceAddress.Create(owner, default, ToolResourceKinds.Tool, "agentstration.bootstrap-relay.v1.0.0");
        var materialized = (await resources.GetExactAsync<ToolResource>(address, default))!;
        await resources.DeleteExactAsync(address, materialized.ETag, default);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await handler.ApplyAsync(document, operation, default));
        Assert.AreEqual(publication.Value.Uid, (await definitions.GetPublicationAsync("bootstrap-relay.v1.0.0", default, owner, default))!.Value.Uid);
        Assert.IsNotNull(await resources.GetExactAsync<ToolResource>(address, default));
        var changed = RelayBootstrapDocument(source, Relay(source, "1.0.0", "changed") with { Publish = true });
        Assert.AreEqual(BootstrapResourceDisposition.Conflict, (await handler.PlanAsync(changed, operation, new(), default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Conflict, await handler.ApplyAsync(changed, operation, default));
        Assert.AreEqual(0, fixture.Calls);
    }

    [TestMethod]
    public async Task BootstrapRelayRejectsMissingSecretAndForeignTargetBeforeCreatingItsDraft()
    {
        var fixture = new SourceHandler();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.AddSingleton<IInternalMcpToolDefinitionProvider>(fixture); services.AddSingleton<IInternalMcpToolHandler>(fixture); }));
        var actor = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(actor);
        var owner = ResourceScopeRef.Workspace(actor.WorkspaceId);
        await factory.Services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(owner, default, default);
        var source = (await factory.Services.GetRequiredService<IResourceStore>().GetExactAsync<ToolResource>(ScopedResourceAddress.Create(owner, default,
            ToolResourceKinds.Tool, "agentstration.relay-test.source"), default))!.Value;
        var handler = ActivatorUtilities.CreateInstance<ToolDefinitionBootstrapHandler>(factory.Services);
        var operation = new BootstrapResourceOperationContext("fixture", Path.GetTempPath(), BootstrapProfileScope.Workspace,
            new(actor.TenantId, actor.WorkspaceId))
        { ActorPrincipalId = actor.PrincipalId };
        var properties = Relay(source, "1.0.0", "hr") with
        {
            Publish = true,
            Bindings = [new SecretToolArgumentBinding
            { Path = "/accessCode", Secret = new(ResourceAddress.Create(default, SecretResourceKinds.Secret, "missing-bootstrap-secret"), owner) }]
        };
        var document = RelayBootstrapDocument(source, properties);
        var error = await Assert.ThrowsAsync<ToolDefinitionValidationException>(() => handler.PlanAsync(document, operation, new(), default));
        Assert.AreEqual("tool_definition_binding_unavailable", error.Code);
        var foreign = operation with { Target = new(actor.TenantId, Guid.NewGuid()) };
        Assert.AreEqual("bootstrap_tool_scope_invalid", (await Assert.ThrowsAsync<ToolDefinitionValidationException>(() =>
            handler.ApplyAsync(document, foreign, default))).Code);
        Assert.IsNull(await factory.Services.GetRequiredService<ToolDefinitionService>().GetExactAsync(document.Metadata.Name, default, owner, default));
        Assert.AreEqual(0, fixture.Calls);
    }

    private static BootstrapResourceDocument RelayBootstrapDocument(ToolResource source, RelayToolDefinitionProperties properties)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var value = JsonSerializer.SerializeToNode<ToolDefinitionProperties>(properties, options)!.AsObject();
        value["target"] = JsonSerializer.SerializeToNode(new ResourceReference(source.Name, source.ScopeRef, source.Namespace), options);
        return new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new() { Name = "bootstrap-relay" },
            Definition = JsonSerializer.SerializeToElement(value, options)
        };
    }
}
