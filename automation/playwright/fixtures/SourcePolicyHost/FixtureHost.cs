using System.Net;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Storage;
using Agentstration.Knowledge;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Playwright.SourcePolicy;

internal static class FixtureHost
{
    public static async Task Main()
    {
        await using var factory = new PolicyFactory();
        var address = new Uri(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")!);
        factory.UseKestrel(address.Port);
        factory.StartServer();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopped.TrySetResult());
        await stopped.Task;
    }
}

internal sealed class PolicyFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(Directory.GetCurrentDirectory(), "src", "Agentstration.Web"));
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<PolicyClock>();
            services.AddSingleton<ControlledContent>();
            services.AddSingleton<IStartupFilter, PolicyControl>();
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<LocalStorageObjectProvider>(p, p.GetRequiredService<PolicyClock>()));
            services.AddSingleton<IStorageContentGateway>(p => p.GetRequiredService<ControlledContent>());
            services.AddSingleton<IStoragePublicationGateway>(p => ActivatorUtilities.CreateInstance<LocalStoragePublicationGateway>(p, p.GetRequiredService<PolicyClock>()));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<DataSourceDatasetAcquisitionService>(p, p.GetRequiredService<PolicyClock>()));
            services.AddSingleton<IKnowledgeProjectionInputResolver>(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionInputResolver>(p, p.GetRequiredService<PolicyClock>()));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionService>(p, p.GetRequiredService<PolicyClock>()));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeAccessService>(p, p.GetRequiredService<PolicyClock>()));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<SourceHistoryMaintenance>(p, p.GetRequiredService<PolicyClock>()));
        });
    }
}

// Only lifecycle and local Storage clocks move. Runtime registration and worker leases use System time.
internal sealed class PolicyClock : TimeProvider
{
    private long offset;
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref offset));
    public void Advance(int seconds) => Interlocked.Add(ref offset, TimeSpan.FromSeconds(seconds).Ticks);
}

internal sealed class PolicyControl(PolicyClock clock, ControlledContent content) : IStartupFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string key = Environment.GetEnvironmentVariable("SOURCE_POLICY_FIXTURE_KEY")
        ?? throw new InvalidOperationException("A per-campaign fixture control key is required.");
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (HttpContext context, RequestDelegate downstream) =>
        {
            if (context.Request.Path != "/__fixture/source-policy") { await downstream(context); return; }
            if (context.Request.Method != "POST" || context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
                || context.Request.Headers["X-Fixture-Control"] != key) { context.Response.StatusCode = 403; return; }
            var command = await JsonSerializer.DeserializeAsync<Control>(context.Request.Body, Json, context.RequestAborted);
            if (command is null || command.AdvanceSeconds is < 0 or > 31 * 86400) { context.Response.StatusCode = 400; return; }
            clock.Advance(command.AdvanceSeconds);
            content.FailWrites = command.FailWrites;
            if (command.Reconcile)
            {
                using var system = context.RequestServices.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
                await context.RequestServices.GetRequiredService<SourceHistoryMaintenance>().ReconcileAsync(context.RequestAborted);
            }
            await context.Response.WriteAsJsonAsync(new { now = clock.GetUtcNow(), content.FailWrites }, context.RequestAborted);
        });
        next(app);
    };
    private sealed record Control(int AdvanceSeconds = 0, bool FailWrites = false, bool Reconcile = false);
}

internal sealed class ControlledContent : IStorageContentGateway
{
    private readonly IStorageContentGateway inner;
    private int failWrites;
    public ControlledContent(IServiceProvider services, PolicyClock clock) => inner = ActivatorUtilities.CreateInstance<StorageContentGateway>(services, clock);
    public bool FailWrites { get => Volatile.Read(ref failWrites) != 0; set => Volatile.Write(ref failWrites, value ? 1 : 0); }
    public Task ValidateAsync(StorageContentRequest target, CancellationToken cancellationToken = default) => inner.ValidateAsync(target, cancellationToken);
    public Task<StorageObjectPage> ListAsync(StorageContentRequest target, string prefix, int limit, string? cursor = null, CancellationToken cancellationToken = default) => inner.ListAsync(target, prefix, limit, cursor, cancellationToken);
    public Task<StorageObject> StatAsync(StorageContentRequest target, string reference, CancellationToken cancellationToken = default) => inner.StatAsync(target, reference, cancellationToken);
    public Task<StorageObjectContent> ReadAsync(StorageContentRequest target, string reference, long offset, int maximumBytes, CancellationToken cancellationToken = default) => inner.ReadAsync(target, reference, offset, maximumBytes, cancellationToken);
    public Task<bool> RetireAsync(StorageContentRequest target, StorageObject exact, CancellationToken cancellationToken = default) => inner.RetireAsync(target, exact, cancellationToken);
    public Task<StorageObject> WriteAsync(StorageContentRequest target, StorageWrite request, CancellationToken cancellationToken = default) => FailWrites
        ? throw new StorageException("fixture_refresh_failed", "Synthetic offline acquisition refresh failure.") : inner.WriteAsync(target, request, cancellationToken);
}
