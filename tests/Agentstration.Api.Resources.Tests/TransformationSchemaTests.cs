using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Functions;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class TransformationSchemaTests : ResourceManagementApiTestBase
{
    [TestMethod]
    public async Task BootstrapDeclaresWithoutExposureThenExplicitApiExposureIsPinnedAndRetrySafe()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        var operation = RssTransformationTestData.Operation(owner);
        var declarationHandler = factory.Services.GetServices<IBootstrapResourceHandler>().Single(h => h.Kind == "Schema");
        var exposureHandler = factory.Services.GetServices<IBootstrapResourceHandler>().Single(h => h.Kind == TransformationSchemaExposureBootstrapResourceHandler.ResourceKind);
        var planning = new BootstrapPlanningContext();
        foreach (var document in RssTransformationTestData.Declarations)
            Assert.AreEqual(BootstrapResourceDisposition.Create, (await declarationHandler.PlanAsync(document, operation, planning, default)).Disposition);
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await exposureHandler.PlanAsync(RssTransformationTestData.Exposure, operation, planning, default)).Disposition);
        Assert.IsNull(await functions.GetAsync(owner, ResourceNamespace.Default, "rss-xml-to-json", default));
        foreach (var document in RssTransformationTestData.Declarations)
        {
            Assert.AreEqual(BootstrapResourceApplyResult.Created, await declarationHandler.ApplyAsync(document, operation, default));
            var first = (await schemas.GetExactAsync(owner, ResourceNamespace.Default, document.Metadata.Name, default))!;
            Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await declarationHandler.ApplyAsync(document, operation, default));
            Assert.AreEqual(first.ETag, (await schemas.GetExactAsync(owner, ResourceNamespace.Default, document.Metadata.Name, default))!.ETag);
            Assert.HasCount(1, await schemas.ListVersionsAsync(owner, ResourceNamespace.Default, document.Metadata.Name, default));
        }
        Assert.IsNull(await functions.GetAsync(owner, ResourceNamespace.Default, "rss-xml-to-json", default));
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        using var response = await client.PostAsJsonAsync("/api/transformation-functions/expose-schema", RssTransformationTestData.Request(owner));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var firstFunction = (await functions.GetAsync(owner, ResourceNamespace.Default, "rss-xml-to-json", default))!;
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await exposureHandler.ApplyAsync(RssTransformationTestData.Exposure, operation, default));
        Assert.AreEqual(firstFunction.ETag, (await functions.GetAsync(owner, ResourceNamespace.Default, "rss-xml-to-json", default))!.ETag);
        Assert.AreEqual(BootstrapResourceDisposition.Skip, (await exposureHandler.PlanAsync(RssTransformationTestData.Exposure, operation, planning, default)).Disposition);
        var accepted = await functions.ResolveAsync(new("rss-xml-to-json", "1.0.0", owner), owner, default);
        Assert.IsNotNull(accepted.Transformation!.Declaration.SchemaUid);
        Assert.IsNotNull(accepted.Transformation.Input.SchemaUid);
        Assert.IsNotNull(accepted.Transformation.Output.SchemaUid);
        Assert.IsNotNull(accepted.Transformation.Engine.Uid);
        Assert.AreEqual("string", accepted.InputSchema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.AreEqual("xml-xsd", accepted.Transformation.Input.Format);
        var exported = ResourceManifestSerializer.ToYaml(firstFunction.Value);
        Assert.Contains("transformationSnapshot", exported);
        Assert.Contains("definitionHash", exported);
        for (var count = 0; count <= 2; count++)
        {
            var result = await functions.InvokeAsync(accepted, owner, JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(count) }), default);
            Assert.AreEqual(count, result.GetProperty("items").GetArrayLength());
            Assert.AreEqual("2.0", result.GetProperty("version").GetString());
            Assert.IsFalse(result.TryGetProperty("description", out _));
            if (count == 0) continue;
            var item = result.GetProperty("items")[0];
            Assert.AreEqual("opaque:0:not-a-uuid", item.GetProperty("guid").GetString());
            Assert.AreEqual("<p>HTML stays data</p>", item.GetProperty("description").GetString());
            Assert.AreEqual("2026-11-17T10:30:00Z", item.GetProperty("publishedAt").GetString());
            Assert.AreEqual("https://example.test/source", item.GetProperty("sourceUrl").GetString());
            Assert.AreEqual(0, item.GetProperty("rating").GetInt32());
        }
        var draft = (await schemas.GetExactAsync(owner, ResourceNamespace.Default, "rss-mapping", default))!;
        var changed = JsonNode.Parse(draft.Value.Definition.Content.GetRawText())!;
        changed["rules"]!["root"]!["fields"]!["title"]!["path"] = "channel/link";
        await schemas.PutAsync(owner, ResourceNamespace.Default, "rss-mapping", draft.Value.Definition with { Content = JsonSerializer.SerializeToElement(changed) }, draft.ETag, default);
        await schemas.PublishAsync(owner, ResourceNamespace.Default, "rss-mapping", "2.0.0", default);
        Assert.AreEqual("Synthetic feed", (await functions.InvokeAsync(accepted, owner, JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(0) }), default)).GetProperty("title").GetString());
        var adopt = RssTransformationTestData.Request(owner) with { Schema = new() { Name = "rss-mapping", Version = "2.0.0" } };
        Assert.AreEqual("transformation_exposure_conflict", (await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => functions.ExposeAsync(adopt, default))).Code);
        await functions.ExposeAsync(adopt with { Version = "2.0.0" }, default);
        var newer = await functions.ResolveAsync(new("rss-xml-to-json", "2.0.0", owner), owner, default);
        Assert.AreEqual("https://example.test/news", (await functions.InvokeAsync(newer, owner, JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(0) }), default)).GetProperty("title").GetString());
        Assert.AreEqual("function_contract_changed", (await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => functions.InvokeAsync(accepted with { Transformation = newer.Transformation }, owner, JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(0) }), default))).Code);
        Assert.AreEqual("function_use_denied", (await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => functions.ResolveAsync(accepted.Reference, ResourceScopeRef.Workspace(Guid.NewGuid()), default))).Code);
    }

    [TestMethod]
    public async Task RealInputAndOutputContractsRejectBadContentAndCurrentSchemaRevocation()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        await RssTransformationTestData.CreateAsync(factory.Services, ResourceScopeRef.Instance);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        // Every schema dependency needs its own descendant Use grant.
        Assert.AreEqual("schema_reference_forbidden", (await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => functions.ExposeAsync(
            RssTransformationTestData.Request(owner) with { Schema = new() { Name = "rss-mapping", Version = "1.0.0", ScopeRef = ResourceScopeRef.Instance } }, default))).Code);
        foreach (var name in new[] { "rss-xml", "rss-json", "rss-mapping" })
        {
            var schema = (await schemas.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, name, default))!;
            await schemas.PutAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, name,
                schema.Value.Definition with { UsePolicy = new() { Grants = [new(owner)] } }, schema.ETag, default);
        }
        await functions.ExposeAsync(RssTransformationTestData.Request(owner) with { Schema = new() { Name = "rss-mapping", Version = "1.0.0", ScopeRef = ResourceScopeRef.Instance } }, default);
        var accepted = await functions.ResolveAsync(new("rss-xml-to-json", "1.0.0", owner), owner, default);
        foreach (var xml in new[] { "<rss>", "<wrong />", "<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><rss>&x;</rss>", RssTransformationTestData.Feed(1).Replace("<guid>", "<wrong>", StringComparison.Ordinal) })
            Assert.AreEqual("transformation_input_invalid", (await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => functions.InvokeAsync(accepted, owner, JsonSerializer.SerializeToElement(new { value = xml }), default))).Code);
        var target = (await schemas.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "rss-json", default))!;
        await schemas.PutAsync(ResourceScopeRef.Instance, target.Value.Namespace, target.Value.Name,
            target.Value.Definition with { Content = RssTransformationTestData.Json("""{"type":"object","required":["missing"]}""") }, target.ETag, default);
        await schemas.PublishAsync(ResourceScopeRef.Instance, target.Value.Namespace, target.Value.Name, "2.0.0", default);
        var mapping = (await schemas.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "rss-mapping", default))!;
        var changed = JsonNode.Parse(mapping.Value.Definition.Content.GetRawText())!;
        changed["output"]!["version"] = "2.0.0";
        await schemas.PutAsync(ResourceScopeRef.Instance, mapping.Value.Namespace, mapping.Value.Name, mapping.Value.Definition with { Content = JsonSerializer.SerializeToElement(changed) }, mapping.ETag, default);
        await schemas.PublishAsync(ResourceScopeRef.Instance, mapping.Value.Namespace, mapping.Value.Name, "2.0.0", default);
        await functions.ExposeAsync(RssTransformationTestData.Request(ResourceScopeRef.Instance) with { Version = "bad-output", Schema = new() { Name = "rss-mapping", Version = "2.0.0" } }, default);
        var badOutput = await functions.ResolveAsync(new("rss-xml-to-json", "bad-output", ResourceScopeRef.Instance), ResourceScopeRef.Instance, default);
        await Assert.ThrowsExactlyAsync<DataContractException>(() => functions.InvokeAsync(badOutput, ResourceScopeRef.Instance, JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(1) }), default));
        var input = (await schemas.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "rss-xml", default))!;
        await schemas.PutAsync(ResourceScopeRef.Instance, input.Value.Namespace, input.Value.Name, input.Value.Definition with { UsePolicy = new() }, input.ETag, default);
        Assert.AreEqual("schema_reference_forbidden", (await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => functions.InvokeAsync(accepted, owner, JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(1) }), default))).Code);
    }

    [TestMethod]
    [DataRow("channel/title/title", "xml_path_missing")]
    [DataRow("channel/item/title", "xml_path_ambiguous")]
    [DataRow("channel/absent", "xml_path_missing")]
    [DataRow("channel/title[1]", "xml_path_invalid")]
    [DataRow("//title", "xml_path_invalid")]
    [DataRow("channel/unknown:title", "xml_namespace_invalid")]
    [DataRow("channel/", "xml_path_invalid")]
    public async Task MappingRejectsUnsupportedMissingAndAmbiguousPaths(string path, string code)
    {
        var rules = JsonNode.Parse(RssTransformationTestData.Declarations[2].Definition.GetProperty("content").GetProperty("rules").GetRawText())!;
        rules["root"]!["fields"]!["title"]!["path"] = path;
        var input = JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(2), rules });
        var error = await Assert.ThrowsExactlyAsync<TransformationFunctionException>(async () => await new XmlToJsonFunction().ExecuteAsync(input, default));
        Assert.AreEqual(code, error.Code);
    }
}
