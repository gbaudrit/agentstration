using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Afc.Abstractions;

namespace Agentstration.Afc.Aep;

// Optional AEP adapter: uses existing versioned Tool metadata and MCP mappings.
// The neutral contract, client and server do not depend on AEP or product resources.
public static class AfcAepContributions
{
    public static IReadOnlyList<AepToolContribution> Create(string contributionId, string server, string prefix = "afc")
    {
        AfcValidation.Text(contributionId); AfcValidation.Text(server);
        var declaration = JsonSerializer.SerializeToElement(AfcSchemas.Declaration(prefix), AfcContract.Json);
        return AfcContract.Tools.Select(operation => new AepToolContribution(contributionId + "." + operation,
            "Forge " + operation, new(server, prefix + "_" + operation),
            Metadata: new Dictionary<string, JsonElement> { [AfcContract.Id] = declaration })).ToArray();
    }

    public static AfcDeclaration Read(AepToolContribution contribution)
    {
        AfcValidation.Require(contribution.Metadata is not null && contribution.Metadata.TryGetValue(AfcContract.Id, out _), "incompatible_contract");
        var declaration = AfcValidation.Parse<AfcDeclaration>(contribution.Metadata[AfcContract.Id]);
        AfcSchemas.Validate(declaration);
        AfcValidation.Require(AfcContract.Tools.Any(operation => contribution.Mcp.Tool == declaration.ToolPrefix + "_" + operation), "incompatible_mapping");
        return declaration;
    }

    public static IReadOnlyList<string> Validate(AepManifest manifest)
    {
        var errors = new List<string>();
        var groups = new Dictionary<(string Server, string Prefix), List<AepToolContribution>>();
        foreach (var tool in manifest.Contributions.Tools ?? [])
        {
            if (tool.Metadata?.ContainsKey(AfcContract.Id) != true) continue;
            try
            {
                var declaration = Read(tool);
                AfcValidation.Require(manifest.Mcp?.Servers.Count(s => s.Id == tool.Mcp.Server) == 1, "incompatible_mapping");
                var key = (tool.Mcp.Server, declaration.ToolPrefix);
                if (!groups.TryGetValue(key, out var group)) { group = []; groups.Add(key, group); }
                group.Add(tool);
            }
            catch (AfcException error) { errors.Add(error.Code); }
        }
        foreach (var group in groups.Values)
        {
            var declaration = Read(group[0]);
            if (group.Count != AfcContract.Tools.Count || AfcContract.Tools.Any(operation =>
                group.Count(t => t.Mcp.Tool == declaration.ToolPrefix + "_" + operation) != 1)
                || group.Any(t => Read(t).Version != declaration.Version)) errors.Add("incompatible_mapping");
        }
        return errors;
    }
}
