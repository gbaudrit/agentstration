using System.Net;
using System.Net.Http.Json;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolGovernanceAuditEndpointTests
{
    [TestMethod]
    public async Task EndpointBindsScopedFiltersAndReturnsSafeAuditContract()
    {
        var reader = new RecordingReader();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IToolGovernanceAuditReader>();
                services.AddSingleton<IToolGovernanceAuditReader>(reader);
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/tool-governance/runtime/run-1?afterSequence=4&limit=25&toolCallId=logical-call&invocationId=attempt-2&toolId=lookup&hookId=managed%3Aguard&resourceGeneration=7&decision=denied");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsNotNull(reader.Query);
        Assert.AreEqual(ToolExecutionOwnerKind.RuntimeRun, reader.Query.OwnerKind);
        Assert.AreEqual("run-1", reader.Query.RunId);
        Assert.AreEqual(4L, reader.Query.AfterSequence);
        Assert.AreEqual(25, reader.Query.Limit);
        Assert.AreEqual("logical-call", reader.Query.ToolCallId);
        Assert.AreEqual("attempt-2", reader.Query.InvocationId);
        Assert.AreEqual("lookup", reader.Query.ToolId);
        Assert.AreEqual("managed:guard", reader.Query.HookId);
        Assert.AreEqual(7L, reader.Query.ResourceGeneration);
        Assert.AreEqual(ToolExecutionHookEvaluationKind.Denied, reader.Query.Decision);
        Assert.AreNotEqual(Guid.Empty, reader.Query.WorkspaceId.Value);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("arguments", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("result", body, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task EndpointRejectsUnknownDecisionBeforeReadingAuditStore()
    {
        var reader = new RecordingReader();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IToolGovernanceAuditReader>();
                services.AddSingleton<IToolGovernanceAuditReader>(reader);
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/tool-governance/flow/run-1?decision=rewritten");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsNull(reader.Query);
    }

    private sealed class RecordingReader : IToolGovernanceAuditReader
    {
        public ToolGovernanceAuditQuery? Query { get; private set; }

        public Task<ToolGovernanceAuditPage> ListAsync(
            ToolGovernanceAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            return Task.FromResult(new ToolGovernanceAuditPage(
                [new ToolGovernanceAuditRecord
                {
                    OwnerKind = query.OwnerKind,
                    RunId = query.RunId,
                    Sequence = 5,
                    Timestamp = DateTimeOffset.UnixEpoch,
                    ToolCallId = "logical-call",
                    InvocationId = "attempt-1",
                    ToolId = "lookup",
                    ToolName = "lookup",
                    Evaluations = [new ToolExecutionHookEvaluation(
                        new ToolExecutionHookIdentity("managed:guard", 10),
                        ToolExecutionHookEvaluationKind.Denied,
                        "blocked")]
                }],
                null));
        }
    }
}
