using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ContributionAccessAdministrationTests
{
    private static readonly ResourceAccessAction[] Actions = [ResourceAccessAction.Discover, ResourceAccessAction.Use];

    [TestMethod]
    public async Task GovernedDraftBulkAccessDenialsAndParentRevocationUseOnePersistedPolicyGraph()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-access-administration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var host = await OpenAsync(directory);
            var services = host.Services;
            var scopeFactory = services.GetRequiredService<IRequestContextScopeFactory>();
            RequestContext consumer;
            using (scopeFactory.PushSystem()) consumer = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            var store = services.GetRequiredService<IResourceStore>();
            var policies = services.GetRequiredService<IResourceAccessPolicyService>();
            var related = services.GetRequiredService<ResourceRelatedAccessService>();
            var access = services.GetRequiredService<ToolAccessService>();
            var consumerScope = ResourceScopeRef.Workspace(consumer.WorkspaceId);
            Fixture fixture;
            using (scopeFactory.PushInstance(consumer))
            {
                fixture = await SeedAsync(services);
                await policies.PutAsync(new(fixture.Registration, []) { AllDescendantActions = Actions }, null, default);
                var snapshot = await related.GetAsync(fixture.Registration.Uid, default);
                Assert.HasCount(4, snapshot.Resources);
                Assert.IsTrue(snapshot.Resources.Any(value => value.Resource.Uid == fixture.FlowUid && value.Resource.Kind == "Flow"));
                var applied = await related.ApplyAsync(fixture.Registration.Uid, Request(snapshot,
                    ResourceScopeRef.Tenant(consumer.TenantId), ResourceRelatedAccessMode.Allow), default);
                Assert.IsTrue(applied.All(value => value.Applied));
                Assert.IsNull(await policies.GetAsync(fixture.Tool.Uid, default), "Member Tools inherit instead of receiving copied grants.");
            }
            using (scopeFactory.Push(consumer))
            {
                Assert.IsTrue((await access.CanUseAsync(fixture.Tool, consumerScope, default)).Allowed);
                Assert.IsTrue((await policies.EvaluateAsync(new(fixture.FlowUid, ResourceScopeRef.Instance, ResourceNamespace.Default, "Flow"),
                    consumerScope, ResourceAccessAction.Use, default)).Allowed, "Independent Flow storage uses the same verified parent policy.");
                Assert.IsNotNull(await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(ResourceScopeRef.Instance, fixture.Tool.Namespace, fixture.Tool.Kind, fixture.Tool.Name), default));
            }
            using (scopeFactory.PushInstance(consumer))
            {
                var snapshot = await related.GetAsync(fixture.Registration.Uid, default);
                await related.ApplyAsync(fixture.Registration.Uid, Request(snapshot, consumerScope, ResourceRelatedAccessMode.Deny), default);
            }
            using (scopeFactory.Push(consumer)) Assert.IsFalse((await access.CanUseAsync(fixture.Tool, consumerScope, default)).Allowed);
            using (scopeFactory.PushInstance(consumer))
            {
                var snapshot = await related.GetAsync(fixture.Registration.Uid, default);
                await related.ApplyAsync(fixture.Registration.Uid, Request(snapshot, consumerScope, ResourceRelatedAccessMode.Inherit), default);
                await policies.PutAsync(new(ToolAccessService.Identity(fixture.Tool),
                    [new(consumerScope, []) { DeniedActions = Actions }]), null, default);
                snapshot = await related.GetAsync(fixture.Registration.Uid, default);
                await related.ApplyAsync(fixture.Registration.Uid, Request(snapshot, consumerScope, ResourceRelatedAccessMode.Allow), default);
                // A deny and restore cycle on another collection must not adopt/remove a direct Tool exception.
                snapshot = await related.GetAsync(fixture.SetUid, default);
                var preserved = await related.ApplyAsync(fixture.SetUid, Request(snapshot, consumerScope, ResourceRelatedAccessMode.Deny), default);
                Assert.IsTrue(preserved.Any(value => value.ErrorCode == "explicit_deny_preserved"));
                snapshot = await related.GetAsync(fixture.SetUid, default);
                await related.ApplyAsync(fixture.SetUid, Request(snapshot, consumerScope, ResourceRelatedAccessMode.Inherit), default);
            }
            using (scopeFactory.Push(consumer))
            {
                var denied = await access.CanUseAsync(fixture.Tool, consumerScope, default);
                Assert.AreEqual(ResourceAccessDecisionReasons.ExplicitDeny, denied.ReasonCode);
                Assert.IsNull(await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(ResourceScopeRef.Instance, fixture.Tool.Namespace, fixture.Tool.Kind, fixture.Tool.Name), default));
                var invocation = new ToolExecutionContext
                {
                    OwnerKind = ToolExecutionOwnerKind.RuntimeRun, ToolCallId = "denied", InvocationId = "denied-1",
                    ToolId = fixture.Tool.Name, ToolName = fixture.Tool.Name, ToolScopeRef = ResourceScopeRef.Instance,
                    ToolUid = fixture.Tool.Uid, ToolGeneration = fixture.Tool.Generation, ToolNamespace = fixture.Tool.Namespace,
                    TenantId = consumer.TenantId, WorkspaceId = new(consumer.WorkspaceId), PrincipalId = consumer.PrincipalId
                };
                var failure = await Assert.ThrowsExactlyAsync<ToolResolutionException>(() =>
                    services.GetRequiredService<IToolInvoker>().ValidateAsync(invocation, default).AsTask());
                Assert.AreEqual("tool_access_denied", failure.Code);
            }
            using (scopeFactory.PushInstance(consumer))
            {
                var denied = (await policies.GetAsync(fixture.Tool.Uid, default))!;
                await policies.PutAsync(denied.Policy with { Grants = [] }, denied.ETag, default);
                var parent = (await policies.GetAsync(fixture.Registration.Uid, default))!;
                await policies.PutAsync(parent.Policy with { AllDescendantActions = [] }, parent.ETag, default);
                Assert.IsTrue((await policies.GetAsync(fixture.SetUid, default))!.Policy.Grants.Count > 0);
            }
            using (scopeFactory.Push(consumer))
                Assert.AreEqual(ResourceAccessDecisionReasons.ParentAccessDenied,
                    (await access.CanUseAsync(fixture.Tool, consumerScope, default)).ReasonCode);
            using (scopeFactory.PushInstance(consumer))
            {
                var parent = (await policies.GetAsync(fixture.Registration.Uid, default))!;
                await policies.PutAsync(parent.Policy with { AllDescendantActions = Actions }, parent.ETag, default);
            }
            using (scopeFactory.Push(consumer)) Assert.IsTrue((await access.CanUseAsync(fixture.Tool, consumerScope, default)).Allowed);
            using (scopeFactory.PushInstance(consumer))
            {
                var address = ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, ExtensionKinds.ExtensionRegistration, "access-fixture");
                var previous = (await store.GetExactAsync<ExtensionRegistrationResource>(address, default))!;
                await store.DeleteExactAsync(address, previous.ETag, default);
                var replacement = await store.PutExactAsync(ResourceScopeRef.Instance, previous.Value with { Uid = Guid.Empty }, null, true, default);
                Assert.AreNotEqual(fixture.Registration.Uid, replacement.Value.Uid);
                await policies.PutAsync(new(new(replacement.Value.Uid, ResourceScopeRef.Instance, replacement.Value.Namespace, replacement.Value.Kind), []) { AllDescendantActions = Actions }, null, default);
                Assert.IsEmpty((await related.GetAsync(replacement.Value.Uid, default)).Resources);
            }
            using (scopeFactory.Push(consumer))
            {
                Assert.IsFalse((await access.CanUseAsync(fixture.Tool, consumerScope, default)).Allowed);
                var flow = await policies.EvaluateAsync(new(fixture.FlowUid, ResourceScopeRef.Instance, ResourceNamespace.Default, "Flow"),
                    consumerScope, ResourceAccessAction.Use, default);
                Assert.IsFalse(flow.Allowed, "A deleted/recreated source must not revive a standalone Flow grant.");
                Assert.AreEqual(ResourceAccessDecisionReasons.RelationshipInvalid, flow.ReasonCode);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ManagedActivationSurvivesRestartWithoutChangingGovernedContentAndRejectsStaleStateEtags()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-operational-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Fixture fixture;
        string manifest;
        try
        {
            using (var host = await OpenAsync(directory))
            {
                var services = host.Services;
                using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
                fixture = await SeedAsync(services);
                manifest = JsonSerializer.Serialize(fixture.Tool);
                var management = services.GetRequiredService<ToolManagementService>();
                await management.SetToolEnabledExactAsync(ResourceScopeRef.Instance, fixture.Tool.Name, fixture.Tool.Namespace, true, null, default);
                Assert.IsTrue(await services.GetRequiredService<ToolAccessService>().IsEnabledAsync(fixture.Tool, default));
                var current = (await management.GetToolExactAsync(ResourceScopeRef.Instance, fixture.Tool.Name, fixture.Tool.Namespace, default))!;
                Assert.AreEqual(manifest, JsonSerializer.Serialize(current.Value));
                Assert.IsFalse(current.Value.Definition.Enabled);
                await Assert.ThrowsExactlyAsync<ResourceConcurrencyException>(() => management.SetToolEnabledExactAsync(
                    ResourceScopeRef.Instance, fixture.Tool.Name, fixture.Tool.Namespace, false, "\"stale\"", default));
            }
            using (var restarted = await OpenAsync(directory))
            {
                var services = restarted.Services;
                using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
                var current = (await services.GetRequiredService<ToolManagementService>().GetToolExactAsync(
                    ResourceScopeRef.Instance, fixture.Tool.Name, fixture.Tool.Namespace, default))!;
                Assert.AreEqual(manifest, JsonSerializer.Serialize(current.Value));
                Assert.IsTrue(await services.GetRequiredService<ToolAccessService>().IsEnabledAsync(current.Value, default));
                Assert.IsNotNull(await services.GetRequiredService<ToolManagementService>().GetToolOperationalStateAsync(current.Value, default));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    private static ResourceRelatedAccessRequest Request(ResourceRelatedAccessSnapshot snapshot, ResourceScopeRef scope, ResourceRelatedAccessMode mode) =>
        new(scope, mode, snapshot.MembershipToken, snapshot.Resources.ToDictionary(value => value.Resource.Uid, value => value.Policy?.ETag));

    private static async Task<Fixture> SeedAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<IResourceStore>();
        var registration = await store.PutExactAsync(ResourceScopeRef.Instance, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.ExtensionRegistration, ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "access-fixture" }, Generation = 1,
            Definition = new() { DisplayName = "Access fixture", Endpoint = new("http://localhost:1"), ExpectedExtensionId = "tests.access" }
        }, null, true, default);
        using var governance = services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Extension(
            $"{ResourceScopeRef.Instance.Value}/{ExtensionKinds.ExtensionRegistration}/default/access-fixture", "tests.access", "1.0.0", registration.Value.Uid));
        var provider = await store.PutExactAsync(ResourceScopeRef.Instance, new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "access-fixture" }, Generation = 1,
            Definition = new() { DisplayName = "Fixture provider", ProviderType = ToolProviderType.Aep,
                Aep = new() { Extension = new("access-fixture", ResourceScopeRef.Instance, ResourceNamespace.Default) } }
        }, null, true, default);
        var tool = await store.PutExactAsync(ResourceScopeRef.Instance, new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.Tool, ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "fixture-read" }, Generation = 1,
            Definition = new() { DisplayName = "Fixture read", Enabled = false, ExternalId = "read",
                Provider = new(provider.Value.Name, ResourceScopeRef.Instance, ResourceNamespace.Default),
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch },
                Schema = new() { Input = JsonSerializer.SerializeToElement(new { type = "object" }) } }
        }, null, true, default);
        var set = await store.PutExactAsync(ResourceScopeRef.Instance, new ToolSetResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolSet, ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "fixture-reads" }, Generation = 1,
            Definition = new() { DisplayName = "Fixture reads", Members = [new() { Tool = new(tool.Value.Name,
                ResourceScopeRef.Instance, ResourceNamespace.Default), ToolUid = tool.Value.Uid, ToolGeneration = tool.Value.Generation,
                Capability = "read", Route = "default" }] }
        }, null, true, default);
        var flow = await services.GetRequiredService<Agentstration.Flows.Application.FlowService>().CreateExactAsync(
            ResourceScopeRef.Instance, new("fixture-flow", null, "1.0.0", true,
                new Agentstration.Flows.DirectFlowDefinition(new Agentstration.Flows.FlowTargetReference(
                    Agentstration.Flows.FlowTargetKind.Agent, "assistant"))), ResourceNamespace.Default, default);
        return new(new(registration.Value.Uid, ResourceScopeRef.Instance, registration.Value.Namespace, registration.Value.Kind), tool.Value, set.Value.Uid, flow.Value.Uid);
    }

    private static async Task<IHost> OpenAsync(string directory)
    {
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new LocalBootstrapOptions { GrantPlatformAdministrator = true });
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
        builder.Services.AddAgentstration(new AgentstrationCompositionOptions { DataDirectory = directory, EnableHostedServices = false });
        var host = builder.Build();
        using var system = host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        await host.Services.GetRequiredService<IAgentstrationStorageInitializer>().InitializeAsync(default);
        await host.Services.GetRequiredService<Agentstration.Agents.AgentManagementService>().InitializeAsync(default);
        await host.Services.GetRequiredService<Agentstration.Flows.Application.FlowService>().InitializeAsync(default);
        await host.Services.GetRequiredService<Agentstration.Flows.Application.FlowRunService>().InitializeAsync(default);
        _ = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        return host;
    }

    private sealed record Fixture(ResourceAccessIdentity Registration, ToolResource Tool, Guid SetUid, Guid FlowUid);
}
