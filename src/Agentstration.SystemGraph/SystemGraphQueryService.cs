using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.SystemGraph;

public sealed class SystemGraphException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Rebuilds and authorizes metadata; owns no family state or execution capability.</summary>
public sealed class SystemGraphQueryService(
    IEnumerable<ISystemGraphContributor> contributors,
    ISystemGraphAuthorization authorization,
    TimeProvider timeProvider,
    ISystemGraphReadScope? metadataScope = null) : ISystemGraphQueryService, ISystemGraphImpactService
{
    private const int MaximumNodes = 10000;
    private const int MaximumRelations = 20000;
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private readonly ISystemGraphContributor[] families = [.. contributors.OrderBy(value => value.Family, StringComparer.Ordinal)];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SystemGraphPage> QueryAsync(SystemGraphQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        try { return await QueryCoreAsync(query, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (SystemGraphException) { throw; }
        catch (Exception) { throw new SystemGraphException("system_graph_unavailable"); }
    }

    private async Task<SystemGraphPage> QueryCoreAsync(SystemGraphQuery query, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > 200 || query.Root is not null && query.Limit < 2 || query.Root?.Length > 512 || query.Cursor?.Length > 2048)
            throw new SystemGraphException("system_graph_query_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        var (currentContext, second) = await ObserveAsync(query, cancellationToken);
        var snapshot = second.Digest;
        var queryKey = Digest(JsonSerializer.Serialize(new { currentContext.AuthorizationKey, query.Namespace, query.Root, query.Limit }, JsonOptions));
        var offset = query.Cursor is null ? 0 : ReadCursor(query.Cursor, snapshot, queryKey);
        var rootNode = query.Root is { } rootId ? second.Nodes.FirstOrDefault(node => node.Id == rootId) : null;
        var candidates = rootNode is null ? second.Nodes : second.Nodes.Where(node => node.Id != rootNode.Id).ToArray();
        var pageNodes = candidates.Skip(offset).Take(query.Limit - (rootNode is null ? 0 : 1)).ToArray();
        var selected = rootNode is null ? pageNodes : new[] { rootNode }.Concat(pageNodes).ToArray();
        var ids = selected.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var relations = second.Relations.Where(edge => ids.Contains(edge.Source) && ids.Contains(edge.Target)).ToArray();
        var next = offset + pageNodes.Length;
        var hasMore = next < candidates.Length;
        var cursor = hasMore ? CreateCursor(new(snapshot, queryKey, next, timeProvider.GetUtcNow().AddMinutes(5))) : null;
        var scopeRefs = selected.Select(node => node.Identity.OwnerScope).ToHashSet();
        var hierarchy = (currentContext.Hierarchy ?? []).Where(scope => scopeRefs.Contains(scope.Scope))
            .Select(scope => scope with { Parent = scope.Parent is { } parent && scopeRefs.Contains(parent) ? parent : null })
            .OrderBy(scope => scope.Scope.Value, StringComparer.Ordinal).ToArray();
        return new(selected, relations, snapshot, timeProvider.GetUtcNow(), "family-contributors-authorized-slice", false, hasMore, cursor) { Scopes = hierarchy };
    }

    public async Task<SystemGraphImpactResult> AnalyzeAsync(SystemGraphImpactQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Root) || query.Root.Length > 512 || query.MaximumDepth is < 1 or > 32 || query.MaximumPaths is < 1 or > 1000)
            throw new SystemGraphException("system_graph_query_invalid");
        try
        {
            var (_, projection) = await ObserveAsync(new(query.Namespace), cancellationToken);
            return SystemGraphImpactAnalyzer.Analyze(query, projection.Nodes, projection.Relations, projection.Digest, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (SystemGraphException) { throw; }
        catch (Exception) { throw new SystemGraphException("system_graph_unavailable"); }
    }

    public async Task<IReadOnlyList<SystemGraphContributorCoverage>> GetCoverageAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await authorization.GetContextAsync(cancellationToken);
            return [.. families.Select(family => family.Coverage), new("entries", [], ["native-entry-identity-port-not-available"])];
        }
        catch (OperationCanceledException) { throw; }
        catch (SystemGraphException) { throw; }
        catch (Exception) { throw new SystemGraphException("system_graph_unavailable"); }
    }

    private async Task<(SystemGraphReadContext Context, Projection Projection)> ObserveAsync(SystemGraphQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = await authorization.GetContextAsync(ct);
        var first = await ReadAsync(context, query, ct);
        // Independent stores have no shared transaction. Validate the authorized read set again.
        var current = await authorization.GetContextAsync(ct);
        var second = await ReadAsync(current, query, ct);
        if (context.AuthorizationKey != current.AuthorizationKey || first.Digest != second.Digest)
            throw new SystemGraphException("system_graph_changed");
        return (current, second);
    }

    public async Task<SystemGraphChangeResult> CompareAsync(SystemGraphChangeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.From) || string.IsNullOrWhiteSpace(query.To) || query.From.Length > 512 || query.To.Length > 512
            || query.MaximumDepth is < 1 or > 32 || query.MaximumPaths is < 1 or > 1000)
            throw new SystemGraphException("system_graph_query_invalid");
        try
        {
            var (_, projection) = await ObserveAsync(new(query.Namespace), cancellationToken);
            var from = projection.Nodes.FirstOrDefault(node => node.Id == query.From);
            var to = projection.Nodes.FirstOrDefault(node => node.Id == query.To);
            var observedAt = timeProvider.GetUtcNow();
            var available = from is not null && to is not null;
            var before = SystemGraphImpactAnalyzer.Analyze(new(available ? query.From : "", query.Namespace, query.MaximumDepth, query.MaximumPaths),
                projection.Nodes, projection.Relations, projection.Digest, observedAt, cancellationToken);
            var after = SystemGraphImpactAnalyzer.Analyze(new(available ? query.To : "", query.Namespace, query.MaximumDepth, query.MaximumPaths),
                projection.Nodes, projection.Relations, projection.Digest, observedAt, cancellationToken);
            var assessment = !available ? "unavailable" : from!.Identity.ResourceUid != to!.Identity.ResourceUid ? "different-identities"
                : from.Identity.Kind == "DataSourceQuery" && from.State == "publication" && to.State == "publication"
                    && from.Evidence?.ContractHash is { } previousContract && to.Evidence?.ContractHash is { } currentContract
                    ? previousContract == currentContract ? "compatible-query-contract" : "query-contract-changed"
                : (from.State == "recorded-generation" || to.State == "recorded-generation") && from.Evidence?.Generation is { } previousGeneration && to.Evidence?.Generation is { } currentGeneration && previousGeneration != currentGeneration
                    ? "generation-drift"
                : from.Id == to.Id ? "same-observation" : "publication-compatibility-unknown";
            return new(assessment, before, after);
        }
        catch (OperationCanceledException) { throw; }
        catch (SystemGraphException) { throw; }
        catch (Exception) { throw new SystemGraphException("system_graph_unavailable"); }
    }

    private async Task<Projection> ReadAsync(SystemGraphReadContext context, SystemGraphQuery query, CancellationToken ct)
    {
        var nodes = new Dictionary<string, SystemGraphNode>(StringComparer.Ordinal);
        var access = new Dictionary<string, SystemGraphAccess>(StringComparer.Ordinal);
        var relations = new HashSet<SystemGraphRelation>();
        if (families.Length == 0 || families.Select(value => value.Family).Distinct(StringComparer.Ordinal).Count() != families.Length)
            throw new SystemGraphException("system_graph_unavailable");
        await foreach (var contribution in ReadSourcesAsync(context, ct))
        {
            var bindingsAllowed = true;
            if (contribution.BindingOwner is { } owner)
            {
                using (metadataScope?.Enter())
                {
                    var ownerAccess = await authorization.EvaluateAsync(owner, context, ct);
                    bindingsAllowed = ownerAccess.Discover && ownerAccess.InspectBindings;
                }
            }
            foreach (var node in contribution.Nodes)
            {
                if (!bindingsAllowed && node.Id != contribution.BindingOwnerNodeId) continue;
                if (query.Namespace is { } ns && node.Identity.Namespace != ns) continue;
                SystemGraphAccess allowed;
                using (metadataScope?.Enter())
                    allowed = await authorization.EvaluateAsync(node.Identity, context, ct);
                if (!allowed.Discover || (node.State.StartsWith("recorded-", StringComparison.Ordinal) || node.State == "reference-identity") && !allowed.InspectBindings) continue;
                var visible = allowed.InspectBindings ? node : node with { Evidence = null };
                if (nodes.TryGetValue(node.Id, out var existing) && existing != visible)
                    throw new SystemGraphException("system_graph_changed");
                nodes[node.Id] = visible;
                access[node.Id] = allowed;
                if (nodes.Count > MaximumNodes) throw new SystemGraphException("system_graph_limit_exceeded");
            }
        }
        // A second bounded source pass filters endpoints before retaining any relation.
        // Hidden endpoints therefore cannot consume the public relation budget.
        await foreach (var contribution in ReadSourcesAsync(context, ct))
        {
            if (contribution.BindingOwner is { } owner)
            {
                using (metadataScope?.Enter())
                {
                    var ownerAccess = await authorization.EvaluateAsync(owner, context, ct);
                    if (!ownerAccess.Discover || !ownerAccess.InspectBindings) continue;
                }
            }
            foreach (var edge in contribution.Relations)
                if (nodes.ContainsKey(edge.Source) && nodes.ContainsKey(edge.Target)
                    && (edge.Kind == "system-membership" || access[edge.Source].InspectBindings && access[edge.Target].InspectBindings))
                {
                    relations.Add(edge);
                    if (relations.Count > MaximumRelations) throw new SystemGraphException("system_graph_limit_exceeded");
                }
        }
        var visibleRelations = relations.ToArray();
        if (query.Root is { } root)
        {
            // Missing and invisible roots deliberately have the same result.
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (nodes.ContainsKey(root))
            {
                ids.Add(root);
                foreach (var edge in visibleRelations.Where(edge => edge.Source == root || edge.Target == root))
                { ids.Add(edge.Source); ids.Add(edge.Target); }
            }
            nodes = nodes.Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            visibleRelations = [.. visibleRelations.Where(edge => ids.Contains(edge.Source) && ids.Contains(edge.Target))];
        }
        var ordered = nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var edges = visibleRelations.OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal).ThenBy(edge => edge.Kind, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceField, StringComparer.Ordinal).ThenBy(edge => edge.Resolution, StringComparer.Ordinal)
            .ThenBy(edge => edge.Assessment, StringComparer.Ordinal).ThenBy(edge => edge.SelectedPublication, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetField, StringComparer.Ordinal).ThenBy(edge => edge.ExpectedHash, StringComparer.Ordinal)
            .ThenBy(edge => edge.ParentHash, StringComparer.Ordinal).ThenBy(edge => edge.BindingHash, StringComparer.Ordinal)
            .ThenBy(edge => edge.CapabilityHash, StringComparer.Ordinal).ToArray();
        return new(ordered, edges, Digest(JsonSerializer.Serialize(new { Nodes = ordered, Relations = edges }, JsonOptions)));
    }

    private async IAsyncEnumerable<SystemGraphContribution> ReadSourcesAsync(SystemGraphReadContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var family in families)
            foreach (var scope in context.Scopes.Distinct().OrderBy(value => value.Value, StringComparer.Ordinal))
            {
                await using var reader = family.ReadAsync(scope, ct).GetAsyncEnumerator(ct);
                while (true)
                {
                    bool next;
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        using var sourceScope = metadataScope?.Enter();
                        next = await reader.MoveNextAsync();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (SystemGraphException) { throw; }
                    catch (Exception)
                    {
                        // Never serialize contributor exceptions, identities or partial counts.
                        throw new SystemGraphException("system_graph_unavailable");
                    }
                    if (!next) break;
                    yield return reader.Current;
                }
            }
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private string CreateCursor(Cursor value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(cursorKey, bytes));
    }

    private int ReadCursor(string token, string snapshot, string queryKey)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(cursorKey, bytes))) throw new FormatException();
            var value = JsonSerializer.Deserialize<Cursor>(bytes, JsonOptions);
            if (value is null || value.Snapshot != snapshot || value.QueryKey != queryKey || value.Offset < 1
                || value.Offset > MaximumNodes || value.ExpiresAt <= timeProvider.GetUtcNow()) throw new FormatException();
            return value.Offset;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new SystemGraphException("system_graph_cursor_invalid"); }
    }

    private sealed record Cursor(string Snapshot, string QueryKey, int Offset, DateTimeOffset ExpiresAt);
    private sealed record Projection(SystemGraphNode[] Nodes, SystemGraphRelation[] Relations, string Digest);
}
