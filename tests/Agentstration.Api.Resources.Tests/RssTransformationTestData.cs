using System.Text.Json;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

internal static class RssTransformationTestData
{
    public static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    public static IReadOnlyList<BootstrapResourceDocument> Declarations => Read("rss-schemas");
    public static BootstrapResourceDocument Exposure => Read("rss-functions").Single();
    public static ExposeTransformationSchemaRequest Request(ResourceScopeRef owner) => new("rss-xml-to-json", "RSS XML to JSON", "1.0.0",
        new() { Name = "rss-mapping", Version = "1.0.0" }, owner, ResourceNamespace.Default);

    public static string Feed(int count) => """
        <rss version="2.0" xmlns:ext="urn:agentstration:rss:example"><channel><title>Synthetic feed</title><link>https://example.test/news</link>
        """ + string.Concat(Enumerable.Range(0, count).Select(index => $"""
        <item><guid>opaque:{index}:not-a-uuid</guid><title>Item {index}</title><description><![CDATA[<p>HTML stays data</p>]]></description><pubDate>Tue, 17 Nov 2026 11:30:00 +0100</pubDate><source url="https://example.test/source">Source</source><ext:rating>{index}</ext:rating></item>
        """)) + "</channel></rss>";

    public static async Task<TransformationFunctionResource> CreateAsync(IServiceProvider services, ResourceScopeRef owner)
    {
        var operation = Operation(owner);
        var handler = services.GetServices<IBootstrapResourceHandler>().Single(h => h.Kind == "Schema");
        foreach (var document in Declarations) await handler.ApplyAsync(document, operation, default);
        return (await services.GetRequiredService<TransformationFunctionService>().ExposeAsync(Request(owner), default)).Value;
    }

    public static BootstrapResourceOperationContext Operation(ResourceScopeRef owner) => new("rss", Path.GetTempPath(),
        owner.Kind == ResourceScopeKind.Instance ? BootstrapProfileScope.Instance : BootstrapProfileScope.Workspace,
        owner.Kind == ResourceScopeKind.Workspace ? new BootstrapApplicationTarget(WorkspaceId: owner.TargetId) : null);

    private static IReadOnlyList<BootstrapResourceDocument> Read(string profile)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        return Directory.GetFiles(Path.Combine(root, "deploy/bootstrap/profiles", profile), "*.yaml")
            .Where(path => Path.GetFileName(path) != "profile.yaml").Order(StringComparer.Ordinal)
            .Select(path => ResourceManifestSerializer.FromYaml<BootstrapResourceDocument>(File.ReadAllText(path))).ToArray();
    }
}
