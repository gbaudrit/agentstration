using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class EntrySummaryExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SavedEntrySummaryPinsExecutionWorkspaceNamespaceAndName()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var context = await client.GetFromJsonAsync<JsonElement>("/api/identity/context");
        var workspace = context.GetProperty("context").GetProperty("workspaceId").GetGuid();
        var owner = ResourceScopeRef.Workspace(workspace);
        var repository = factory.Services.GetRequiredService<IWorkplaceRepository>();
        foreach (var ns in new[] { ResourceNamespace.Default, ResourceNamespace.Parse("preview") })
            await repository.UpsertEntryDraftAsync(new EntryDraft
            {
                WorkspaceId = new(workspace),
                Id = new("preview-entry", ns),
                Name = "preview-entry",
                DisplayName = "Saved " + ns.Value,
                Presentation = new() { Fields = [new EntryFieldDefinition { Name = "request", Label = "Request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }] },
                Binding = new(EntryBindingKind.Agent, "saved-target")
            }, default);
        string Path(ResourceScopeRef scope, string ns) => $"/api/management/entries/preview-entry/summary?scopeRef={Uri.EscapeDataString(scope.Value)}&namespace={ns}";
        using var response = await client.GetAsync(Path(owner, "preview"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var summary = await response.Content.ReadFromJsonAsync<EntrySavedSummaryResponse>();
        Assert.IsNotNull(summary);
        Assert.AreEqual(workspace, summary.Value.WorkspaceId.Value);
        Assert.AreEqual(new EntryId("preview-entry", ResourceNamespace.Parse("preview")), summary.Value.Id);
        Assert.AreEqual("Saved preview", summary.Value.DisplayName);
        Assert.IsNull(summary.Published);
        using var otherOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "preview"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherOwner.StatusCode);
        using var missingNamespace = await client.GetAsync(Path(owner, "missing"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingNamespace.StatusCode);
    }
}
