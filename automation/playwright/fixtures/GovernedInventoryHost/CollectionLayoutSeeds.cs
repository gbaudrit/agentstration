using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Playwright.GovernedInventory;

// Saved, disabled metadata for layout inspection only. No acquisition, query or
// unavailable operation is executed. The synthetic pins are deliberately inert.
internal static class CollectionLayoutSeeds
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        using var request = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = services.GetRequiredService<IResourceStore>();
        var owner = ResourceScopeRef.Instance;
        var ns = ResourceNamespace.Default;
        var source = new DataSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSource,
            ScopeRef = owner,
            Metadata = new() { Name = "layout-query-dataset" },
            Definition = new()
            {
                DisplayName = "Collection layout inspection dataset",
                Enabled = false,
                Description = "Disabled synthetic metadata; no acquisition or query execution is claimed.",
                Dataset = new(null, new(Write: true), new(new(new(owner, ns, "ConnectedServiceOperation", "layout-unavailable-operation"),
                    Guid.NewGuid(), 1, new string('0', 64)), JsonSerializer.SerializeToElement(new { })))
                { DocumentCollectionUid = Guid.NewGuid() }
            }
        };
        source = (await store.PutExactAsync(owner, source, null, true, ct)).Value;
        var functions = await store.ListExactAsync<TransformationFunctionResource>(owner, FunctionResourceKinds.TransformationFunction, 0, 100, ct);
        var native = functions.First().Value;
        for (var index = 1; index <= 3; index++)
        {
            await store.PutExactAsync(owner, new DataSourceQueryResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = DataSourceResourceKinds.DataSourceQuery,
                ScopeRef = owner,
                Metadata = new() { Name = $"layout-query-{index}-long-technical-name-for-column-boundaries" },
                Definition = new()
                {
                    DisplayName = $"Query {index} — Analyse détaillée des documents et des données de production / Long document analysis and production records",
                    Enabled = false,
                    Query = JsonSerializer.SerializeToElement(new { filter = "layout inspection only" }),
                    Selection = new()
                    {
                        Source = new(source.Name, owner, ns),
                        SourceUid = source.Uid,
                        AcquisitionScope = owner,
                        Contract = new(new("layout-inert-profile", "1.0.0", "application/json", new string('0', 64)))
                    }
                }
            }, null, true, ct);
            await store.PutExactAsync(owner, native with
            {
                Uid = Guid.Empty,
                Generation = 0,
                Governance = ResourceGovernance.User,
                Metadata = new() { Name = $"aaa-layout-function-{index}-long-technical-name-for-column-boundaries" },
                Definition = native.Definition with
                {
                    DisplayName = $"AAA Function {index} — Transformation détaillée des documents et des données de production / Long document transformation",
                    Description = string.Join(" ", Enumerable.Repeat("Analyse et transforme les documents enregistrés tout en conservant les références exactes, les versions et les contrats. Reads and transforms saved documents while preserving exact resource references and contracts.", 2))
                }
            }, null, true, ct);
        }
    }
}
