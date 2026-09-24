using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Notifications;
using Agentstration.Infrastructure.Tools;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DateTimeToolTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task NativeDateTimeToolReturnsExplicitUtcLocalTimezoneAndOffset()
    {
        var expected = new DateTimeOffset(2026, 9, 24, 10, 11, 12, TimeSpan.Zero);
        var tool = new DateTimeMcpTool(new FixedTimeProvider(expected));
        var result = await tool.ExecuteAsync(new(
            Guid.NewGuid(),
            new(Guid.NewGuid()),
            Guid.NewGuid(),
            "date-time-call",
            null,
            JsonSerializer.SerializeToElement(new { }),
            ToolDefinitionCallerKind.Agent),
            default);

        Assert.IsNotNull(result);
        Assert.AreEqual(expected, result.Value.GetProperty("utc").GetDateTimeOffset());
        Assert.AreEqual(TimeZoneInfo.Local.Id, result.Value.GetProperty("timeZone").GetString());
        var local = result.Value.GetProperty("local").GetDateTimeOffset();
        Assert.AreEqual(TimeZoneInfo.ConvertTime(expected, TimeZoneInfo.Local), local);
        Assert.AreEqual(local.Offset.ToString("c"), result.Value.GetProperty("utcOffset").GetString());
    }

    [TestMethod]
    public async Task FirstProjectionCreatesEditableCoreToolsCategoryOnlyOnce()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var projection = factory.Services.GetRequiredService<InternalMcpToolProjectionService>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);

        await projection.EnsureAsync(scope, ResourceNamespace.Default, default);

        var toolName = AgentstrationToolProvider.ToolResourceName(AgentstrationInternalTools.DateTimeGet);
        var tool = await store.GetAsync<ToolResource>(new(ToolResourceKinds.Tool, toolName), default);
        var category = await store.GetAsync<ToolCategoryResource>(new(ToolResourceKinds.ToolCategory, "base-tools"), default);
        Assert.IsNotNull(tool);
        Assert.IsNotNull(category);
        Assert.AreEqual("Outils de base", category.Value.Definition.DisplayName);
        Assert.AreEqual(toolName, category.Value.Definition.Tools.Single().Name);

        await store.DeleteAsync(new(ToolResourceKinds.ToolCategory, "base-tools"), category.ETag, default);
        await projection.EnsureAsync(scope, ResourceNamespace.Default, default);

        Assert.IsNull(await store.GetAsync<ToolCategoryResource>(new(ToolResourceKinds.ToolCategory, "base-tools"), default));
        Assert.IsNotNull(await store.GetAsync<ToolResource>(new(ToolResourceKinds.Tool, toolName), default));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
