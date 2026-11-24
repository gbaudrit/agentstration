using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Functions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class TransformationSchemaBootstrapScopeTests : ResourceManagementApiTestBase
{
    [TestMethod]
    public async Task TheSameProfilesInstallAtEachDeclaredScopeWithoutImplicitExposureOrDuplicateVersions()
    {
        var root = Path.Combine(Path.GetTempPath(), "transformation-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "rss-2-0-schemas", "rss-2-0-functions" })
            {
                var destination = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                foreach (var file in Directory.GetFiles(Path.Combine(RssTransformationTestData.ProfilesPath, name), "*.yaml"))
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
            await using var factory = Factory().WithWebHostBuilder(builder => builder
                .UseSetting("Agentstration:Bootstrap:Path", root)
                .UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false"));
            var context = await GetBootstrapContextAsync(factory);
            using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var bootstrap = factory.Services.GetRequiredService<IBootstrapProfileApiService>();
            var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
            var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
            var owners = new[] { ResourceScopeRef.Instance, ResourceScopeRef.Tenant(context.TenantId), ResourceScopeRef.Workspace(context.WorkspaceId) };
            var functionUids = new HashSet<Guid>();
            foreach (var owner in owners)
            {
                var scope = owner.Kind switch
                {
                    ResourceScopeKind.Instance => BootstrapProfileScope.Instance,
                    ResourceScopeKind.Tenant => BootstrapProfileScope.Tenant,
                    _ => BootstrapProfileScope.Workspace
                };
                BootstrapApplicationTarget? target = scope switch
                {
                    BootstrapProfileScope.Instance => null,
                    BootstrapProfileScope.Tenant => new(TenantId: context.TenantId),
                    _ => new(TenantId: context.TenantId, WorkspaceId: context.WorkspaceId)
                };
                // Only the existing descriptor setting changes; resource files are reused unchanged.
                foreach (var name in new[] { "rss-2-0-schemas", "rss-2-0-functions" })
                {
                    var descriptor = Path.Combine(root, name, "profile.yaml");
                    File.WriteAllText(descriptor, Regex.Replace(File.ReadAllText(descriptor),
                        @"(?m)^  targetScope: \w+", "  targetScope: " + scope.ToString().ToLowerInvariant()));
                }
                var declarations = new BootstrapProfileSelection(["rss-2-0-schemas"], target);
                var combined = new BootstrapProfileSelection(["rss-2-0-schemas", "rss-2-0-functions"], target);
                var preview = await bootstrap.PreviewAsync(combined, context.PrincipalId, default);
                Assert.AreEqual(scope, preview.Scope);
                Assert.HasCount(4, preview.Resources);
                Assert.IsTrue(preview.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Create),
                    JsonSerializer.Serialize(preview.Resources));
                Assert.IsNull(await functions.GetAsync(owner, ResourceNamespace.Default, "rss-2-0-xml-to-json", default));

                var declarationsPreview = await bootstrap.PreviewAsync(declarations, context.PrincipalId, default);
                var installed = await bootstrap.ApplyAsync(declarations, declarationsPreview.Digest, context.PrincipalId, default);
                Assert.AreEqual(BootstrapApplicationStatus.Succeeded, installed.Definition.Status, installed.Definition.Error);
                Assert.IsNull(await functions.GetAsync(owner, ResourceNamespace.Default, "rss-2-0-xml-to-json", default));
                foreach (var name in new[] { "rss-2-0-xml", "rss-2-0-json", "rss-2-0-mapping" })
                {
                    var schema = (await schemas.GetExactAsync(owner, ResourceNamespace.Default, name, default))!.Value;
                    Assert.AreEqual(owner, schema.ScopeRef);
                    Assert.HasCount(1, await schemas.ListVersionsAsync(owner, ResourceNamespace.Default, name, default));
                }
                var exposurePreview = await bootstrap.PreviewAsync(combined, context.PrincipalId, default);
                Assert.AreEqual(3, exposurePreview.Resources.Count(resource => resource.Disposition == BootstrapResourceDisposition.Skip));
                var exposed = await bootstrap.ApplyAsync(combined, exposurePreview.Digest, context.PrincipalId, default);
                Assert.AreEqual(BootstrapApplicationStatus.Succeeded, exposed.Definition.Status, exposed.Definition.Error);
                var first = (await functions.GetAsync(owner, ResourceNamespace.Default, "rss-2-0-xml-to-json", default))!;
                Assert.IsTrue(functionUids.Add(first.Value.Uid), "Different owners retain distinct resource identities.");
                var accepted = await functions.ResolveAsync(new("rss-2-0-xml-to-json", "1.0.0", owner), owner, default);
                Assert.AreEqual(owner, accepted.Transformation!.Declaration.Source!.ScopeRef);
                Assert.AreEqual(owner, accepted.Transformation.Input.Source!.ScopeRef);
                Assert.AreEqual(owner, accepted.Transformation.Output.Source!.ScopeRef);
                Assert.AreEqual(ResourceScopeRef.Instance, accepted.Transformation.Engine.ScopeRef);
                var result = await functions.InvokeAsync(accepted, owner,
                    JsonSerializer.SerializeToElement(new { value = RssTransformationTestData.Feed(1) }), default);
                Assert.HasCount(1, result.GetProperty("items").EnumerateArray().ToArray());

                var replay = await bootstrap.PreviewAsync(combined, context.PrincipalId, default);
                Assert.IsTrue(replay.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Skip));
                var reapplied = await bootstrap.ApplyAsync(combined, replay.Digest, context.PrincipalId, default);
                Assert.AreEqual(BootstrapApplicationStatus.Succeeded, reapplied.Definition.Status, reapplied.Definition.Error);
                Assert.AreEqual(first.ETag, (await functions.GetAsync(owner, ResourceNamespace.Default, "rss-2-0-xml-to-json", default))!.ETag);
                if (scope != BootstrapProfileScope.Instance)
                    await Assert.ThrowsAsync<BootstrapRequestException>(() => bootstrap.PreviewAsync(
                        combined with { Target = null }, context.PrincipalId, default));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
