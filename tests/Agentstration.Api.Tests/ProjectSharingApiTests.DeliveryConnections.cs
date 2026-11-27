extern alias GitHubExtension;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.ResourceManagement;
using Agentstration.Work;
using Agentstration.Testing;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    private static async Task<WorkProjectConnectionSelection[]> DeliveryConnectionsAsync(SharingHost host,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<GitHubExtension::Program> extension, bool first, AuthenticatedGitFixture native)
    {
        WorkProjectConnectionSelection tracking, forge, other;
        using (host.Scope())
        {
            tracking = await GitHubProviderTests.InstallIssueServiceAsync(host.Host.Services, host.Context, extension, alternateAccount: !first, instanceService: first);
            forge = await GitHubProviderTests.InstallDeliveryForgeServiceAsync(host.Host.Services, host.Context, "forge", first, first);
            other = await GitHubProviderTests.InstallDeliveryForgeServiceAsync(host.Host.Services, host.Context, "other-forge", false, !first, "43", "fixture/second");
        }
        await DeliveryGitServicesAsync(host, first, native);
        await ManageSeedDescribeAsync(host);
        return [tracking, forge, other];
    }
    private static byte[] DeliveryNamedSeed(byte[] bytes, IReadOnlyList<WorkProjectConnectionSelection> connections)
    {
        using var input = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in input.Entries)
            {
                using var reader = new StreamReader(entry.Open()); var text = reader.ReadToEnd();
                if (entry.FullName is "bootstrap/10-relay.yaml" or "bootstrap/11-code-relay.yaml") continue;
                if (entry.FullName is "bootstrap/profile.yaml" or "bootstrap/20-project.yaml")
                {
                    var node = JsonNode.Parse(ResourceManifestSerializer.FromYamlStrict<JsonElement>(text).GetRawText())!;
                    if (entry.FullName == "bootstrap/profile.yaml")
                        node["definition"]!["bindings"] = JsonSerializer.SerializeToNode(connections.Select(value => value.Requirement.Name).Concat(new[] { "sharing", "code-publication" }).Select(name => new
                        { name = name + "-service", targetKind = "connectedService", displayName = "Local " + name, required = true }), Json);
                    else
                    {
                        node["definition"]!["nativeServices"] = JsonSerializer.SerializeToNode(new[] { "sharing", "code-publication" }.Select(name => new
                        {
                            name,
                            service = new { binding = name + "-service" },
                            tool = new { extensionContribution = new { kind = "Tool", extensionId = "Agentstration.Extensions.Git", contributionId = "git.repository" } }
                        }), Json);
                        node["definition"]!["connectionRequirements"] = JsonSerializer.SerializeToNode(connections.Select(value => value.Requirement), Json);
                        node["definition"]!["connectionAssignments"] = JsonSerializer.SerializeToNode(connections.Select(value => new
                        {
                            name = value.Requirement.Name,
                            service = new { binding = value.Requirement.Name + "-service" },
                            describeTool = new
                            {
                                extensionContribution = new
                                {
                                    kind = "Tool",
                                    extensionId = "Agentstration.Extensions.GitHub",
                                    contributionId = value.Requirement.Contract == WorkProjectConnectionRules.Tracking ? "github.tracking.describe" : "github.forge.describe"
                                }
                            },
                            exports = WorkProjectConnectionRules.TargetFields.Select(field => new { field, exportId = field })
                        }), Json);
                    }
                    text = ResourceManifestSerializer.ToYaml(JsonSerializer.SerializeToElement(node, Json));
                }
                WriteSeed(archive, entry.FullName, text);
            }
        return output.ToArray();
    }
}
