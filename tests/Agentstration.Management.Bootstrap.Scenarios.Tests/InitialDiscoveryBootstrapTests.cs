extern alias awpworker;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Secrets;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class AgentstrationDocumentationEndToEndTests
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("timeout")]
    [DataRow("cancel")]
    [Timeout(90_000)]
    public async Task InitialDiscoveryUsesTheRealWorkerWhileOrdinaryAssignmentsRemainPending(string outcome)
    {
        void Trace(string message) => Console.WriteLine("Initial discovery (" + outcome + "): " + message);
        using var catalog = new TemporaryCatalog();
        await catalog.WriteInitialProfileAsync();
        var profile = Path.Combine(catalog.Root, "initial-discovery");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(profile, "profile.yaml"), """
            apiVersion: agentstration.io/v1
            kind: BootstrapProfile
            metadata: { name: initial-discovery }
            definition: { targetScope: instance }
            """);
        await File.WriteAllTextAsync(Path.Combine(profile, "resources.yaml"), """
            apiVersion: agentstration.io/v1
            kind: ConnectedService
            metadata: { name: initial-http }
            definition:
              displayName: Offline HTTP discovery
              type: { resourceRef: { name: http-endpoint, version: 1.0.0 } }
              options: {}
              bindings:
              - inputId: baseUrl
                kind: literal
                literal: 'https://offline.invalid/feed?q=fixture'
            ---
            apiVersion: agentstration.io/v1
            kind: ConnectedServiceOperationDiscovery
            metadata: { name: initial-operations }
            definition:
              connectedService: { resourceRef: { name: initial-http } }
              operations:
              - name: get
                selector: { method: GET, endpoint: 'https://offline.invalid/feed' }
                enabled: true
            ---
            apiVersion: agentstration.io/v1
            kind: Vault
            metadata: { name: after-initial-discovery }
            definition: { displayName: After discovery, providerType: local }
            """);
        await using var factory = Factory(catalog.Root);
        Trace("starting baseline host");
        using var client = await Task.Run(() => factory.CreateClient()).WaitAsync(TimeSpan.FromSeconds(30));
        Trace("baseline host ready");
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var account = await services.GetRequiredService<UserManager<LocalIdentityUser>>().FindByNameAsync("bootstrap-admin");
        var principal = await services.GetRequiredService<IPrincipalResolver>().ResolveLocalAsync(account!.Id, default);
        var workspace = await services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
        using var context = services.GetRequiredService<IRequestContextScopeFactory>().Push(new(principal!.Id, workspace.TenantId, workspace.Id));
        var assignments = services.GetRequiredService<RuntimeWorkerAssignmentService>();
        _ = await assignments.CreateExactAsync(ResourceScopeRef.Workspace(workspace.Id), workspace.TenantId,
            RuntimeAssignmentTargetKind.FlowRun, "ordinary-pending-run", "microsoft-agent-framework", "1.0", "1.0", "ordinary", "sha256:ordinary", default);
        var configuration = services.GetRequiredService<IConfiguration>();
        configuration["Agentstration:Bootstrap:InitialProfiles:0"] = "initial-discovery";
        configuration["Agentstration:Bootstrap:DiscoveryTimeout"] = outcome == "timeout" ? "00:00:03" : "00:00:30";
        var startup = services.GetRequiredService<BootstrapStartupState>();
        startup.Begin();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var applying = services.GetRequiredService<BootstrapStartupCoordinator>().ExecuteAsync(
            services.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverForActiveWorkspacesAsync, cancellation.Token);
        Trace("initial application started");
        while (startup.Read().Activity?.WaitReason != "waiting_worker" && !applying.IsCompleted)
            await Task.Delay(25, cancellation.Token);
        if (applying.IsCompleted) await applying;
        var waiting = startup.Read();
        Trace("observing Worker wait");
        var activity = waiting.Activity;
        Assert.IsNotNull(activity, applying.IsFaulted ? applying.Exception!.ToString() : null);
        Assert.AreEqual("waiting_worker", activity.WaitReason);
        Assert.IsFalse(startup.IsReady);
        Assert.IsTrue(startup.WorkerCommunicationOpen);
        Assert.IsTrue(waiting.Steps.Where(step => step.Id is "topology" or "workers" or "builtins" or "extensions").All(step => step.State == "succeeded"));
        Assert.AreEqual("microsoft-agent-framework", activity.RuntimeCapability);
        Assert.IsNotNull(activity.Deadline);
        Assert.IsNotNull(activity.FlowRunId);
        using var denied = await client.GetAsync("/api/work-items", cancellation.Token);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        using var unknownProtocol = await client.PostAsJsonAsync("/api/awp/v1/unknown", new { }, cancellation.Token);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unknownProtocol.StatusCode, "A protocol-looking URL is not a registered startup endpoint.");
        using var unsignedWorker = await client.PostAsJsonAsync("/api/awp/v1/workers/register", new { }, cancellation.Token);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unsignedWorker.StatusCode, "Admitting the endpoint must not bypass Worker authentication.");
        using var capabilityDenied = await client.PostAsJsonAsync(AepProtocol.SecretAccessPath,
            new { version = AepProtocol.SecretAccessVersion, secretCapability = "invalid", extensionId = "fixture", requirementId = "password", executionId = "fixture" }, cancellation.Token);
        Assert.AreEqual(HttpStatusCode.Forbidden, capabilityDenied.StatusCode, "Startup admits only the callback transport; capability validation remains authoritative.");
        if (outcome == "success")
        {
            using var worker = await AwpTestWorker.CreateAsync(factory, services);
            Trace("Worker registered");
            await worker.ExecuteAsync(activity.FlowRunId).WaitAsync(TimeSpan.FromSeconds(35));
            Trace("Worker assignment completed");
            await applying.WaitAsync(TimeSpan.FromSeconds(10));
            Trace("profile resources applied");
            Assert.IsFalse(startup.IsReady, "Only final host completion releases ordinary work.");
            Assert.AreEqual(RuntimeAssignmentState.Pending, (await assignments.GetByTargetExactAsync(ResourceScopeRef.Workspace(workspace.Id), RuntimeAssignmentTargetKind.FlowRun, "ordinary-pending-run", default))!.Value.State);
            using (services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
                Assert.IsNotNull(await services.GetRequiredService<IResourceStore>().GetExactAsync<VaultResource>(new(ResourceScopeRef.Instance, ResourceNamespace.Default, "Vault", "after-initial-discovery"), default));
            startup.Complete();
            startup.Begin();
            Trace("replay started");
            await services.GetRequiredService<BootstrapStartupCoordinator>().ExecuteAsync(
                services.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverForActiveWorkspacesAsync, default).WaitAsync(TimeSpan.FromSeconds(10));
            Trace("replay completed");
            Assert.IsFalse(startup.Read().Families.Any(family => family.State is "failed" or "waiting"));
            startup.Complete();
        }
        else
        {
            if (outcome == "cancel") cancellation.Cancel();
            var failure = await Assert.ThrowsAsync<Exception>(async () => await applying);
            if (outcome == "cancel") Assert.IsInstanceOfType<OperationCanceledException>(failure);
            startup.Fail(failure);
            if (outcome == "timeout") Assert.AreEqual("bootstrap_discovery_timeout", startup.Read().Failure?.Code);
            Assert.IsFalse(startup.IsReady);
            Assert.HasCount(0, startup.ClaimableAssignmentIds!);
            using (services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
                Assert.IsNull(await services.GetRequiredService<IResourceStore>().GetExactAsync<VaultResource>(new(ResourceScopeRef.Instance, ResourceNamespace.Default, "Vault", "after-initial-discovery"), default));
            configuration["Agentstration:Bootstrap:DiscoveryTimeout"] = "00:00:30";
            startup.Begin();
            using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var retry = services.GetRequiredService<BootstrapStartupCoordinator>().ExecuteAsync(
                services.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverForActiveWorkspacesAsync, retryDeadline.Token);
            while (startup.Read().Activity?.WaitReason != "waiting_worker" && !retry.IsCompleted)
                await Task.Delay(25, retryDeadline.Token);
            if (retry.IsCompleted) await retry;
            var retriedActivity = startup.Read().Activity;
            Assert.IsNotNull(retriedActivity);
            Assert.IsNotNull(retriedActivity.FlowRunId);
            Assert.AreNotEqual(activity.FlowRunId, retriedActivity.FlowRunId, "A terminal failed discovery needs a new attempt; unfinished work retains its identity.");
            using var retryWorker = await AwpTestWorker.CreateAsync(factory, services);
            await retryWorker.ExecuteAsync(retriedActivity.FlowRunId).WaitAsync(TimeSpan.FromSeconds(35));
            await retry.WaitAsync(TimeSpan.FromSeconds(10));
            using (services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
                Assert.IsNotNull(await services.GetRequiredService<IResourceStore>().GetExactAsync<VaultResource>(new(ResourceScopeRef.Instance, ResourceNamespace.Default, "Vault", "after-initial-discovery"), default));
            startup.Complete();
        }
    }
}
