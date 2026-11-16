using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Knowledge;

public sealed partial class KnowledgeSourceToolExposureService
{
    public async Task<KnowledgeSourceToolProposal> ProposeAsync(KnowledgeSourceId id, CancellationToken ct)
    {
        var scope = KnowledgeOperationalRead.AdministrativeScope(requestContext) ?? CurrentWorkspaceScope();
        var source = (await sources.ResolveForUseAsync(id, scope, ct)).Value;
        var readiness = await sources.GetReadinessForUseAsync(id, scope, ct);
        var previous = (await GetAsync(id, ct))?.Value;
        StorageDocumentDescription? description = null;
        string? capabilityIssue = null;
        if (source.Definition.Output is { DocumentCollectionUid: { } collection } output)
        {
            try
            {
                description = documents is null ? null : await documents.DescribeAsync(new(output.Storage,
                    new(scope, source.Address), collection, new(QueryProfiles: null)), ct);
                if (description is null) capabilityIssue = "knowledge_documents_unavailable";
            }
            catch (StorageException e) { capabilityIssue = e.Code; }
        }
        KnowledgeSnapshotView? active = null;
        try { if (snapshots is not null) active = await snapshots.GetActiveAsync(id, ct); }
        catch (Agentstration.Identity.Contracts.AuthorizationDeniedException) { /* Configuration remains inspectable without Snapshot metadata permission. */ }
        var currentSnapshot = active?.Snapshot.KnowledgeSourceGeneration == source.Generation ? active.Snapshot : null;
        var tools = new List<KnowledgeToolProposal>();
        Add(KnowledgeSourceOperation.Describe, null, source.Definition.DisplayName, source.Definition.Description, null);
        Add(KnowledgeSourceOperation.GetStatus, null, source.Definition.DisplayName, source.Definition.Description, null);
        foreach (var binding in source.Definition.DataSources)
        {
            var pin = source.Composition?.DataSources.SingleOrDefault(b => b.BindingName == binding.Name);
            var selection = new KnowledgeContributionSelection(binding.Name, pin?.DataSourceUid ?? Guid.Empty);
            AddAccess(selection, binding.Name, binding.Description,
                pin is null ? "knowledge_contribution_unresolved" : readiness.DataSources?.SingleOrDefault(b => b.Name == binding.Name)?.Issue);
        }
        // A post-aggregation transformation may create a new ensemble rather than preserving input identities.
        if (source.Definition.Transformations.Count > 0 || source.Definition.ProjectionFlow is { Name: not "knowledge-projection-builtin" })
            AddAccess(new("derived", source.Uid), source.Definition.DisplayName, source.Definition.Description, null);
        var issues = readiness.Issues.ToList();
        var existingSet = await toolSets.GetExactAsync(scope, source.Namespace, source.Name + ".access", ct);
        if (existingSet is not null && !IsOwnedBy(existingSet.Value.Metadata, source.Uid))
            issues.Add("knowledge_source_tool_set_conflict");
        for (var index = 0; index < tools.Count; index++)
        {
            var candidate = tools[index];
            var definition = await store.GetExactAsync<Agentstration.Tools.ToolDefinitionResource>(new(scope, source.Namespace,
                Agentstration.Tools.ToolResourceKinds.ToolDefinition, candidate.DefinitionName), ct);
            if (definition is not null && !IsOwnedBy(definition.Value.Metadata, source.Uid))
            {
                tools[index] = candidate with { Issue = "knowledge_source_tool_definition_conflict" };
                issues.Add("knowledge_source_tool_definition_conflict");
            }
        }
        var current = previous?.Operations.ToDictionary(o => o.Route, StringComparer.Ordinal)
            ?? new Dictionary<string, KnowledgeSourceToolOperationExposure>(StringComparer.Ordinal);
        tools = tools.Select(t => t with
        {
            Change = t.Issue is not null ? "unavailable" : !current.TryGetValue(t.Route, out var old) ? "added"
                : old.Proposal?.ContractDigest == t.ContractDigest ? "unchanged" : "modified"
        }).ToList();
        foreach (var old in current.Values.Where(o => tools.All(t => t.Route != o.Route)))
            tools.Add(old.Proposal is { } proposal ? proposal with { Change = "removed" }
                : new()
                {
                    Route = old.Route,
                    DefinitionName = old.Tool.Name,
                    Operation = old.Operation,
                    DisplayName = old.Route,
                    Description = old.Route,
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                    ContractDigest = string.Empty,
                    Change = "removed"
                });
        var digest = Digest(new
        {
            source.Uid,
            source.Generation,
            source.Definition,
            source.Composition,
            readiness.Ready,
            Issues = issues,
            Tools = tools.Where(t => t.Change != "removed").Select(t => t with { Change = "added" }).ToArray()
        });
        return new()
        {
            SourceUid = source.Uid,
            SourceGeneration = source.Generation,
            Digest = digest,
            ToolSetName = source.Name + ".access",
            PublishedVersion = previous?.ToolSetVersion,
            Ready = readiness.Ready && capabilityIssue is null && issues.Count == 0,
            Issues = issues,
            Tools = tools
        };

        void AddAccess(KnowledgeContributionSelection selection, string name, string? businessDescription, string? issue)
        {
            var members = currentSnapshot?.Artifacts.Where(a => KnowledgeContributions.FromSnapshot(currentSnapshot, a) == selection).ToArray();
            if (currentSnapshot?.Artifacts.Any(a => KnowledgeContributions.FromSnapshot(currentSnapshot, a) is null) == true)
                issue ??= "knowledge_contribution_evidence_unavailable";
            if (members is { Length: 0 })
                issue ??= "knowledge_contribution_no_published_members";
            var hasText = members is not { Length: > 0 } || members.Any(a => a.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || a.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || a.MediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || a.MediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase));
            var readIssue = issue ?? (!hasText ? "knowledge_contribution_text_unsupported" : null) ?? capabilityIssue
                ?? (source.Definition.Output?.Rule.Read == false ? "knowledge_read_unsupported" : null) ?? (description is { Policy.Get: false } ? "knowledge_read_unsupported" : null);
            Add(KnowledgeSourceOperation.Read, selection, name, businessDescription, readIssue);
            Add(KnowledgeSourceOperation.SearchRegex, selection, name, businessDescription, readIssue);
            if (source.Definition.Output?.DocumentCollectionUid is not null)
                Add(KnowledgeSourceOperation.StructuredQuery, selection, name, businessDescription,
                    issue ?? capabilityIssue ?? (description is not { Policy.Query: true, Capabilities.ImmutablePublications: true, Capabilities.DocumentSelection: true }
                        || description.QueryProfiles.Count == 0 ? "knowledge_contribution_query_unsupported" : null));
        }
        void Add(KnowledgeSourceOperation operation, KnowledgeContributionSelection? selection, string name, string? businessDescription, string? issue)
        {
            var route = selection is null ? KnowledgeAccessSchemas.Route(operation)
                : ContributionRoute(selection) + "." + KnowledgeAccessSchemas.Route(operation);
            var profile = operation == KnowledgeSourceOperation.StructuredQuery ? description?.QueryProfiles.FirstOrDefault() : null;
            var help = operation switch
            {
                KnowledgeSourceOperation.Describe => "Describe this published Knowledge ToolSet, its contributions, contracts and exact Snapshot metadata without reading content or preparing data. request accepts {} or an exact snapshotId.",
                KnowledgeSourceOperation.GetStatus => "Inspect recorded Snapshot status and input freshness without reading content or starting preparation. request accepts {}.",
                KnowledgeSourceOperation.Read => "Read bounded Unicode chunks of text-readable content in this contribution only. Binary documents require a configured text transformation. Reuse the snapshotId, documentId and readRange from its search. Without snapshotId, prepare current data if needed. Continue with cursor only.",
                KnowledgeSourceOperation.SearchRegex => "Search text-readable content in this contribution only with a bounded .NET non-backtracking regular expression (no lookarounds/backreferences). Returns exact snapshotId, documentId and ranges for read. Continue with cursor only.",
                _ => "Query this contribution's immutable structured records only using the fixed profile/model. Initial request supplies query; continuations supply cursor only. Filtering, ordering, selection and aggregates depend on the supported profile. No SQL, writes or destination selection."
            };
            if (profile is { Id: "odata/4.01" })
                help += " Query encoding: odata-options+json, e.g. query: {}. Supports the reviewed $filter/$select/$orderby and simple $apply aggregate/groupby subset on declared fields. Use request.pageSize for pages; $top/$skip, joins and expansion are unsupported.";
            var input = JsonNode.Parse(KnowledgeAccessSchemas.Input(operation).GetRawText())!.AsObject();
            var properties = input["properties"]!.AsObject();
            properties.Remove("contribution"); properties.Remove("profile"); properties.Remove("exposure"); properties.Remove("exposureScope");
            var schema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["request"] = input },
                ["required"] = new JsonArray("request"),
                ["additionalProperties"] = false
            });
            var proposal = new KnowledgeToolProposal
            {
                Route = route,
                DefinitionName = source.Name + ".access." + route.Replace('_', '-'),
                Operation = operation,
                Contribution = selection,
                DisplayName = name + " · " + KnowledgeAccessSchemas.Route(operation),
                Description = string.IsNullOrWhiteSpace(businessDescription) ? name + ": " + help : name + ": " + businessDescription + "\n" + help,
                InputSchema = schema,
                OutputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                DataSchema = description?.Schema,
                ContentEvidence = currentSnapshot is not null ? "published_metadata" : description is null ? "not_checked" : "declared_json",
                Profile = profile,
                Issue = issue,
                ContractDigest = ""
            };
            proposal = proposal with { ContractDigest = Digest(proposal with { ContentEvidence = "not_checked", Issue = null }) };
            tools.Add(proposal);
        }
    }

    private static string ContributionRoute(KnowledgeContributionSelection selection)
    {
        var label = new string(selection.BindingName.Take(40).Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        return (label.Length == 0 ? "contribution" : label) + "-" + Digest(selection)[..8];
    }

    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
}
