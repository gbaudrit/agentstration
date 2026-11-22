using System.Net.Http.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agentstration.Management.Tests;

internal static class BootstrapApplicationTestFixture
{
    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The Agentstration repository root could not be located.");
    }

    internal static WebApplicationFactory<Program> LoadTestFactory(LoadTestGate? gate = null) =>
        new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:Authentication:Mode", "Development");
            builder.UseSetting("Agentstration:Authentication:DevelopmentGrantPlatformAdministrator", "true");
            builder.UseSetting("Agentstration:Bootstrap:Path", Path.Combine(FindRepositoryRoot(), "deploy", "bootstrap", "profiles"));
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false");
            builder.ConfigureServices(services =>
            {
                if (gate is not null)
                {
                    foreach (var descriptor in services.Where(value => value.ServiceType == typeof(IBootstrapResourceHandler)
                                 && value.ImplementationType == typeof(ParameterBootstrapResourceHandler)).ToArray()) services.Remove(descriptor);
                    services.AddScoped<IBootstrapResourceHandler>(provider => new GatedParameterHandler(
                        ActivatorUtilities.CreateInstance<ParameterBootstrapResourceHandler>(provider), gate));
                }
            });
        });

    internal static async Task<BootstrapApplicationResource> WaitForApplicationAsync(HttpClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var processed = 0;
        while (true)
        {
            var application = (await client.GetFromJsonAsync<BootstrapApplicationResource>($"/api/bootstrap/applications/{id}", timeout.Token))!;
            Assert.IsTrue(application.Definition.Resources.Count >= processed, "Persisted progress must never go backwards.");
            processed = application.Definition.Resources.Count;
            if (application.Definition.Status != BootstrapApplicationStatus.Running) return application;
            await Task.Delay(50, timeout.Token);
        }
    }

    internal sealed class LoadTestGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResourceName { get; init; } = "bootstrap-load-test-002";
        public bool Fail { get; init; }
        public bool FailPlanning { get; init; }
    }

    internal sealed class GatedParameterHandler(ParameterBootstrapResourceHandler inner, LoadTestGate gate) : IBootstrapResourceHandler
    {
        public string Kind => inner.Kind;
        public BootstrapProfileScope Scope => inner.Scope;
        public Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning, CancellationToken cancellationToken)
        {
            if (gate.FailPlanning) throw new InvalidOperationException("synthetic-sensitive-content");
            return inner.PlanAsync(resource, operation, planning, cancellationToken);
        }
        public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            if (resource.Metadata.Name == gate.ResourceName)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
                if (gate.Fail) throw new InvalidOperationException("Synthetic load-test failure.");
            }
            return await inner.ApplyAsync(resource, operation, cancellationToken);
        }
    }
}
