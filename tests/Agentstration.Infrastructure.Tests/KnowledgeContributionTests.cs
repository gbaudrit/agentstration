using System.Text;
using System.Text.Json;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tests;

public sealed partial class KnowledgeMetadataTests
{
    private static KnowledgeProjectionInputEvidence ContributionInput(string binding, Guid uid) => new()
    {
        BindingName = binding,
        DataSourceUid = uid,
        DataSourceScopeRef = ResourceScopeRef.Instance,
        DataSourceNamespace = ResourceNamespace.Default,
        DataSourceName = "same-source",
        DataSourceGeneration = 1,
        AcquisitionId = "acquisition-" + binding,
        AcquisitionUid = Guid.NewGuid(),
        AcquisitionFlowRunId = "acquire",
        AcquiredAt = DateTimeOffset.UnixEpoch
    };

    [TestMethod]
    public async Task ContributionReadAndRegexNeverReadSiblingArtifactsAndCursorsCannotChangeContribution()
    {
        var f = new Fixture(artifacts: 4);
        using var scope = f.Push();
        var uid = Guid.NewGuid();
        var a = new KnowledgeContributionSelection("contracts", uid);
        var b = new KnowledgeContributionSelection("procedures", uid);
        var snapshot = f.Snapshot with
        {
            ProjectionInputs = [ContributionInput(a.BindingName, uid), ContributionInput(b.BindingName, uid)],
            Artifacts = f.Snapshot.Artifacts.Select((item, i) => item with { Contribution = i % 2 == 1 ? b : a }).ToArray()
        };
        f.Store.Add(snapshot);
        f.Publications.Content["document-0"] = Encoding.UTF8.GetBytes(Fixture.Text);
        f.Publications.Content["document-2"] = Encoding.UTF8.GetBytes(Fixture.Text);
        var first = await f.Access.ReadAsync(f.Id, new() { SnapshotId = snapshot.Name, Contribution = a, MaxCharacters = 2 }, default);
        Assert.AreEqual(a, first.Contribution);
        Assert.IsNotNull(first.NextCursor);
        var error = await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.ReadAsync(f.Id,
            new() { Cursor = first.NextCursor, Contribution = b }, default));
        Assert.AreEqual("knowledge_access_cursor_contribution_mismatch", error.Code);
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.ReadAsync(f.Id,
            new() { Cursor = first.NextCursor }, default));
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.ReadAsync(f.Id,
            new() { SnapshotId = snapshot.Name, DocumentId = "document-1", Contribution = a }, default));
        var continued = await f.Access.ReadAsync(f.Id, new() { Cursor = first.NextCursor, Contribution = a }, default);
        Assert.IsTrue(continued.Chunks.All(c => c.DocumentId != "document-1"));
        var search = await f.Access.SearchRegexAsync(f.Id, new() { SnapshotId = snapshot.Name, Contribution = a, Pattern = "Nantes" }, default);
        Assert.IsTrue(search.Matches.All(c => c.DocumentId != "document-1"));
        Assert.IsFalse(f.Publications.Reads.Contains("document-1"));
    }

    [TestMethod]
    public async Task StructuredContributionPushesExactDocumentSelectionToGatewayAndRetainsItAcrossPages()
    {
        var f = new Fixture(artifacts: 3, structured: true);
        using var scope = f.Push();
        var a = new KnowledgeContributionSelection("invoices", Guid.NewGuid());
        f.Store.Add(f.Snapshot with
        {
            ProjectionInputs = [ContributionInput(a.BindingName, a.DataSourceUid)],
            Artifacts = f.Snapshot.Artifacts.Select((item, i) => item with { Contribution = i == 1 ? a : new("derived", f.Source.Uid) }).ToArray()
        });
        var first = await f.Access.QueryDocumentsAsync(f.Id, new()
        {
            SnapshotId = f.Snapshot.Name,
            Contribution = a,
            Profile = f.Documents.Profile,
            Query = JsonSerializer.SerializeToElement(new { })
        }, default);
        Assert.AreEqual(a, first.Contribution);
        CollectionAssert.AreEqual(new[] { "document-1" }, f.Documents.Queries.Single().DocumentIds!.ToArray());
        Assert.IsNotNull(first.NextCursor);
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.QueryDocumentsAsync(f.Id, new() { Cursor = first.NextCursor }, default));
        _ = await f.Access.QueryDocumentsAsync(f.Id, new() { Cursor = first.NextCursor, Contribution = a }, default);
        Assert.IsTrue(f.Documents.Queries.All(q => q.DocumentIds!.SequenceEqual(["document-1"])));
        Assert.AreEqual(0, f.Publications.Reads.Count);
    }

    [TestMethod]
    public async Task DiscoveryUsesImmutableExposureAndSnapshotMetadataWithoutContentAccessOrWrites()
    {
        var f = new Fixture();
        using var scope = f.Push();
        var selection = new KnowledgeContributionSelection("contracts", Guid.NewGuid());
        f.Store.Add(f.Snapshot with
        {
            ProjectionInputs = [ContributionInput(selection.BindingName, selection.DataSourceUid)],
            Artifacts = [f.Snapshot.Artifacts[0] with { Contribution = selection }]
        });
        var candidate = new KnowledgeToolProposal
        {
            Route = "contracts-b70461ec.read",
            DefinitionName = "contracts-read",
            Operation = KnowledgeSourceOperation.Read,
            Contribution = selection,
            DisplayName = "Contracts",
            Description = "Read supplier contracts",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            ContractDigest = "contract"
        };
        f.Store.Add(new KnowledgeToolExposureVersion
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "KnowledgeToolExposureVersion",
            Metadata = new() { Name = "published-a" },
            ScopeRef = f.Source.ScopeRef,
            SourceUid = f.Source.Uid,
            DisplayName = "Supplier Knowledge",
            Version = "1.0.0",
            Proposal = new()
            {
                SourceUid = f.Source.Uid,
                SourceGeneration = 1,
                Digest = "proposal-a",
                ToolSetName = "supplier-access",
                Tools = [candidate]
            }
        });
        f.Authorization.Denied.UnionWith([Agentstration.Identity.Contracts.AuthorizationPermissions.RunsExecute, Agentstration.Identity.Contracts.AuthorizationPermissions.ArtifactsReadContent]);
        var result = await f.Access.DescribeAsync(f.Id, new("published-a"), default);
        Assert.AreEqual("proposal-a", result.ProposalDigest);
        Assert.AreEqual(f.Snapshot.Name, result.SnapshotId);
        StringAssert.Contains(result.InvocationInstructions!, "request.snapshotId");
        StringAssert.Contains(result.InvocationInstructions!, "Never send snapshotId beside request");
        Assert.AreEqual(selection, result.Contributions.Single().Selection);
        Assert.AreEqual("overview", result.DetailLevel);
        Assert.IsEmpty(result.Tools);
        Assert.IsEmpty(result.Contributions.Single().DocumentIds);
        Assert.IsEmpty(result.Contributions.Single().Schemas);
        Assert.AreEqual("Contracts", result.ToolSummaries.Single().DisplayName);
        Assert.AreEqual("contracts-b70461ec", result.Contributions.Single().Input);
        Assert.AreEqual("contracts-b70461ec", result.Contributions.Single().DescribeArguments.GetProperty("request").GetProperty("input").GetString());
        var aliased = await f.Access.DescribeAsync(f.Id, new("published-a", Input: "contracts-b70461ec"), default);
        Assert.AreEqual(selection, aliased.Contributions.Single().Selection);
        var detailed = await f.Access.DescribeAsync(f.Id, new("published-a", Input: "contracts"), default);
        Assert.AreEqual("input", detailed.DetailLevel);
        Assert.AreEqual("Contracts", detailed.Tools.Single().DisplayName);
        Assert.HasCount(1, detailed.Contributions.Single().DocumentIds);
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.DescribeAsync(f.Id, new("published-a", Input: "unknown"), default));
        Assert.AreEqual(KnowledgeContentAvailability.Unknown, result.ContentAvailability);
        Assert.AreEqual(0, f.Store.Writes);
        Assert.AreEqual(0, f.Publications.Reads.Count);
        Assert.AreEqual(0, f.Resolver.Calls);
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.DescribeAsync(f.Id, new("unpublished"), default));
        var restricted = await f.Access.DescribeAsync(f.Id, new("published-a"), default, [], agent: true);
        Assert.IsEmpty(restricted.Tools);
        Assert.IsEmpty(restricted.Access);
        Assert.IsEmpty(restricted.Contributions);
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.DescribeAsync(f.Id, new("published-a", Input: "contracts"), default, [], agent: true));
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.DescribeAsync(f.Id, new("published-a", Input: "contracts-b70461ec"), default, [], agent: true));
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.DescribeAsync(f.Id, new("published-a"), default, agent: true));
        var published = (await f.Store.GetExactAsync<KnowledgeToolExposureVersion>(new(f.Source.ScopeRef!.Value,
            f.Source.Namespace, "KnowledgeToolExposureVersion", "published-a"), default))!.Value;
        f.Store.Add(published with
        {
            Proposal = published.Proposal with
            {
                Tools = [candidate,
                    candidate with { Route = "other.read", Contribution = new("contracts-b70461ec", Guid.NewGuid()) }]
            }
        });
        var ambiguous = await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.DescribeAsync(f.Id,
            new("published-a", Input: "contracts-b70461ec"), default));
        Assert.AreEqual("knowledge_describe_input_not_exposed", ambiguous.Code);

    }
    [TestMethod]
    [DataRow("workspace", false, false, false, false)]
    [DataRow("workspace", false, false, true, false)]
    [DataRow("tenant", false, false, false, false)]
    [DataRow("tenant", false, false, true, false)]
    [DataRow("instance", false, false, false, false)]
    [DataRow("instance", false, false, true, false)]
    [DataRow("instance", true, false, false, false)]
    [DataRow("instance", true, false, true, false)]
    [DataRow("instance", false, true, false, false)]
    [DataRow("instance", false, true, true, false)]
    [DataRow("instance", false, false, true, true)]
    public async Task RuntimeDiscoveryAndODataSchemasRefreshOldPublicationsInEachOwnerScope(string owner, bool replaceSource, bool noRequestContext, bool flatArguments, bool mixedArguments)
    {
        var f = new Fixture(owner);
        using var scope = noRequestContext ? null : f.Push();
        var original = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"request":{"type":"object","properties":{"query":{}},"additionalProperties":false}},"additionalProperties":false}
            """);
        var wrappedOriginal = original;
        if (flatArguments) original = original.GetProperty("properties").GetProperty("request");
        var profile = new StorageQueryProfilePin("odata/4.01", "1.0", "odata-options+json", "model");
        var tools = new List<IAgentTool>();
        foreach (var operation in new[] { "describe", "input.query_documents" })
        {
            var toolSchema = mixedArguments && operation != "describe" ? wrappedOriginal : original;
            var name = "knowledge-" + operation.Replace('.', '-');
            var bindings = new List<ToolArgumentBinding> { new LiteralToolArgumentBinding
            {
                Path = "/source", Value = JsonSerializer.SerializeToElement(new
                {
                    name = f.Source.Name, @namespace = f.Source.Namespace.Value,
                    scopeRef = f.Source.ScopeRef!.Value.Value, uid = f.Source.Uid
                })
            } };
            if (operation != "describe") bindings.Add(new LiteralToolArgumentBinding
            {
                Path = "/request/profile",
                Value = JsonSerializer.SerializeToElement(profile, JsonSerializerOptions.Web)
            });
            f.Store.Add(new ToolDefinitionVersionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolDefinitionVersion,
                Metadata = new()
                {
                    Name = name,
                    Annotations = new Dictionary<string, string>
                    {
                        ["agentstration.io/knowledge-source-uid"] = f.Source.Uid.ToString("D"),
                        ["agentstration.io/knowledge-operation"] = operation
                    }
                },
                ScopeRef = f.Source.ScopeRef,
                DefinitionName = name,
                DefinitionUid = Guid.NewGuid(),
                PublishedAt = DateTimeOffset.UnixEpoch,
                Definition = new RelayToolDefinitionProperties
                {
                    DisplayName = "Published",
                    Description = "Old usage",
                    InputSchema = toolSchema,
                    Bindings = bindings,
                    Target = new() { Name = "native", ScopeRef = f.Source.ScopeRef!.Value, Uid = Guid.NewGuid(), Generation = 1 }
                }
            });
            tools.Add(new PresentationProbeTool(name, toolSchema, f.Source.ScopeRef));
        }
        if (replaceSource) f.Store.Add(f.Source with { Uid = Guid.NewGuid(), Definition = f.Source.Definition with { Description = "Private replacement Source purpose" } });
        var enricher = new KnowledgeAgentPresentationEnricher(f.Store, new KnowledgeToolMetadataService(f.Store, f.Sources, f.Context), f.Context);
        var result = await enricher.EnrichAsync(new("Authored instructions", tools), default);
        StringAssert.Contains(result.Instructions, "Knowledge Sources assigned");
        StringAssert.Contains(result.Instructions, mixedArguments ? "Assigned Tools use different argument layouts" : flatArguments ? "do not add a request wrapper" : "Never send snapshotId beside request");
        StringAssert.Contains(result.Instructions, "Consult data from every exposed input");
        StringAssert.Contains(result.Instructions, "issue its structured Tool call now");
        StringAssert.Contains(result.Instructions, "Do not finish with only a plan");
        StringAssert.Contains(result.Instructions, "unavailable or unconsulted source/input");
        if (replaceSource || noRequestContext)
        {
            StringAssert.Contains(result.Instructions, "unavailable");
            Assert.IsFalse(result.Instructions.Contains("Private replacement Source purpose", StringComparison.Ordinal));
            Assert.IsFalse(result.Tools.Any(tool => tool.Description!.Contains("Private replacement Source purpose", StringComparison.Ordinal)));
        }
        var query = result.Tools.Single(tool => tool.Name.Contains("query", StringComparison.Ordinal));
        var arguments = flatArguments && !mixedArguments ? query.InputSchema : query.InputSchema.GetProperty("properties").GetProperty("request");
        Assert.AreEqual("object", arguments.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        StringAssert.Contains(query.Description!, "eq/ne/gt/ge/lt/le");
        Assert.IsTrue(query.RequiresApproval);
        Assert.AreEqual(tools[1].Id, query.Id);
        Assert.AreEqual(tools[1].ExternalId, query.ExternalId);
        Assert.AreEqual(tools[1].ScopeRef, query.ScopeRef);
        Assert.IsTrue(JsonElement.DeepEquals(mixedArguments ? wrappedOriginal : original, tools[1].InputSchema));
        Assert.AreEqual(0, f.Store.Writes);
        Assert.AreEqual(0, f.Publications.Reads.Count);
        Assert.AreEqual(0, f.Resolver.Calls);
    }

    private sealed record PresentationProbeTool(string Name, JsonElement InputSchema, ResourceScopeRef? ScopeRef) : IAgentTool
    {
        public string Id => "agentstration." + Name;
        public string? Description => "Old usage";
        public string? ProviderId => AgentstrationToolProvider.Name;
        public ResourceNamespace? Namespace => ResourceNamespace.Default;
        public string? ExternalId => Name;
        public JsonElement? OutputSchema => null;
        public bool RequiresApproval => true;
    }

    [TestMethod]
    public async Task HistoricalMembershipUsesExactPreparedEvidenceAndRejectsAmbiguousAttribution()
    {
        var f = new Fixture();
        using var scope = f.Push();
        var uid = Guid.NewGuid();
        var input = ContributionInput("contracts", uid) with
        {
            PreparedArtifacts = [new() { ArtifactId = "document-0", Kind = KnowledgeArtifactKind.Durable, Disposition = KnowledgeArtifactDisposition.Publishable }]
        };
        var snapshot = f.Snapshot with { ProjectionInputs = [input] };
        f.Store.Add(snapshot);
        f.Publications.Content["document-0"] = Encoding.UTF8.GetBytes(Fixture.Text);
        var selection = new KnowledgeContributionSelection("contracts", uid);
        var read = await f.Access.ReadAsync(f.Id, new() { SnapshotId = snapshot.Name, Contribution = selection }, default);
        Assert.AreEqual(Fixture.Text, read.Chunks.Single().Content);
        f.Store.Add(snapshot with { ProjectionInputs = [input, input with { BindingName = "other-view" }] });
        var error = await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.ReadAsync(f.Id,
            new() { SnapshotId = snapshot.Name, Contribution = selection }, default));
        Assert.AreEqual("knowledge_contribution_evidence_unavailable", error.Code);
        Assert.AreEqual(new KnowledgeContributionSelection("derived", f.Source.Uid),
            KnowledgeContributions.ForArtifact(f.Source.Uid, [input, input with { BindingName = "other-view" }], "document-0"));
    }

}
