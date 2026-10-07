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

    [TestMethod]
    public void ExecutionTabReusesSchemaEditorAndSimulatesByDefault()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"dryRun":{"type":"boolean"}},"additionalProperties":false}""")
        });
        var client = context.Services.GetRequiredService<ToolClient>();
        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        Assert.AreEqual("true", rendered.Find("#tool-overview-tab").GetAttribute("aria-selected"));
        rendered.Find("#tool-execution-tab").Click();

        Assert.AreEqual("true", rendered.Find("#tool-execution-tab").GetAttribute("aria-selected"));
        Assert.IsNotNull(rendered.Find("[data-testid='schema-input-editor']"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-simulate-mode']").ClassName, "selected");
        var dryRun = rendered.Find("[data-schema-path='$.dryRun'] input");
        Assert.IsTrue(dryRun.HasAttribute("checked"));
        Assert.IsTrue(dryRun.HasAttribute("disabled"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-dry-run-managed']").TextContent, "automatically");
        rendered.Find("[data-testid='tool-run-submit']").Click();

        rendered.WaitForAssertion(() => Assert.HasCount(1, client.RunRequests));
        Assert.AreEqual(ToolRunMode.Simulate, client.RunRequests[0].Mode);
        Assert.IsTrue(client.RunRequests[0].Arguments.GetProperty("dryRun").GetBoolean());
        Assert.IsNotNull(rendered.Find("[data-testid='tool-run-result']"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-result']").TextContent, "Provider invokedYes");
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-output']").TextContent, "dryRun");

        rendered.Find("[data-testid='tool-run-execute-mode']").Click();
        dryRun = rendered.Find("[data-schema-path='$.dryRun'] input");
        Assert.IsFalse(dryRun.HasAttribute("checked"));
        Assert.IsFalse(dryRun.HasAttribute("disabled"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='tool-run-dry-run-managed']"));
    }

    [TestMethod]
    public void RealExecutionPutsToolOutputBeforeTechnicalDetails()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{},"additionalProperties":false}""")
        });
        var client = context.Services.GetRequiredService<ToolClient>();
        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        rendered.Find("#tool-execution-tab").Click();
        rendered.Find("[data-testid='tool-run-execute-mode']").Click();
        rendered.Find("[data-testid='tool-run-submit']").Click();

        rendered.WaitForAssertion(() => Assert.HasCount(1, client.RunRequests));
        Assert.AreEqual(ToolRunMode.Execute, client.RunRequests[0].Mode);
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-output']").TextContent, "created");
        Assert.IsNotNull(rendered.Find(".tool-run-diagnostics"));
    }

    [TestMethod]
    public void SimulationIsDisabledWhenDryRunIsNotDeclared()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{},"additionalProperties":false}""")
        });
        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        rendered.Find("#tool-execution-tab").Click();

        Assert.IsTrue(rendered.Find("[data-testid='tool-run-simulate-mode']").HasAttribute("disabled"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-execute-mode']").ClassName, "selected");
        StringAssert.Contains(rendered.Find(".tool-run-mode-unavailable").TextContent, "does not declare a boolean dryRun");
    }

    private static BunitContext CreateContext(ToolSchema schema, string? description = "Create one durable notification.")
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new ToolClient(new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "Tool",
            Metadata = new ResourceMetadata { Name = "create-notification" },
            Definition = new ToolResourceProperties
            {
                DisplayName = "Create notification",
                Description = description,
                Schema = schema,
                Provider = new ResourceReference("provider"),
                ExternalId = "create-notification",
                Discovery = new ToolDiscoveryState
                {
                    Available = true,
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow
                }
            }
        });
        context.Services.AddSingleton(client);
        context.Services.AddSingleton<IToolsClient>(client);
        return context;
    }

    private sealed class ToolClient(ToolResource tool) : IToolsClient
    {
        public List<RunToolRequest> RunRequests { get; } = [];

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

        public Task<RunToolResponse> RunToolAsync(ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken)
        {
            RunRequests.Add(request);
            return Task.FromResult(new RunToolResponse(
                request.Mode,
                request.Mode == ToolRunMode.Simulate ? "simulated" : "completed",
                name,
                @namespace.Value,
                "provider",
                true,
                false,
                [new ToolRunCheck(request.Mode == ToolRunMode.Simulate ? "dry_run" : "execution", "passed", "Completed.")],
                request.Mode == ToolRunMode.Execute
                    ? JsonSerializer.SerializeToElement(new { status = "created" })
                    : JsonSerializer.SerializeToElement(new { dryRun = true })));
        }
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
