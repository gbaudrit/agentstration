using System.Net.Http.Headers;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.Asp.Server;
using Asp.Samples.LocalStorage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agentstration.Asp.Tests;

internal sealed class AspTestHost : IAsyncDisposable
{
    public TestClock Clock { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Http { get; private set; } = null!;
    public McpClient Mcp { get; private set; } = null!;
    public AspClient Client { get; private set; } = null!;
    public SampleAccess Access => Factory.Services.GetRequiredService<SampleAccess>();
    public AspTestHost(IAspObjectReader? provider = null, AspSessionLimits? limits = null)
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
            if (provider is not null) { services.RemoveAll<IAspObjectReader>(); services.AddSingleton(provider); }
            services.RemoveAll<AspMcpServer>();
            services.AddSingleton(sp => new AspMcpServer(sp.GetRequiredService<SampleAccess>().Bindings(sp.GetRequiredService<IAspObjectReader>()), Clock,
                sessionLimits: limits));
        }));
    }
    public async Task StartAsync()
    {
        Http = Factory.CreateClient(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Access.Token);
        Mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new("http://localhost/mcp") }, Http));
        Client = await AspClient.ConnectAsync(Mcp, "asp", SampleAccess.FirstStore);
    }
    public AspWriteBlobRequest Write(string key, byte[] content, bool final = true, string? id = null) => new(id ?? Guid.NewGuid().ToString("N"), 0, final,
        Convert.ToBase64String(content), Options: new(key), NotAfter: Clock.GetUtcNow().AddMinutes(2));
    public AspReadBlobRequest Read(string reference, AspByteRange? range = null, int? preference = null) => new(Guid.NewGuid().ToString("N"), Selection: new(reference, range, preference), NotAfter: Clock.GetUtcNow().AddMinutes(2));
    public async Task<CallToolResult> RawAsync(string operation, object arguments, AspStoreRef? store = null)
    {
        var wire = JsonSerializer.SerializeToElement(new AspInvocation<object>(store ?? SampleAccess.FirstStore, arguments), AspProtocol.JsonOptions);
        return await Mcp.CallToolAsync("asp_" + operation, wire.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value));
    }
    public async ValueTask DisposeAsync()
    {
        if (Mcp is not null) await Mcp.DisposeAsync(); Http?.Dispose(); await Factory.DisposeAsync();
    }
    public static async Task Error(string code, Func<Task> operation) => Assert.AreEqual(code, (await Assert.ThrowsAsync<AspException>(operation)).Code);
    internal sealed class TestClock : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public DateTimeOffset Now
        {
            get => now;
            set { now = value; foreach (var timer in timers.ToArray()) timer.Tick(now); }
        }
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period); timers.Add(timer); return timer;
        }
        private sealed class ManualTimer(TestClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private DateTimeOffset next = due == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.Now + due;
            private TimeSpan interval = period;
            public void Tick(DateTimeOffset current)
            {
                if (next > current) return;
                next = interval == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : current + interval;
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) { interval = period; next = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.Now + dueTime; return true; }
            public void Dispose() => next = DateTimeOffset.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
