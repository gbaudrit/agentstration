using System.Net.Http.Headers;
using System.Text.Json;
using Afc.Samples.LocalForge;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Afc.Tests;

internal sealed class AfcTestHost : IAsyncDisposable
{
    public TestClock Clock { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Http { get; private set; } = null!;
    public McpClient Mcp { get; private set; } = null!;
    public AfcClient Client { get; private set; } = null!;
    public SampleAccess Access => Factory.Services.GetRequiredService<SampleAccess>();
    public AfcTargetRef Target { get; private set; } = null!;

    public AfcTestHost(Action<McpServerOptions>? configure = null, Func<TimeProvider, IAfcProvider>? provider = null)
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
            if (provider is not null) { services.RemoveAll<IAfcProvider>(); services.AddSingleton(provider(Clock)); }
            if (configure is not null) services.PostConfigure(configure);
        }));
    }
    public async Task StartAsync(string target = "repository", bool other = false)
    {
        Http = Factory.CreateClient(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other ? Access.OtherToken : Access.Token);
        Mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, Http));
        Target = target == "other" ? Access.Other : target == "readonly" ? Access.ReadOnly : Access.Repository;
        Client = await AfcClient.ConnectAsync(Mcp, Target);
    }
    public AfcCreateIntent Intent(string title = "fixture") => new(MemoryForgeProvider.RepositoryId, title,
        new("feature", MemoryForgeProvider.HeadObjectId), new("main", MemoryForgeProvider.BaseObjectId));
    public AfcCreateRequest Request(AfcCreateIntent? intent = null, string? key = null, AfcMutationMode mode = AfcMutationMode.Execute)
    {
        intent ??= Intent();
        return new(Guid.NewGuid().ToString("N"), key ?? Guid.NewGuid().ToString("N"), AfcContract.Digest(Target, intent), intent, mode);
    }
    public async Task<AfcChangeRequest> CreateAsync(string title = "fixture") => (await Client.CreateAsync(Request(Intent(title)))).ChangeRequest!;
    public async Task<CallToolResult> RawAsync(string operation, object arguments, AfcTargetRef? target = null)
    {
        var wire = JsonSerializer.SerializeToElement(new AfcInvocation<object>(target ?? Target, arguments), AfcContract.Json);
        return await Mcp.CallToolAsync("afc_" + operation, wire.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value));
    }
    public static async Task Error(string code, Func<Task> action) => Assert.AreEqual(code, (await Assert.ThrowsAsync<AfcException>(action)).Code);
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
