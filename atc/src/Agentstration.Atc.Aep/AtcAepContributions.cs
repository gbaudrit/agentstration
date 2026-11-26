using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Atc.Abstractions;

namespace Agentstration.Atc.Aep;

// Optional AEP adapter: uses existing versioned Tool metadata and MCP mappings.
// The neutral contract, client and server do not depend on AEP or product resources.
public static class AtcAepContributions
{
    public static IReadOnlyList<AepToolContribution> Create(string contributionId, string server, string prefix = "atc")
    {
        AtcValidation.Text(contributionId); AtcValidation.Text(server);
        var declaration = JsonSerializer.SerializeToElement(AtcSchemas.Declaration(prefix), AtcContract.Json);
        return AtcContract.Tools.Select(operation => new AepToolContribution(contributionId + "." + operation,
            "Tracking " + operation, new(server, prefix + "_" + operation),
            Metadata: new Dictionary<string, JsonElement> { [AtcContract.Id] = declaration })).ToArray();
    }

    public static AtcDeclaration Read(AepToolContribution contribution)
    {
        AtcValidation.Require(contribution.Metadata is not null && contribution.Metadata.TryGetValue(AtcContract.Id, out _), "incompatible_contract");
        var declaration = AtcValidation.Parse<AtcDeclaration>(contribution.Metadata[AtcContract.Id]);
        AtcSchemas.Validate(declaration);
        AtcValidation.Require(AtcContract.Tools.Any(operation => contribution.Mcp.Tool == declaration.ToolPrefix + "_" + operation), "incompatible_mapping");
        return declaration;
    }

    public static IReadOnlyList<string> Validate(AepManifest manifest)
    {
        var errors = new List<string>();
        var groups = new Dictionary<(string Server, string Prefix), List<AepToolContribution>>();
        foreach (var tool in manifest.Contributions.Tools ?? [])
        {
            if (tool.Metadata?.ContainsKey(AtcContract.Id) != true) continue;
            try
            {
                var declaration = Read(tool);
                AtcValidation.Require(manifest.Mcp?.Servers.Count(s => s.Id == tool.Mcp.Server) == 1, "incompatible_mapping");
                var key = (tool.Mcp.Server, declaration.ToolPrefix);
                if (!groups.TryGetValue(key, out var group)) { group = []; groups.Add(key, group); }
                group.Add(tool);
            }
            catch (AtcException error) { errors.Add(error.Code); }
        }
        foreach (var group in groups.Values)
        {
            var declaration = Read(group[0]);
            if (group.Count != AtcContract.Tools.Count || AtcContract.Tools.Any(operation =>
                group.Count(t => t.Mcp.Tool == declaration.ToolPrefix + "_" + operation) != 1)
                || group.Any(t => Read(t).Version != declaration.Version)) errors.Add("incompatible_mapping");
        }
        return errors;
    }
}
