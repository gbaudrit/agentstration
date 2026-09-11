using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolDefinitionConsoleTests
{
    [TestMethod]
    public void DefinitionsPageListsFlowBackedMcpTools()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(new DefinitionClient());

        var rendered = context.Render<ToolDefinitions>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Send notification");
            StringAssert.Contains(rendered.Markup, "notification-flow");
            StringAssert.Contains(rendered.Markup, "/tools/definitions/notification.send?namespace=default");
        });
    }

    private sealed class DefinitionClient : IToolDefinitionsClient
    {
        public Task<IReadOnlyList<ToolDefinitionResource>> GetAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolDefinitionResource>>([
            new ToolDefinitionResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ToolDefinition,
                Metadata = new ResourceMetadata { Name = "notification.send" },
                ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                Definition = new ToolDefinitionProperties
                {
                    DisplayName = "Send notification",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                    Flow = new ToolDefinitionFlowTarget { Name = "notification-flow" }
                }
            }
        ]);

        public Task<ResourceSnapshot<ToolDefinitionResource>> GetAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> CreateAsync(Agentstration.Management.Contracts.CreateToolDefinitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> UpdateAsync(string name, ResourceNamespace @namespace, Agentstration.Management.Contracts.PutToolDefinitionRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> SetEnabledAsync(string name, ResourceNamespace @namespace, bool enabled, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string name, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
