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
    private readonly AwpWorker.AwpClientCredential credential;
    private readonly AwpWorker.AwpClient client;
    private readonly AwpWorker.AwpAssignmentRunner runner;
    private readonly AwpWorker.RuntimeWorkerOptions options = new() { LeaseSafetyMarginSeconds = 1 };
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task processing;

    private AwpTestWorker(
        AwpWorker.AwpClientCredential credential,
        AwpWorker.AwpClient client,
        bool processAssignments)
    {
        this.credential = credential;
        this.client = client;
        runner = new(
            new AwpWorker.AwpAssignmentExecutor(
                NullLoggerFactory.Instance,
                NullLogger<AwpWorker.AwpAssignmentExecutor>.Instance),
            TimeProvider.System,
            NullLogger<AwpWorker.AwpRuntimeWorkerService>.Instance);
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
        bool processAssignments = true)
    {
        _ = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>()
            .EnsureInitializedAsync(default);
        var workerId = Guid.NewGuid();
        var identities = factory.Services.GetRequiredService<AwpWorkerIdentityService>();
        _ = await identities.AnnounceAsync(new(workerId, "API test Worker",
            AwpWorkerAuthentication.ProtocolVersion), default);
        var pairing = await identities.IssuePairingCodeAsync(workerId, default);
        var issued = await identities.ClaimAsync(new(workerId, pairing.Code), "api-test", default);
        var credential = new AwpWorker.AwpClientCredential(
            issued.WorkerId,
            issued.CredentialId,
            issued.InstanceId,
            Encoding.UTF8.GetBytes(issued.Secret));
        var client = new AwpWorker.AwpClient(
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
                [new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", ["1.0"])])), default);
        return new AwpTestWorker(credential, client, processAssignments);
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
    }

    public async ValueTask DisposeAsync()
    {
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
