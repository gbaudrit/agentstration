using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Tests.Work;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class WorkProjectConnectedServiceTests
{
    [TestMethod]
    public async Task PortableRequirementsResolveIndependentLocalServicesAndScopesButRejectAnotherExternalTarget()
    {
        var left = new ProjectContractFixture(); var right = new ProjectContractFixture();
        using var leftScope = left.Context.Push(left.Caller); using var rightScope = right.Context.Push(right.Caller);
        var leftRoute = left.AddConnection(true, "tracking");
        var rightRoute = right.AddConnection(true, "tracking", ResourceScopeRef.Instance);
        var source = new[] { left.AddService(leftRoute, "backend"), left.AddService(leftRoute, "frontend", targetId: "frontend") };
        var assignments = new[] { right.AddService(rightRoute, "backend", instanceService: true), right.AddService(rightRoute, "frontend", targetId: "frontend") };
        var metadata = new WorkSharingProjectMetadata("Shared", null, [new(Guid.NewGuid())])
        { ConnectionRequirements = source.Select(value => value.Requirement).ToArray() };
        var codec = new Agentstration.Infrastructure.Work.YamlWorkDocumentCodec();
        var portable = codec.Read<WorkSharingProjectMetadata>(codec.Write(metadata), WorkSharingFormat.MaximumRecordBytes).Value!;
        foreach (var need in portable.ConnectionRequirements)
        {
            var own = assignments.Single(value => value.Requirement.Name == need.Name);
            var selected = new WorkProjectConnectionSelection(need, own.Assignment);
            var resolved = await right.Resolver.ResolveConnectionAsync(right.WorkspaceId, right.PrincipalId, selected, default);
            Assert.IsTrue(resolved.IsSuccess, resolved.ErrorCode);
            Assert.AreEqual(need.Target!.TargetId, resolved.Value!.Binding.Contract!.TargetId);
            Assert.AreNotEqual(source.Single(value => value.Requirement.Name == need.Name).Assignment.Service.Uid, resolved.Value.Service!.Service.Uid);
            var wrong = selected with { Requirement = need with { Target = new(need.Target.ProviderId, "wrong") } };
            Assert.AreEqual("work_project_connection_target_mismatch", (await right.Resolver.ResolveConnectionAsync(right.WorkspaceId, right.PrincipalId, wrong, default)).ErrorCode);
        }
        Assert.AreNotEqual(source[0].Assignment.Service.Address.ScopeRef, assignments[0].Assignment.Service.Address.ScopeRef);
        Assert.AreNotEqual(leftRoute.Contract!.Extension.Uid, rightRoute.Contract!.Extension.Uid);
    }

    [TestMethod]
    public async Task NamedRequirementsResolveMultipleServicesOfOneTypeWithoutSingletonRouting()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var route = fixture.AddConnection(false, "forge");
        var first = fixture.AddService(route, "backend"); var second = fixture.AddService(route, "frontend", targetId: "frontend");
        var definition = new WorkProjectDefinition("Two repositories", null, [], [])
        { ConnectionRequirements = [first.Requirement, second.Requirement], ConnectionAssignments = [first.Assignment, second.Assignment] };
        Assert.IsTrue(definition.Validate().IsSuccess);
        foreach (var selection in new[] { first, second })
        {
            var result = await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default);
            Assert.IsTrue(result.IsSuccess, result.ErrorCode); Assert.AreEqual(selection.Assignment.Service, result.Value!.Service!.Service);
            Assert.AreEqual(selection.Requirement.Name, result.Value.Binding.Name);
            Assert.AreEqual(selection.Requirement.Target!.TargetId, result.Value.Binding.Contract!.TargetId);
            Assert.IsTrue(WorkProjectConnectionRules.Evidence(result.Value));
        }
        Assert.AreEqual(1, fixture.Resources.Values.OfType<ConnectedServiceTypeResource>().Count());
        Assert.AreNotEqual(first.Assignment.Service.Uid, second.Assignment.Service.Uid);
    }

    [TestMethod]
    public async Task WorkspaceServiceCanUseAncestorTypeAndAdapterWithExplicitGrants()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var route = fixture.AddConnection(true, "tracking", ResourceScopeRef.Instance);
        var selection = fixture.AddService(route, ancestorType: true);
        var result = await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default);
        Assert.IsTrue(result.IsSuccess, result.ErrorCode);
        Assert.AreNotEqual(result.Value!.Service!.Service.Address.ScopeRef, result.Value.Service.Type.Address.ScopeRef);
        fixture.Denied.Add(result.Value.Service.Type.Uid);
        Assert.IsFalse((await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default)).IsSuccess);
    }

    [TestMethod]
    public async Task RetainedEvidenceSurvivesSerializationAndRefusesChangedParameterBeforeMcpEffect()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var selection = fixture.AddService(fixture.AddConnection(true, "tracking"), parameterRevision: true);
        var first = await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default);
        Assert.IsTrue(first.IsSuccess, first.ErrorCode);
        var restored = JsonSerializer.Deserialize<WorkProjectResolvedBinding>(JsonSerializer.Serialize(first.Value))!;
        Assert.IsTrue((await fixture.Contexts.RevalidateAsync(fixture.WorkspaceId, restored, default)).IsSuccess);
        var parameter = fixture.ParameterValues.Keys.Single();
        fixture.BeforeInvoke = () => fixture.ParameterValues[parameter] = JsonSerializer.SerializeToElement("revision-2");
        Assert.IsFalse((await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default)).IsSuccess,
            "A parameter changed after resolution must be checked again immediately before native invocation.");
        fixture.BeforeInvoke = null;
        Assert.AreEqual("work_project_contract_changed", (await fixture.Contexts.RevalidateAsync(fixture.WorkspaceId, restored, default)).ErrorCode);
    }

    [TestMethod]
    public async Task TargetMismatchScopeForgeryDisabledAndReplacedServicesFailWithoutDiscovery()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var selection = fixture.AddService(fixture.AddConnection(true, "tracking"));
        var mismatch = selection with { Requirement = selection.Requirement with { Target = new("foreign", "target") } };
        Assert.AreEqual("work_project_connection_target_mismatch", (await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, mismatch, default)).ErrorCode);
        var wrongScope = selection with
        {
            Assignment = selection.Assignment with
            {
                Service = selection.Assignment.Service with
                { Address = selection.Assignment.Service.Address with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } }
            }
        };
        Assert.IsFalse((await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, wrongScope, default)).IsSuccess);
        var service = (ConnectedServiceResource)fixture.Resources[selection.Assignment.Service.Address];
        fixture.Put(service with { Definition = service.Definition with { Enabled = false } });
        Assert.IsFalse((await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default)).IsSuccess);
        fixture.Put(service with { Uid = Guid.NewGuid() });
        Assert.IsFalse((await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default)).IsSuccess);
        Assert.AreEqual(0, fixture.DiscoveryCalls);
    }

    [TestMethod]
    public async Task IncompatibleProtectedExportsAndUntrustedPrincipalCannotSelectAConnection()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var selection = fixture.AddService(fixture.AddConnection(true, "tracking"));
        Assert.IsFalse((await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, Guid.NewGuid(), selection, default)).IsSuccess);
        var type = fixture.Resources.Values.OfType<ConnectedServiceTypeResource>().Single();
        var version = type.Definition.Versions.Single();
        fixture.Put(type with
        {
            Definition = type.Definition with
            {
                Versions = [version with
            { Exports = version.Exports.Select(value => value.Id == "providerId" ? value with { Protection = ConnectedServiceValueProtection.Secured } : value).ToArray() }]
            }
        });
        Assert.AreEqual("work_project_connection_incompatible", (await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default)).ErrorCode);
        Assert.AreEqual(0, fixture.DiscoveryCalls);
    }

    [TestMethod]
    public async Task LocalTransportMetadataCannotAddSecretForwardingAbsentFromTheLiveManifest()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var route = fixture.AddConnection(true, "tracking"); var selection = fixture.AddService(route);
        var tool = (Agentstration.Tools.ToolResource)fixture.Resources[route.Target.Address];
        fixture.Put(tool with
        {
            Definition = tool.Definition with
            {
                Metadata = new Dictionary<string, JsonElement>(tool.Definition.Metadata)
                { [Agentstration.Infrastructure.Work.WorkProjectConnectedServiceResolver.TransportMetadata] = JsonSerializer.SerializeToElement(new { version = "1.0" }) }
            }
        });
        var result = await fixture.Resolver.ResolveConnectionAsync(fixture.WorkspaceId, fixture.PrincipalId, selection, default);
        Assert.AreEqual("work_project_connection_incompatible", result.ErrorCode);
        Assert.HasCount(0, fixture.Calls);
    }

    [TestMethod]
    public void PortableNeedsContainNoLocalReferencesAndInvalidAssignmentsCannotBeSaved()
    {
        var fixture = new ProjectContractFixture(); var selected = fixture.AddService(fixture.AddConnection(true, "tracking"));
        var definition = new WorkProjectDefinition("Project", null, [], []) { ConnectionRequirements = [selected.Requirement] };
        Assert.IsTrue(definition.Validate().IsSuccess, "An unconfigured requirement is explicit valid project state.");
        var portable = JsonSerializer.Serialize(definition.ConnectionRequirements);
        Assert.IsFalse(portable.Contains(selected.Assignment.Service.Uid.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(portable.Contains("scopeRef", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse((definition with { ConnectionRequirements = [selected.Requirement, selected.Requirement] }).Validate().IsSuccess);
        Assert.IsFalse((definition with { ConnectionAssignments = [selected.Assignment with { Name = "unknown" }] }).Validate().IsSuccess);
        Assert.IsFalse((definition with { ConnectionAssignments = [selected.Assignment with { Exports = [] }] }).Validate().IsSuccess);
    }
}
