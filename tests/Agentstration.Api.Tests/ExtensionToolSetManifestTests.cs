extern alias GitHubExtension;
extern alias GitExtension;
using System.Net.Http.Json;
using Agentstration.Aep.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ExtensionToolSetManifestTests
{
    [TestMethod]
    public async Task GitAndGitHubAdvertiseSeparateCompleteToolSetsWithoutConfiguredAccounts()
    {
        await using var git = new WebApplicationFactory<GitExtension::Program>();
        await using var github = new WebApplicationFactory<GitHubExtension::Program>();
        foreach (var (http, extensionId) in new[] { (git.CreateClient(), "Agentstration.Extensions.Git"), (github.CreateClient(), "Agentstration.Extensions.GitHub") })
        {
            using var client = http;
            var manifest = (await client.GetFromJsonAsync<AepManifest>(AepProtocol.DiscoveryPath, AepProtocol.JsonOptions))!;
            Assert.AreEqual(extensionId, manifest.Extension.Id);
            Assert.IsEmpty(AepDescriptorValidator.Validate(manifest));
            var groups = manifest.Contributions.ToolSets!;
            Assert.IsTrue(groups.All(group => group.Version == "1.0.0"));
            var members = groups.SelectMany(group => group.Members).Select(value => value.ToolId).ToArray();
            CollectionAssert.AreEquivalent(manifest.Contributions.Tools!.Select(value => value.Id).ToArray(), members);
            Assert.AreEqual(members.Length, members.Distinct(StringComparer.Ordinal).Count());
            if (extensionId == "Agentstration.Extensions.Git")
            {
                Assert.AreEqual("tools", groups.Single().Id);
                CollectionAssert.AreEqual(new[] { "git.repository" }, members);
            }
            else
            {
                Assert.HasCount(2, groups);
                CollectionAssert.AreEquivalent(new[] { "github.tracking.describe", "github.tracking.list", "github.tracking.read",
                    "github.forge.describe", "github.forge.resolve", "github.forge.read", "github.forge.observe" },
                    groups.Single(group => group.Id == "github-read").Members.Select(value => value.ToolId).ToArray());
                CollectionAssert.AreEquivalent(new[] { "github.tracking.mutate", "github.forge.create", "github.issue", "github.coding" },
                    groups.Single(group => group.Id == "github-write").Members.Select(value => value.ToolId).ToArray());
            }
        }
    }
}
