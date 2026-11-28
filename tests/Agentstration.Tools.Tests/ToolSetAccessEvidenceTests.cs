using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Tools.Tests;

[TestClass]
public sealed class ToolSetAccessEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public void GovernedDraftRequiresExactMemberIdentityAndSameRegistrationAuthority()
    {
        var registration = Guid.NewGuid();
        var tool = Tool(ResourceScopeRef.Instance, ResourceGovernance.Extension("registration-a", "tests.a", "1.0.0", registration));
        var set = Set(tool, ResourceScopeRef.Instance, ResourceGovernance.Extension("registration-a", "tests.a", "2.0.0", registration));
        Assert.IsTrue(Sources(tool, set).Contains(set.Uid), "A supplied extension version change does not replace its authority.");
        Assert.IsEmpty(Sources(tool, set with { Definition = set.Definition with { Members = [set.Definition.Members[0] with { ToolUid = Guid.NewGuid() }] } }));
        Assert.IsEmpty(Sources(tool, set with { Definition = set.Definition with { Members = [set.Definition.Members[0] with { ToolGeneration = 99 }] } }));
        Assert.IsEmpty(Sources(tool, set with { Governance = ResourceGovernance.Extension("registration-b", "tests.b", "1.0.0", Guid.NewGuid()) }));
    }

    [TestMethod]
    public void ConsumerOwnedToolSetCannotConferAccessToAncestorToolEvenWithCopiedUidEvidence()
    {
        var governance = ResourceGovernance.Extension("registration-a", "tests.a", "1.0.0", Guid.NewGuid());
        var tool = Tool(ResourceScopeRef.Instance, governance);
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var set = Set(tool, workspace, governance);
        Assert.IsEmpty(Sources(tool, set));
        Assert.IsEmpty(Sources(tool, Set(tool, ResourceScopeRef.Instance, ResourceGovernance.User)),
            "An operator draft is not authority without an exact published membership.");
    }

    [TestMethod]
    public void PublishedVersionUsesLiveAccessOfItsExactOwningToolSet()
    {
        var governance = ResourceGovernance.Extension("registration-a", "tests.a", "1.0.0", Guid.NewGuid());
        var tool = Tool(ResourceScopeRef.Instance, governance);
        var set = Set(tool, ResourceScopeRef.Instance, governance);
        var version = new ToolSetVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolSetVersion, Uid = Guid.NewGuid(),
            ScopeRef = ResourceScopeRef.Instance, Metadata = new() { Name = "reads-v1" }, ToolSetName = set.Name,
            ToolSetUid = set.Uid, ToolSetGeneration = set.Generation, Version = "1.0.0", DefinitionHash = "hash", PublishedAt = DateTimeOffset.UnixEpoch
        };
        var documents = new[] { Document(set), Document(version) }.ToDictionary(value => value.Identity.Uid);
        Assert.AreEqual(set.Uid, new ToolSetAccessRelationships().Describe(documents[version.Uid], documents).PolicySourceUid);
        documents[set.Uid] = Document(set with { Uid = Guid.NewGuid() });
        Assert.IsFalse(new ToolSetAccessRelationships().Describe(documents[version.Uid], documents).Valid);
    }

    private static IReadOnlyList<Guid> Sources(ToolResource tool, ToolSetResource set)
    {
        var documents = new[] { Document(tool), Document(set) }.ToDictionary(value => value.Identity.Uid);
        return new ToolSetAccessRelationships().Describe(documents[tool.Uid], documents).InheritFrom;
    }
    private static ToolResource Tool(ResourceScopeRef owner, ResourceGovernance governance) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.Tool, Uid = Guid.NewGuid(), Generation = 1,
        ScopeRef = owner, Governance = governance, Metadata = new() { Name = "read" },
        Definition = new() { DisplayName = "Read", Enabled = false }
    };
    private static ToolSetResource Set(ToolResource tool, ResourceScopeRef owner, ResourceGovernance governance) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolSet, Uid = Guid.NewGuid(), Generation = 1,
        ScopeRef = owner, Governance = governance, Metadata = new() { Name = "reads" },
        Definition = new() { DisplayName = "Reads", Members = [new() { Tool = new(tool.Name, tool.ScopeRef, tool.Namespace),
            ToolUid = tool.Uid, ToolGeneration = tool.Generation, Capability = "read", Route = "default" }] }
    };
    private static ResourceAccessDocument Document(Resource resource) => new(new(resource.Uid, resource.ScopeRef!.Value,
        resource.Namespace, resource.Kind, resource.Name, resource.Generation, null, "\"resource\""), resource.Governance,
        JsonSerializer.SerializeToElement(resource, resource.GetType(), JsonOptions), null);
}
