using System.Net.Http.Headers;
using System.Text.Json;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Client;
using Atc.Samples.LocalTracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Atc.Tests;

internal sealed class AtcTestHost : IAsyncDisposable
{
    public TestClock Clock { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Http { get; private set; } = null!;
    public McpClient Mcp { get; private set; } = null!;
    public AtcClient Client { get; private set; } = null!;
    public SampleAccess Access => Factory.Services.GetRequiredService<SampleAccess>();
    public AtcTargetRef Target { get; private set; } = null!;

    public AtcTestHost(Action<McpServerOptions>? configure = null, Func<TimeProvider, IAtcProvider>? provider = null)
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
            if (provider is not null) { services.RemoveAll<IAtcProvider>(); services.AddSingleton(provider(Clock)); }
            if (configure is not null) services.PostConfigure(configure);
        }));
    }
    public async Task StartAsync(string target = "team", bool other = false)
    {
        Http = Factory.CreateClient(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other ? Access.OtherToken : Access.Token);
        Mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, Http));
        Target = target == "team" ? Access.Team : target == "readonly" ? Access.ReadOnly : Access.Repository;
        Client = await AtcClient.ConnectAsync(Mcp, Target);
    }
    public AtcMutationRequest Mutation(AtcChange change, string? key = null, AtcMutationMode mode = AtcMutationMode.Execute) =>
        new(Guid.NewGuid().ToString("N"), key ?? Guid.NewGuid().ToString("N"), AtcContract.Digest(Target, change), change, mode);
    public async Task<AtcItem> CreateAsync(string title = "fixture") => (await Client.MutateAsync(Mutation(new AtcCreate(title)))).Item!;
    public async Task<CallToolResult> RawAsync(string operation, object arguments, AtcTargetRef? target = null)
    {
        var wire = JsonSerializer.SerializeToElement(new AtcInvocation<object>(target ?? Target, arguments), AtcContract.Json);
        return await Mcp.CallToolAsync("atc_" + operation, wire.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value));
    }
    public static async Task Error(string code, Func<Task> action) => Assert.AreEqual(code, (await Assert.ThrowsAsync<AtcException>(action)).Code);
    public async ValueTask DisposeAsync()
    {
        if (Mcp is not null) await Mcp.DisposeAsync(); Http?.Dispose(); await Factory.DisposeAsync();
    }
    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
}
