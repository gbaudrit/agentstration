using System.Globalization;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ToolDetailsComponentTests
{
    [TestMethod]
    public void ShowsCapabilityInputFieldsOutputFieldsAndExactSourceSchemas()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"deliveryKey":{"type":"string","description":"Stable key supplied by provider.","maxLength":256},"actionUrl":{"type":"string","pattern":"^/"}},"required":["deliveryKey"]}
                """),
            Output = JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"notificationId":{"type":"string","description":"Created notification identifier."}},"required":["notificationId"]}
                """)
        });

        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        Assert.AreEqual("What this tool does", rendered.Find("#tool-capability-heading").TextContent);
        StringAssert.Contains(rendered.Find(".tool-capability").TextContent, "Create one durable notification.");
        Assert.AreEqual("Inputs", rendered.Find("#tool-input-heading").TextContent);
        Assert.AreEqual("Outputs", rendered.Find("#tool-output-heading").TextContent);
        var inputFields = rendered.FindAll(".tool-contract").First().QuerySelectorAll(".tool-field");
        Assert.AreEqual(2, inputFields.Length);
        StringAssert.Contains(inputFields[0].TextContent, "deliveryKey");
        StringAssert.Contains(inputFields[0].TextContent, "Required");
        StringAssert.Contains(inputFields[0].TextContent, "Stable key supplied by provider.");
        StringAssert.Contains(inputFields[0].TextContent, "Maximum length: 256");
        StringAssert.Contains(inputFields[1].TextContent, "Optional");
        StringAssert.Contains(inputFields[1].TextContent, "Pattern: ^/");
        StringAssert.Contains(rendered.FindAll(".tool-contract")[1].TextContent, "notificationId");
        Assert.AreEqual(2, rendered.FindAll(".tool-raw-schema pre").Count);
        StringAssert.Contains(rendered.FindAll(".tool-raw-schema pre")[0].TextContent, "\"required\"");
        StringAssert.Contains(rendered.FindAll(".tool-raw-schema pre")[1].TextContent, "\"notificationId\"");
    }

    [TestMethod]
    public void MissingAndNonObjectSchemasHaveHonestFallbacks()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"oneOf":[{"type":"string"},{"type":"number"}]}"""),
            Output = null
        }, description: null);

        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        StringAssert.Contains(rendered.Find(".tool-capability").TextContent, "No capability description was provided");
        StringAssert.Contains(rendered.FindAll(".tool-contract")[0].TextContent, "cannot be summarized");
        StringAssert.Contains(rendered.FindAll(".tool-contract")[1].TextContent, "No schema was provided");
        Assert.AreEqual(1, rendered.FindAll(".tool-raw-schema").Count);
        StringAssert.Contains(rendered.Find(".tool-raw-schema pre").TextContent, "oneOf");
    }

    [TestMethod]
    public void FrenchChromeKeepsProviderDescriptionsVerbatim()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"key":{"type":"string","description":"Provider-authored English description."}},"required":["key"]}""")
        });

        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        Assert.AreEqual("Ce que fait cet outil", rendered.Find("#tool-capability-heading").TextContent);
        Assert.AreEqual("Entrées", rendered.Find("#tool-input-heading").TextContent);
        Assert.AreEqual("Sorties", rendered.Find("#tool-output-heading").TextContent);
        StringAssert.Contains(rendered.Find(".tool-field").TextContent, "Obligatoire");
        StringAssert.Contains(rendered.Find(".tool-field").TextContent, "Provider-authored English description.");
        StringAssert.Contains(rendered.Find(".tool-capability").TextContent, "Create one durable notification.");
    }

    private static BunitContext CreateContext(ToolSchema schema, string? description = "Create one durable notification.")
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(new ToolClient(new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "Tool",
            Metadata = new ResourceMetadata { Name = "create-notification" },
            Definition = new ToolResourceProperties
            {
                DisplayName = "Create notification",
                Description = description,
                Schema = schema
            }
        }));
        return context;
    }

    private sealed class ToolClient(ToolResource tool) : IToolsClient
    {
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ToolResource>(tool, "\"etag\""));

        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ToolResource>(tool with { Definition = tool.Definition with { Enabled = enabled } }, "\"etag-2\""));

        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;
        private readonly CultureInfo previousUi = CultureInfo.CurrentUICulture;

        public CultureScope(string culture)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
