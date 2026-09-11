using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StorageConcurrencySmokeTests
{
    [TestMethod]
    public async Task SqliteAcceptsBoundedConcurrentWorkItemWritesWithoutErrors()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"agentstration-storage-smoke-{Guid.NewGuid():N}");
        try
        {
            await using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Agentstration:Testing:ApiOnly", "true");
                builder.UseSetting("Agentstration:Storage:Provider", "Sqlite");
                builder.UseSetting("Data:Directory", dataDirectory);
                builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false");
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            _ = host.CreateClient();
            var repository = host.Services.GetRequiredService<IWorkItemRepository>();
            var workspaceId = new WorkspaceId(Guid.NewGuid());

            var stored = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            {
                var id = WorkItemId.New();
                var now = DateTimeOffset.UtcNow;
                var item = WorkItem.Create(id, workspaceId, "content", $"Smoke {index}", now);
                var created = await repository.CreateAsync(item, default);
                var expectedVersion = created.Value.Version;
                created.Value.AddMessage("update", "smoke", Guid.NewGuid(), now.AddMilliseconds(1));
                await repository.SaveAsync(created.Value, expectedVersion, default);
                return await repository.GetAsync(workspaceId, id, default);
            }));

            Assert.HasCount(8, stored);
            Assert.IsTrue(stored.All(item => item is not null && item.Value.Messages.Count == 1));
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        }
    }
}
