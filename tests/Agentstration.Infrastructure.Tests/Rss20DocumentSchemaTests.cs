using Agentstration.Asc.Abstractions;
using Agentstration.Asc.OData;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class Rss20DocumentSchemaTests
{
    [TestMethod]
    public void PublishedRssItemProjectionCanBeAdmittedAndQueriedByTheAscDocumentProfile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var manifest = ResourceManifestSerializer.FromYaml<BootstrapResourceDocument>(File.ReadAllText(
            Path.Combine(root, "deploy", "bootstrap", "profiles", "rss-2-0-schemas", "02-rss-2-0-json.yaml")));
        var itemSchema = manifest.Definition.GetProperty("content").GetProperty("properties").GetProperty("items").GetProperty("items");

        DocumentModel.ValidateSchema(itemSchema);
        _ = new ODataDocumentProfile(itemSchema);
    }
}
