using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Tests.Work;
using Agentstration.Tools;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class WorkProjectContractResolverTests
{
    [TestMethod]
    public async Task TrackingWithoutRepositoryAndForgeUseIndependentAuthoritiesAndStableEvidence()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var tracking = fixture.AddConnection(true, "tracking"); var forge = fixture.AddConnection(false, "forge");
        var first = await fixture.ResolveAsync(tracking); var second = await fixture.ResolveAsync(forge);
        Assert.IsTrue(first.IsSuccess, first.ErrorCode); Assert.IsTrue(second.IsSuccess, second.ErrorCode);
        Assert.AreNotEqual(first.Value!.Contract!.Provider.Uid, second.Value!.Contract!.Provider.Uid);
        Assert.HasCount(4, first.Value.Contract.Contributions); Assert.HasCount(5, second.Value.Contract.Contributions);
        Assert.IsTrue(WorkProjectContextRules.ValidEvidence(tracking, first.Value.Contract));
        Assert.AreEqual(first.Value.Contract.MappingDigest, (await fixture.ResolveAsync(tracking)).Value!.Contract!.MappingDigest);
        Assert.IsTrue(fixture.Calls.All(call => call.ToolName.EndsWith(".describe", StringComparison.Ordinal)));
        var restored = JsonSerializer.Deserialize<WorkProjectResolvedBinding>(JsonSerializer.Serialize(first.Value))!;
        Assert.IsTrue((await fixture.Contexts.RevalidateAsync(fixture.WorkspaceId, restored, default)).IsSuccess);
    }

    [TestMethod]
    public async Task CapturedProofRejectsChangedImplementationOrRevocationWithoutRewritingEvidence()
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var binding = fixture.AddConnection(true, "tracking");
        var captured = (await fixture.ResolveAsync(binding)).Value!; var original = JsonSerializer.Serialize(captured);
        fixture.ImplementationVersion = "2.0.0";
        Assert.AreEqual("work_project_contract_changed", (await fixture.Contexts.RevalidateAsync(fixture.WorkspaceId, captured, default)).ErrorCode);
        Assert.IsTrue((await fixture.ResolveAsync(binding)).IsSuccess, "New captures observe current implementation evidence; retained captures never adopt it.");
        fixture.Denied.Add(binding.Contract!.Provider.Uid); var calls = fixture.DiscoveryCalls;
        Assert.AreEqual("work_project_binding_unavailable", (await fixture.Contexts.RevalidateAsync(fixture.WorkspaceId, captured, default)).ErrorCode);
        Assert.AreEqual(calls, fixture.DiscoveryCalls); Assert.AreEqual(original, JsonSerializer.Serialize(captured));
    }

    [TestMethod]
    [DataRow("missing-metadata")]
    [DataRow("schema")]
    [DataRow("version")]
    [DataRow("provider-replaced")]
    [DataRow("extension-replaced")]
    [DataRow("tool-disabled")]
    [DataRow("denied-tool")]
    [DataRow("foreign-extension")]
    [DataRow("replacement-during-discovery")]
    public async Task InvalidSelectionsFailBeforeDescribe(string change)
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var binding = fixture.AddConnection(true, "tracking");
        var tool = (ToolResource)fixture.Resources[binding.Target.Address];
        if (change == "missing-metadata") fixture.Put(tool with { Definition = tool.Definition with { Metadata = new Dictionary<string, JsonElement>() } });
        if (change == "schema") fixture.Put(tool with { Definition = tool.Definition with { Schema = tool.Definition.Schema! with { Output = JsonSerializer.SerializeToElement(new { type = "string" }) } } });
        if (change == "version") binding = binding with { Contract = binding.Contract! with { Version = "2.0" } };
        if (change == "provider-replaced") fixture.Put(fixture.Resources[binding.Contract!.Provider.Address] with { Uid = Guid.NewGuid() });
        if (change == "extension-replaced") fixture.Put(fixture.Resources[binding.Contract!.Extension.Address] with { Uid = Guid.NewGuid() });
        if (change == "tool-disabled") fixture.Put(tool with { Definition = tool.Definition with { Enabled = false } });
        if (change == "denied-tool") fixture.Denied.Add(tool.Uid);
        if (change == "foreign-extension") binding = binding with
        {
            Contract = binding.Contract! with
            { Extension = binding.Contract.Extension with { Address = binding.Contract.Extension.Address with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } } }
        };
        if (change == "replacement-during-discovery") fixture.BeforeList = () => fixture.Put(tool with { Uid = Guid.NewGuid() });
        var result = await fixture.ResolveAsync(binding);
        Assert.IsFalse(result.IsSuccess); Assert.HasCount(0, fixture.Calls);
    }

    [TestMethod]
    public async Task ForeignCallersFailBeforeDiscoveryAndUnsupportedOrForeignRepliesNeverBecomeEvidence()
    {
        var fixture = new ProjectContractFixture(); var binding = fixture.AddConnection(true, "tracking");
        Assert.IsFalse((await fixture.ResolveAsync(binding)).IsSuccess); Assert.AreEqual(0, fixture.MetadataReads);
        using var scope = fixture.Context.Push(fixture.Caller);
        Assert.IsFalse((await fixture.Resolver.ResolveAsync(WorkspaceId.New(), fixture.PrincipalId, binding, default)).IsSuccess);
        Assert.AreEqual(0, fixture.MetadataReads);
        fixture.Allowed = false; Assert.IsFalse((await fixture.ResolveAsync(binding)).IsSuccess); Assert.AreEqual(0, fixture.DiscoveryCalls);
        fixture.Allowed = true;
        fixture.DeniedPermissions.Add(Agentstration.Identity.Contracts.AuthorizationPermissions.RunsExecute);
        Assert.AreEqual("work_project_binding_denied", (await fixture.ResolveAsync(binding)).ErrorCode);
        Assert.AreEqual(0, fixture.DiscoveryCalls); fixture.DeniedPermissions.Clear(); fixture.ReadOnly = true;
        var mutation = binding with { Contract = binding.Contract! with { RequiredOperations = ["create"] } };
        Assert.AreEqual("work_project_contract_operation_unsupported", (await fixture.ResolveAsync(mutation)).ErrorCode);
        fixture.ForeignTarget = true;
        Assert.AreEqual("work_project_contract_invalid", (await fixture.ResolveAsync(binding)).ErrorCode);
        Assert.IsTrue(fixture.Calls.All(call => call.ToolName.EndsWith(".describe", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FreshAepRouteChangesAreRejectedAtInvocationBeforeConnectingToTheMappedServer()
    {
        var fixture = new ProjectContractFixture { UseActualMcp = true }; using var scope = fixture.Context.Push(fixture.Caller);
        var binding = fixture.AddConnection(true, "tracking");
        fixture.BeforeInvoke = () =>
        {
            var manifest = fixture.Manifests["tracking.test"];
            fixture.Manifests["tracking.test"] = manifest with { Extension = manifest.Extension with { Version = "2.0.0" } };
        };
        Assert.AreEqual("work_project_contract_changed", (await fixture.ResolveAsync(binding)).ErrorCode);
        Assert.HasCount(1, fixture.Calls); Assert.AreEqual(2, fixture.DiscoveryCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task IncompatibleDeclaredMajorNeverFallsBackToNativeTools(bool tracking)
    {
        var fixture = new ProjectContractFixture(); using var scope = fixture.Context.Push(fixture.Caller);
        var binding = fixture.AddConnection(tracking, tracking ? "tracking" : "forge");
        var host = tracking ? "tracking.test" : "forge.test"; var manifest = fixture.Manifests[host];
        var key = tracking ? Agentstration.Atc.Abstractions.AtcContract.Id : Agentstration.Afc.Abstractions.AfcContract.Id;
        var declaration = JsonSerializer.SerializeToElement(new { contractId = key, version = "2.0", toolPrefix = "incompatible", schemaDigests = new Dictionary<string, string>() });
        fixture.Manifests[host] = manifest with
        {
            Contributions = manifest.Contributions with
            { Tools = manifest.Contributions.Tools!.Select(value => value with { Metadata = new Dictionary<string, JsonElement> { [key] = declaration } }).ToArray() }
        };
        Assert.AreEqual("work_project_contract_invalid", (await fixture.ResolveAsync(binding)).ErrorCode);
        Assert.HasCount(0, fixture.Calls);
    }

    [TestMethod]
    public void ContractBindingsRequireExplicitCompleteSelectionsAndUniquePurposes()
    {
        var fixture = new ProjectContractFixture(); var binding = fixture.AddConnection(true, "tracking");
        Assert.IsTrue(WorkProjectContextRules.Valid([binding]));
        foreach (var invalid in new[]
        {
            binding with { Contract = null }, binding with { Purpose = WorkProjectBindingPurpose.Repository },
            binding with { Contract = binding.Contract! with { RequiredOperations = [] } },
            binding with { Contract = binding.Contract! with { ProviderId = "" } },
            binding with { Contract = binding.Contract! with { Extension = binding.Contract.Extension with { Uid = Guid.Empty } } }
        }) Assert.IsFalse(WorkProjectContextRules.Valid([invalid]));
        Assert.IsTrue(WorkProjectContextRules.Valid([binding, binding with { Name = "other" }]));
    }
}
