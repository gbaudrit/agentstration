using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Web.Hosting;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SqliteDataDirectoryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionStorageUsesDataDirectoryAndHonorsExplicitPaths(bool overridePaths)
    {
        var directory = NewDirectory();
        try
        {
            await using var provider = CreateProvider(directory, overridePaths);
            var expected = overridePaths ? Path.Combine(directory, "overrides") : Path.Combine(directory, "data");
            await AssertPathAsync<ResourceManagementDbContext>("control-plane.db");
            await AssertPathAsync<WorkDbContext>("work-plane.db");
            await AssertPathAsync<FlowDbContext>("flow-plane.db");
            await AssertPathAsync<RuntimeRunDbContext>("runtime-plane.db");

            async Task AssertPathAsync<TContext>(string fileName) where TContext : DbContext
            {
                await using var context = await provider.GetRequiredService<IDbContextFactory<TContext>>().CreateDbContextAsync();
                Assert.AreEqual(Path.Combine(expected, fileName), context.Database.GetDbConnection().DataSource);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ManagementWorkAndFlowSurviveHostReplacementWithSharedDataDirectory()
    {
        var directory = NewDirectory();
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var owner = Guid.NewGuid();
        var workId = WorkItemId.New();
        var flowId = new FlowId("persistent-flow");
        var parameterAddress = ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default,
            ParameterResourceKinds.Parameter, "persistent-parameter");
        try
        {
            await using (var first = CreateProvider(directory, overridePaths: false))
            {
                var resources = first.GetRequiredService<IResourceStore>();
                var work = first.GetRequiredService<IWorkItemRepository>();
                var flows = first.GetRequiredService<IFlowRepository>();
                await resources.InitializeAsync(default);
                await work.InitializeAsync(default);
                await flows.InitializeAsync(default);
                using var system = first.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
                await resources.PutExactAsync(ResourceScopeRef.Instance, new ParameterResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ParameterResourceKinds.Parameter,
                    Metadata = new() { Name = parameterAddress.Name },
                    Definition = new()
                    {
                        DisplayName = "Persistent parameter",
                        ValueType = ParameterValueType.Text,
                        Value = JsonSerializer.SerializeToElement("retained")
                    }
                }, null, true, default);
                var now = DateTimeOffset.UtcNow;
                await work.CreateAsync(WorkItem.Create(workId, workspaceId, owner, "test", "Persistent work", now), default);
                await flows.CreateAsync(new FlowResource(ResourceScopeRef.Workspace(workspaceId.Value), flowId,
                    "Persistent flow", null, "1.0.0", true, null,
                    new DirectFlowDefinition(new(FlowTargetKind.Agent, "test-agent")), new Dictionary<string, string>(), now, now), default);
            }

            // Replace the application's content directory while retaining only its data directory.
            Directory.Delete(Path.Combine(directory, "app"), recursive: true);
            await using var replacement = CreateProvider(directory, overridePaths: false);
            using var replacementSystem = replacement.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var parameter = await replacement.GetRequiredService<IResourceStore>().GetExactAsync<ParameterResource>(parameterAddress, default);
            Assert.IsNotNull(parameter);
            Assert.AreEqual("retained", parameter.Value.Definition.Value.GetString());
            var restoredWork = await replacement.GetRequiredService<IWorkItemRepository>().GetAsync(workspaceId, owner, workId, default);
            Assert.IsNotNull(restoredWork);
            Assert.AreEqual("Persistent work", restoredWork.Value.Instruction);
            var restoredFlow = await replacement.GetRequiredService<IFlowRepository>().GetExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), flowId, default);
            Assert.IsNotNull(restoredFlow);
            Assert.AreEqual("Persistent flow", restoredFlow.Value.Name);
        }
        finally
        {
            foreach (var fileName in new[] { "control-plane.db", "work-plane.db", "flow-plane.db" })
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "data", fileName)}");
                SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider CreateProvider(string directory, bool overridePaths)
    {
        var contentRoot = Path.Combine(directory, "app");
        Directory.CreateDirectory(contentRoot);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(contentRoot, "appsettings.json"), overwrite: true);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(global::Program).Assembly.GetName().Name,
            ContentRootPath = contentRoot
        });
        var settings = new Dictionary<string, string?>
        {
            ["Data:Directory"] = Path.Combine(directory, "data"),
            ["AI:Provider"] = "Deterministic",
            ["Agentstration:Storage:Provider"] = "Sqlite"
        };
        if (overridePaths)
        {
            settings["Data:ControlPlanePath"] = Path.Combine(directory, "overrides", "control-plane.db");
            settings["Data:WorkPlanePath"] = Path.Combine(directory, "overrides", "work-plane.db");
            settings["Data:FlowPath"] = Path.Combine(directory, "overrides", "flow-plane.db");
            settings["Data:RuntimePath"] = Path.Combine(directory, "overrides", "runtime-plane.db");
        }
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddAgentstrationStandaloneHost();
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"agentstration-sqlite-directory-{Guid.NewGuid():N}");
}
