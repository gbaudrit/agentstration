extern alias afcSample;
extern alias atcSample;
using System.Text.Json;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Server;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Server;
using Agentstration.Tests.Work;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MemoryForgeProvider = afcSample::Afc.Samples.LocalForge.MemoryForgeProvider;
using MemoryTrackingProvider = atcSample::Atc.Samples.LocalTracking.MemoryTrackingProvider;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class ProjectContractMcpTests
{
    [TestMethod]
    public async Task GovernedProjectResolutionNegotiatesIndependentRealMcpContractsAndRejectsLiveSchemaDrift()
    {
        var fixture = new ProjectContractFixture { UseActualMcp = true };
        using var scope = fixture.Context.Push(fixture.Caller);
        var tracking = fixture.AddConnection(true, "tracking"); var forge = fixture.AddConnection(false, "forge");
        var tracker = new MemoryTrackingProvider(TimeProvider.System); var repository = new MemoryForgeProvider(TimeProvider.System);
        var trackingTarget = new AtcTargetRef("tracking-authority", "target", "binding", "revision-1");
        var forgeTarget = new AfcTargetRef("forge-authority", "target", "binding", "revision-1");
        var atc = new AtcMcpServer([new(trackingTarget, tracker, (_, _) => ValueTask.FromResult(
            new AtcAccessContext(trackingTarget, "fixture", "grant-1", AtcContract.Operations, [])))]);
        var afc = new AfcMcpServer([new(forgeTarget, repository, (_, _) => ValueTask.FromResult(
            new AfcAccessContext(forgeTarget, "fixture", "grant-1", AfcContract.Operations)))]);
        var wireCalls = new List<string>(); var drift = false;
        var builder = WebApplication.CreateBuilder(); builder.Configuration["AllowedHosts"] = "tracking.test;forge.test";
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler(async (request, token) =>
            {
                var a = await atc.ListToolsAsync(request, token); var b = await afc.ListToolsAsync(request, token);
                var tools = a.Tools.Concat(b.Tools).ToList();
                if (drift) tools.Single(tool => tool.Name == "atc_mutate").OutputSchema = JsonSerializer.SerializeToElement(new { type = "string" });
                return new ListToolsResult { Tools = tools };
            })
            .WithCallToolHandler((request, token) =>
            {
                wireCalls.Add(request.Params!.Name);
                return request.Params.Name.StartsWith("atc_", StringComparison.Ordinal) ? atc.CallToolAsync(request, token) : afc.CallToolAsync(request, token);
            });
        await using var app = builder.Build(); app.MapMcp("/mcp"); await app.StartAsync();
        fixture.McpHandler = app.GetTestServer().CreateHandler();
        var first = await fixture.ResolveAsync(tracking); var second = await fixture.ResolveAsync(forge);
        Assert.IsTrue(first.IsSuccess, first.ErrorCode + ": " + fixture.InvocationError); Assert.IsTrue(second.IsSuccess, second.ErrorCode);
        Assert.AreEqual(tracker.ImplementationId, first.Value!.Contract!.ImplementationId);
        Assert.AreEqual(repository.ImplementationId, second.Value!.Contract!.ImplementationId);
        CollectionAssert.AreEqual(new[] { "atc_describe", "afc_describe" }, wireCalls);
        Assert.AreEqual(0, repository.Count);
        drift = true;
        Assert.AreEqual("work_project_contract_changed", (await fixture.ResolveAsync(tracking)).ErrorCode);
        Assert.HasCount(2, wireCalls, "Live schema drift must fail before calls/tools, even when local discovery metadata is unchanged.");
        fixture.Denied.Add(tracking.Contract!.Provider.Uid); var discoveries = fixture.DiscoveryCalls;
        Assert.IsFalse((await fixture.Contexts.RevalidateAsync(fixture.WorkspaceId, first.Value, default)).IsSuccess);
        Assert.AreEqual(discoveries, fixture.DiscoveryCalls);
    }
}
