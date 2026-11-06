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
    public async Task FirstProjectionCreatesEditableCoreToolsCategoryOnlyOnce()
    {
        await using var factory = await ManagementServiceFixture.CreateAsync();
        var context = factory.Context;
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var projection = factory.Services.GetRequiredService<InternalMcpToolProjectionService>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);

        await projection.EnsureAsync(scope, ResourceNamespace.Default, default);

        var toolName = AgentstrationToolProvider.ToolResourceName(AgentstrationInternalTools.DateTimeGet);
        var toolAddress = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ToolResourceKinds.Tool, toolName);
        var categoryAddress = ScopedResourceAddress.Create(scope, ResourceNamespace.Default,
            ToolResourceKinds.ToolCategory, "base-tools");
        var tool = await store.GetExactAsync<ToolResource>(toolAddress, default);
        var category = await store.GetExactAsync<ToolCategoryResource>(categoryAddress, default);
        Assert.IsNotNull(tool);
        Assert.IsNotNull(category);
        Assert.AreEqual("Outils de base", category.Value.Definition.DisplayName);
        Assert.AreEqual(toolName, category.Value.Definition.Tools.Single().Name);

        await store.DeleteExactAsync(categoryAddress, category.ETag, default);
        await projection.EnsureAsync(scope, ResourceNamespace.Default, default);

        Assert.IsNull(await store.GetExactAsync<ToolCategoryResource>(categoryAddress, default));
        Assert.IsNotNull(await store.GetExactAsync<ToolResource>(toolAddress, default));
    }

}
