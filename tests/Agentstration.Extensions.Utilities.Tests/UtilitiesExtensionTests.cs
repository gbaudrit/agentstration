using System.Globalization;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agentstration.Extensions.Utilities.Tests;

[TestClass]
public sealed class UtilitiesExtensionTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    [TestMethod]
    public async Task ManifestSeparatesAgentToolsFromSchemaOperationsAndPinsTheirMcpMappings()
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        var manifest = await new AepClient(http).GetManifestAsync();

        Assert.AreEqual("Agentstration.Extensions.Utilities", manifest.Extension.Id);
        Assert.IsEmpty(AepDescriptorValidator.Validate(manifest));
        Assert.AreEqual("utilities", manifest.Mcp!.Servers.Single().Id);
        Assert.AreEqual("/mcp", manifest.Mcp.Servers.Single().Endpoint);
        CollectionAssert.AreEquivalent(new[]
        {
            "hash.compute:utilities:hash_compute",
            "json.compact:utilities:json_compact",
            "text.upper:utilities:text_upper"
        }, manifest.Contributions.Tools!.Select(tool => $"{tool.Id}:{tool.Mcp.Server}:{tool.Mcp.Tool}").ToArray());

        var format = manifest.Contributions.SchemaFormats!.Single();
        Assert.AreEqual("json-schema.validation", format.Id);
        Assert.AreEqual("json-schema", format.Format);
        CollectionAssert.AreEqual(new[] { Draft202012 }, format.Dialects.ToArray());
        Assert.IsFalse(format.IsDefault);
        Assert.AreEqual(new AepMcpToolMapping("utilities", "schema_validate_definition"), format.Operations.ValidateDefinition);
        Assert.AreEqual(new AepMcpToolMapping("utilities", "schema_validate_instance"), format.Operations.ValidateInstance);
    }

    [TestMethod]
    public async Task McpCatalogExposesExactlyTheOperationsMappedByAep()
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        var manifest = await new AepClient(http).GetManifestAsync();
        await using var mcp = await ConnectAsync(http);
        var tools = await mcp.ListToolsAsync();
        var format = manifest.Contributions.SchemaFormats!.Single();
        var mapped = manifest.Contributions.Tools!.Select(tool => tool.Mcp.Tool)
            .Concat([format.Operations.ValidateDefinition.Tool, format.Operations.ValidateInstance.Tool]).ToArray();

        CollectionAssert.AreEquivalent(mapped, tools.Select(tool => tool.Name).ToArray());
        foreach (var name in new[] { "hash_compute", "json_compact", "text_upper" })
        {
            var schema = tools.Single(tool => tool.Name == name).JsonSchema;
            Assert.IsTrue(schema.GetProperty("properties").TryGetProperty("dryRun", out _));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HashUsesTheKnownSha256VectorThroughMcp(bool dryRun)
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        await using var mcp = await ConnectAsync(http);
        var result = await mcp.CallToolAsync("hash_compute", new Dictionary<string, object?> { ["text"] = "abc", ["dryRun"] = dryRun });

        Assert.IsFalse(result.IsError is true);
        Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Text(result));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JsonCompactionPreservesNestedValuesThroughMcp(bool dryRun)
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        await using var mcp = await ConnectAsync(http);
        var result = await mcp.CallToolAsync("json_compact", new Dictionary<string, object?>
        {
            ["json"] = " { \"a\" : 1, \"nested\" : [ true, null ] } ",
            ["dryRun"] = dryRun
        });

        Assert.IsFalse(result.IsError is true);
        Assert.AreEqual("{\"a\":1,\"nested\":[true,null]}", Text(result));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UppercaseReturnsItsResultThroughMcp(bool dryRun)
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        await using var mcp = await ConnectAsync(http);
        var result = await mcp.CallToolAsync("text_upper", new Dictionary<string, object?> { ["text"] = "utilities", ["dryRun"] = dryRun });

        Assert.IsFalse(result.IsError is true);
        Assert.AreEqual("UTILITIES", Text(result));
    }

    [TestMethod]
    public void UppercaseUsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.AreEqual("IDI", UtilityTools.Uppercase("idi"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public async Task InvalidJsonIsAnMcpToolError()
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        await using var mcp = await ConnectAsync(http);
        var result = await mcp.CallToolAsync("json_compact", new Dictionary<string, object?> { ["json"] = "{" });

        Assert.IsTrue(result.IsError is true);
        StringAssert.Contains(Text(result), "json_compact");
    }

    [TestMethod]
    public async Task SchemaOperationsReturnValidationDataThroughMcp()
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        await using var mcp = await ConnectAsync(http);
        var definition = JsonSerializer.SerializeToElement(new { type = "string" });
        var arguments = new Dictionary<string, object?> { ["definition"] = definition, ["dialect"] = Draft202012 };
        Assert.IsEmpty(Validation(await mcp.CallToolAsync("schema_validate_definition", arguments)).Issues);
        arguments["instance"] = "value";
        Assert.IsEmpty(Validation(await mcp.CallToolAsync("schema_validate_instance", arguments)).Issues);
        arguments["instance"] = 42;
        var invalid = Validation(await mcp.CallToolAsync("schema_validate_instance", arguments));

        Assert.HasCount(1, invalid.Issues);
        Assert.AreEqual("instance_type", invalid.Issues.Single().Code);
        Assert.AreEqual("$", invalid.Issues.Single().Path);
    }

    [TestMethod]
    [DataRow("[]", null, "definition_type")]
    [DataRow("{\"type\":17}", null, "unsupported_type")]
    [DataRow("{\"type\":\"unknown\"}", null, "unsupported_type")]
    [DataRow("{\"type\":\"object\"}", "https://example.invalid/dialect", "unsupported_dialect")]
    public async Task InvalidDefinitionsAndDialectsReturnTheSameIssueForBothOperations(string definition, string? dialect, string code)
    {
        await using var host = new WebApplicationFactory<global::Program>();
        using var http = host.CreateClient();
        await using var mcp = await ConnectAsync(http);
        var arguments = new Dictionary<string, object?> { ["definition"] = JsonSerializer.Deserialize<JsonElement>(definition) };
        if (dialect is not null) arguments["dialect"] = dialect;
        var definitionResult = Validation(await mcp.CallToolAsync("schema_validate_definition", arguments));
        arguments["instance"] = 42;
        var instanceResult = Validation(await mcp.CallToolAsync("schema_validate_instance", arguments));

        Assert.HasCount(1, definitionResult.Issues);
        Assert.AreEqual(code, definitionResult.Issues.Single().Code);
        CollectionAssert.AreEqual(definitionResult.Issues.ToArray(), instanceResult.Issues.ToArray());
    }

    private static Task<McpClient> ConnectAsync(HttpClient http) => McpClient.CreateAsync(
        new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "utilities-test" },
            http, NullLoggerFactory.Instance, ownsHttpClient: false), loggerFactory: NullLoggerFactory.Instance);

    private static string Text(CallToolResult result) => Assert.IsInstanceOfType<TextContentBlock>(result.Content.Single()).Text;

    private static AepSchemaValidationResult Validation(CallToolResult result)
    {
        Assert.IsFalse(result.IsError is true);
        var value = result.StructuredContent is { } structured
            ? structured.Deserialize<AepSchemaValidationResult>(AepProtocol.JsonOptions)
            : JsonSerializer.Deserialize<AepSchemaValidationResult>(Text(result), AepProtocol.JsonOptions);
        Assert.IsNotNull(value);
        return value;
    }
}
