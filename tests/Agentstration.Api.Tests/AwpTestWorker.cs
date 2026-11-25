extern alias awpworker;

using System.Text;
using Agentstration.Awp.Abstractions;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Secrets.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using AwpWorker = awpworker::Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpTestWorker : IAsyncDisposable
{
    private readonly Agentstration.Runtime.Worker.Core.AwpClientCredential credential;
    private readonly Agentstration.Runtime.Worker.Core.AwpClient client;
    private readonly Agentstration.Runtime.Worker.Core.AwpAssignmentRunner runner;
    private readonly Agentstration.Runtime.Worker.Core.RuntimeWorkerOptions options = new() { LeaseSafetyMarginSeconds = 1 };
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task processing;
    private bool disposed;
    internal Guid WorkerId => client.WorkerId.Value;
    internal Task<AwpClaimResponse> ClaimOnceAsync(CancellationToken token = default) =>
        client.ClaimAsync(new(client.WorkerId, client.SessionId, 1, 1), token);

    private AwpTestWorker(
        Agentstration.Runtime.Worker.Core.AwpClientCredential credential,
        Agentstration.Runtime.Worker.Core.AwpClient client,
        bool processAssignments,
        Agentstration.Runtime.Worker.Core.IAwpAssignmentExecutor? executor = null)
    {
        this.credential = credential;
        this.client = client;
        runner = new(
            executor ?? new AwpWorker.AwpAssignmentExecutor(
                NullLoggerFactory.Instance,
                NullLogger<AwpWorker.AwpAssignmentExecutor>.Instance),
            TimeProvider.System,
            NullLogger<Agentstration.Runtime.Worker.Core.AwpRuntimeWorkerService>.Instance);
        processing = processAssignments
            ? Task.Run(ProcessAsync, CancellationToken.None)
            : Task.CompletedTask;
    }

    public static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Agentstration:AwpWorkerTrust:Enabled", "true");
        builder.UseSetting("Agentstration:AwpWorkerTrust:InstanceId", "api-test");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMasterKeyProvider>();
            services.AddSingleton<IMasterKeyProvider, TestMasterKeyProvider>();
        });
    }

    public static async Task<AwpTestWorker> StartAsync(
        WebApplicationFactory<Program> factory,
        bool processAssignments = true,
        Agentstration.Runtime.Worker.Core.IAwpAssignmentExecutor? executor = null,
        IReadOnlyList<AwpRuntimeCapability>? capabilities = null)
    {
        _ = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>()
            .EnsureInitializedAsync(default);
        var workerId = Guid.NewGuid();
        var identities = factory.Services.GetRequiredService<AwpWorkerIdentityService>();
        _ = await identities.AnnounceAsync(new(workerId, "API test Worker",
            AwpWorkerAuthentication.ProtocolVersion), default);
        var pairing = await identities.IssuePairingCodeAsync(workerId, default);
        var issued = await identities.ClaimAsync(new(workerId, pairing.Code), "api-test", default);
        var credential = new Agentstration.Runtime.Worker.Core.AwpClientCredential(
            issued.WorkerId,
            issued.CredentialId,
            issued.InstanceId,
            Encoding.UTF8.GetBytes(issued.Secret));
        var client = new Agentstration.Runtime.Worker.Core.AwpClient(
            new Uri("https://localhost"),
            credential,
            new(Guid.NewGuid()),
            TimeProvider.System,
            0,
            factory.Server.CreateHandler());
        await client.ActivateSessionAsync(default);
        _ = await client.RegisterAsync(new(
            [AwpProtocol.Version],
            new(client.WorkerId, client.SessionId, "api-test", 1,
                capabilities ?? [new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", ["1.0", "1.1"])])), default);
        return new AwpTestWorker(credential, client, processAssignments, executor);
    }

    public Task<AwpClaimResponse> ClaimAsync(int maximumWaitSeconds = 5, CancellationToken cancellationToken = default) =>
        client.ClaimAsync(new(client.WorkerId, client.SessionId, 1, maximumWaitSeconds), cancellationToken);

    public Task RunAsync(
        AwpRunAssignment assignment,
        DateTimeOffset serverTime,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(client, assignment, serverTime, options, cancellationToken);

    private async Task ProcessAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var claim = await client.ClaimAsync(
                    new(client.WorkerId, client.SessionId, 1, 1), shutdown.Token);
                if (claim.Assignment is null)
                {
                    await Task.Delay(10, shutdown.Token);
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await runner.RunAsync(client, claim.Assignment, claim.ServerTime, options, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (HttpRequestException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (IOException) when (shutdown.IsCancellationRequested)
        {
            // TestServer can surface an aborted long-poll response as I/O during shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await shutdown.CancelAsync();
        try { await processing; }
        catch (OperationCanceledException) { }
        client.Dispose();
        credential.Dispose();
        shutdown.Dispose();
    }

    private sealed class TestMasterKeyProvider : IMasterKeyProvider, IMasterKeyInitializer
    {
        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        public ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Key.ToArray());

        public Task<SecretVaultInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SecretVaultInitializationResult(
                true,
                Path.Combine(Path.GetTempPath(), "agentstration-api-tests", "master.key")));
        }
    }
}
