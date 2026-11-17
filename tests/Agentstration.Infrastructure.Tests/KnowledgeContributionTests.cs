using System.Text;
using System.Text.Json;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;

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
            Route = "contracts.read",
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
        Assert.AreEqual(selection, result.Contributions.Single().Selection);
        Assert.AreEqual("overview", result.DetailLevel);
        Assert.IsEmpty(result.Tools);
        Assert.IsEmpty(result.Contributions.Single().DocumentIds);
        Assert.IsEmpty(result.Contributions.Single().Schemas);
        Assert.AreEqual("Contracts", result.ToolSummaries.Single().DisplayName);
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
        await Assert.ThrowsAsync<KnowledgeAccessException>(() => f.Access.DescribeAsync(f.Id, new("published-a"), default, agent: true));

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
