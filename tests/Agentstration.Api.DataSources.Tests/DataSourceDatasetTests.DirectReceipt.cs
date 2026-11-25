using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task DirectReceiptUsesTheLifecycleClockAndTerminalReplayCannotExtendItsHandoff()
    {
        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow().AddMinutes(21));
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<DataSourceDatasetAcquisitionService>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<DataSourceFlowOutcomeService>(p, clock));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var definition = Source("direct-clock", fixture, JsonSerializer.SerializeToElement(new { dataset = "ephemeral" }));
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(definition with
        {
            Definition = definition.Definition with
            { Dataset = definition.Definition.Dataset! with { Storage = null, Lifecycle = new() { AllowDirect = true } } }
        }, default);
        var datasets = factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>();
        var accepted = await datasets.StartAsync(source.Value, Empty, null, "projection-receipt", default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var acquisition = await WaitAsync(client, accepted.Value.Name);
        var receipt = acquisition.DirectResult!;
        Assert.AreEqual(clock.Now.AddMinutes(5), receipt.ExpiresAt);
        var output = await datasets.ReadDirectAsync(acquisition, receipt.RequestId, default);
        Assert.AreEqual("ephemeral", output[0].GetProperty("dataset").GetString());

        clock.Now = clock.Now.AddMinutes(6);
        var snapshot = await factory.Services.GetRequiredService<IDataSourceAcquisitionFlowGateway>().GetAsync(
            fixture.Context.WorkspaceId, acquisition.FlowRunId, fixture.Context.TenantId, fixture.Context.PrincipalId, default);
        await factory.Services.GetRequiredService<DataSourceFlowOutcomeService>().ApplyAsync(fixture.Context.WorkspaceId,
            acquisition.FlowRunId, DataSourceAcquisitionState.Succeeded, acquisition.CompletedAt!.Value, null,
            snapshot!.Output, acquisition.Progress!, null, default);
        var replay = (await factory.Services.GetRequiredService<DataSourceAcquisitionService>().GetAsync(
            ResourceNamespace.Default, acquisition.Name, default)).Value;
        Assert.AreEqual(receipt, replay.DirectResult);
        var expired = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => datasets.ReadDirectAsync(replay, receipt.RequestId, default));
        Assert.AreEqual("data_source_direct_content_unavailable", expired.Code);
    }
}
