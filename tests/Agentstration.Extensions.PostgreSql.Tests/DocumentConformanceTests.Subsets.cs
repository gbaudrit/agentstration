using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Asp.Samples.StructuredDocuments;

namespace Agentstration.Extensions.PostgreSql.Tests;

public sealed partial class DocumentConformanceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GovernedDocumentSubsetAppliesBeforeAggregatesAndPagesAndBindsContinuation(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres);
        var publication = await host.Client.PrepareAsync(new("subset-prepare", DateTimeOffset.UtcNow.AddMinutes(2), "subset-publication",
            [new("a", Json("""{"status":"a","metrics":{"errorRate":1}}""")),
             new("b", Json("""{"status":"b","metrics":{"errorRate":100}}""")),
             new("c", Json("""{"status":"c","metrics":{"errorRate":2}}"""))]));
        var sealing = await AspDocumentClient.ConnectAsync(host.Mcp, "aspdoc", StructuredSampleAccess.Store, new("sample", publication.Id));
        await sealing.SealAsync(new("subset-seal", publication.Id, publication.Digest));
        host.Access.Policy = host.Access.Policy with { Put = false, Delete = false, Publish = false, DocumentIds = ["a", "c"] };
        var client = await AspDocumentClient.ConnectAsync(host.Mcp, "aspdoc", StructuredSampleAccess.Store, new("sample", publication.Id));
        var first = await client.QueryAsync(host.Query("{}", 1));
        Assert.AreEqual("a", first.Documents.Single().Id);
        Assert.IsNotNull(first.Cursor);
        var second = await client.QueryAsync(host.Query("{}", 1) with { Cursor = first.Cursor });
        Assert.AreEqual("c", second.Documents.Single().Id);
        Assert.IsNull(second.Cursor);
        var aggregate = await client.QueryAsync(host.Query("""{"$apply":"aggregate($count as Count,metrics/errorRate with sum as Total)"}"""));
        Assert.AreEqual(2, aggregate.Groups.Single().GetProperty("Count").GetInt32());
        Assert.AreEqual(3m, aggregate.Groups.Single().GetProperty("Total").GetDecimal());
        await Error("forbidden", () => client.GetAsync(new("outside-subset", "b")));
        host.Access.Policy = host.Access.Policy with { DocumentIds = ["b"] };
        await Error("invalid_reference", () => client.QueryAsync(host.Query("{}", 1) with { Cursor = first.Cursor }));
    }
}
