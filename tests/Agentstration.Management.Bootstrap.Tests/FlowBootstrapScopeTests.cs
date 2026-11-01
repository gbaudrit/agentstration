using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Declarative;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    private static readonly JsonSerializerOptions FlowBootstrapJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task FlowBootstrapAppliesDefinitionsAtEveryProfileScope()
    {
        using var directory = new TemporaryDirectory();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory.Path, "flow.db")};Pooling=False");
        services.AddSingleton<ISchemaBindingResolver, TestSchemaBindingResolver>();
        services.AddSingleton<FlowService>();
        await using var provider = services.BuildServiceProvider();
        var flows = provider.GetRequiredService<FlowService>();
        await flows.InitializeAsync(default);
        var handler = new FlowBootstrapResourceHandler(flows, new AlwaysValidFlowDefinitionValidator(), TimeProvider.System);
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var workspaceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var cases = new[]
        {
            (BootstrapProfileScope.Instance, (BootstrapApplicationTarget?)null, ResourceScopeRef.Instance),
            (BootstrapProfileScope.Tenant, new BootstrapApplicationTarget(tenantId), ResourceScopeRef.Tenant(tenantId)),
            (BootstrapProfileScope.Workspace, new BootstrapApplicationTarget(tenantId, workspaceId), ResourceScopeRef.Workspace(workspaceId))
        };

        Assert.IsTrue(cases.All(value => handler.SupportsProfileScope(value.Item1)));
        foreach (var (profileScope, target, scopeRef) in cases)
        {
            var operation = new BootstrapResourceOperationContext("test", directory.Path, profileScope, target);
            var planning = new BootstrapPlanningContext();
            var document = FlowBootstrapDocument();

            var plan = await handler.PlanAsync(document, operation, planning, default);
            var applied = await handler.ApplyAsync(document, operation, default);

            Assert.AreEqual(BootstrapResourceDisposition.Create, plan.Disposition);
            Assert.AreEqual(BootstrapResourceApplyResult.Created, applied);
            Assert.AreEqual(scopeRef, (await flows.GetExactAsync(scopeRef, new("bootstrap-flow"), default))!.Value.ScopeRef);
            Assert.AreEqual(scopeRef, (await flows.GetVersionExactAsync(scopeRef, new("bootstrap-flow"), "1.0.0", default))!.Value.ScopeRef);
        }
    }

    private static BootstrapResourceDocument FlowBootstrapDocument() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = FlowResourceKinds.Flow,
        Metadata = new() { Name = "bootstrap-flow" },
        Definition = JsonSerializer.SerializeToElement(new DeclarativeFlowDefinition
        {
            DisplayName = "Bootstrap Flow",
            Version = "1.0.0",
            Spec = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant")),
            Publish = true,
            Activate = true
        }, FlowBootstrapJsonOptions)
    };

    private sealed class AlwaysValidFlowDefinitionValidator : IFlowDefinitionValidator
    {
        public ValueTask<FlowValidationResult> ValidateAsync(
            FlowGraphDefinition definition,
            FlowValidationContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(new FlowValidationResult([]));
    }

    private sealed class TestSchemaBindingResolver : ISchemaBindingResolver
    {
        public ValueTask<SchemaBindingSnapshot> ResolveAsync(
            string path,
            SchemaBinding binding,
            SchemaBindingResolutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "The Flow bootstrap scope fixture does not define schema bindings.");
    }
}
