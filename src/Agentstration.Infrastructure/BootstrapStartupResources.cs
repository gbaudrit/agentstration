using System.Security.Cryptography;
using System.Text;
using Agentstration.Bootstrap.Contracts;

namespace Agentstration.Infrastructure;

public sealed partial class BootstrapStartupState
{
    // The existing catalog bounds permit 17 applications of 16 profiles, each
    // containing at most 512 documents. No manifest or secret value is retained.
    private const int MaximumResources = 2 * 17 * 16 * 512;
    private readonly Dictionary<string, BootstrapStartupResource> resourceRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Kind, string GroupId)> lastGroupByStep = new(StringComparer.Ordinal);
    private string? activeResourceId;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public string RegisterResource(string targetLevel, string profile, string location, string stepId,
        string kind, string name, string resourceNamespace = "default")
    {
        lock (sync)
        {
            var id = ResourceId(targetLevel, profile, location);
            if (resourceRows.ContainsKey(id)) return id;
            if (resourceRows.Count >= MaximumResources)
                throw new InvalidOperationException("The Bootstrap observation limit was exceeded.");
            var groupId = lastGroupByStep.TryGetValue(stepId, out var previous) && previous.Kind == kind ? previous.GroupId : id;
            lastGroupByStep[stepId] = (kind, groupId);
            resourceRows.Add(id, new(id, stepId, profile, location, kind, name,
                resourceNamespace, targetLevel, "pending", [])
            {
                GroupId = groupId
            });
            revision++;
            return id;
        }
    }

    internal static string ResourceId(string targetLevel, string profile, string location) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{targetLevel}\n{profile}\n{location}")));

    internal BootstrapStartupResource? FindResource(Guid attempt, string id)
    {
        lock (sync) return attemptId == attempt && resourceRows.TryGetValue(id, out var row) ? row : null;
    }

    internal void ObserveSubstitution(string id, BootstrapStartupModelSubstitution change)
    {
        lock (sync)
        {
            if (!resourceRows.TryGetValue(id, out var row)) return;
            resourceRows[id] = row with { Substitution = change };
            ObserveOperationCore(id, "model_substitution", "succeeded");
        }
    }

    public string StartResource(string targetLevel, string profile, string location, string kind, string name, string resourceNamespace)
    {
        lock (sync)
        {
            var id = ResourceId(targetLevel, profile, location);
            if (!resourceRows.TryGetValue(id, out var row)) throw new InvalidOperationException("The resource was not in the startup plan.");
            activeResourceId = id;
            resourceRows[id] = row with { Kind = kind, Name = name, Namespace = resourceNamespace, State = "running" };
            ObserveOperationCore(id, "resource_apply", "running");
            return id;
        }
    }

    public void ObserveOperation(string id, string code, string state)
    {
        lock (sync) ObserveOperationCore(id, code, state);
    }

    private void ObserveOperationCore(string? id, string code, string state)
    {
        if (id is null || !resourceRows.TryGetValue(id, out var row)) return;
        if (row.Operations.Any(operation => operation.Code == code && operation.State == state)) return;
        var operations = row.Operations.Where(operation => operation.Code != code).ToList();
        // Operation codes are emitted by handlers, never arbitrary exception text.
        if (operations.Count >= 16) return;
        operations.Add(new(code, state, clock.GetUtcNow()));
        resourceRows[id] = row with
        {
            State = state == "waiting" ? "waiting" : row.State,
            Operations = operations.ToArray()
        };
        revision++;
    }

    public void FinishResource(string id, string outcome)
    {
        lock (sync)
        {
            if (resourceRows.TryGetValue(id, out var completed) && completed.State == outcome) return;
            ObserveOperationCore(id, "resource_apply", outcome);
            if (resourceRows.TryGetValue(id, out var row))
            {
                resourceRows[id] = row with { State = outcome };
                if (outcome == "failed" && activity?.ResourceId != id)
                    activity = new(row.Kind, row.Name, ResourceId: id, TargetLevel: row.TargetLevel, GroupId: row.GroupId);
            }
            if (activeResourceId == id) activeResourceId = null;
            revision++;
        }
    }

    private string ObservedState(BootstrapStartupResource row) =>
        failure is not null && row.State == "pending" ? "blocked" : row.State;

    private BootstrapStartupFamily[] ReadFamilies() => resourceRows.Values
        .GroupBy(row => (row.StepId, row.Kind, row.GroupId))
        .Select(group =>
        {
            var rows = group.ToArray();
            var failedCount = rows.Count(row => row.State == "failed");
            var conflictCount = rows.Count(row => row.State == "conflict");
            var processed = rows.Count(row => row.State is "created" or "skipped" or "succeeded" or "failed" or "conflict");
            var waiting = rows.Count(row => row.State == "waiting");
            var state = failedCount + conflictCount > 0 ? "failed"
                : waiting > 0 ? "waiting" : rows.Any(row => row.State == "running") ? "running"
                : processed == rows.Length ? "succeeded" : failure is not null ? "blocked" : "pending";
            return new BootstrapStartupFamily(group.Key.StepId, group.Key.Kind, state, rows.Length, processed,
                rows.Count(row => row.State == "created"), rows.Count(row => row.State == "skipped"),
                failedCount, conflictCount, waiting)
            {
                GroupId = group.Key.GroupId,
                Profile = rows.Select(row => row.Profile).Distinct(StringComparer.Ordinal).Take(2).ToArray() is [var profile] ? profile : null
            };
        }).ToArray();

    public BootstrapStartupResourcePage? ReadResources(BootstrapStartupResourceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        if (query.Limit is < 1 or > 100 || query.Search?.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(query));
        lock (sync)
        {
            if (query.AttemptId != attemptId) return null;
            var matches = resourceRows.Values.Where(row =>
                (query.StepId is null || row.StepId == query.StepId)
                && (query.Kind is null || row.Kind == query.Kind)
                && (query.GroupId is null || row.GroupId == query.GroupId)
                && (query.State is null || ObservedState(row) == query.State)
                && (string.IsNullOrWhiteSpace(query.Search)
                    || row.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                    || row.Profile.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                    || row.TargetLevel.Contains(query.Search, StringComparison.OrdinalIgnoreCase)));
            var items = matches.Skip(query.Offset).Take(query.Limit)
                .Select(row => row with { State = ObservedState(row) }).ToArray();
            return new(attemptId, revision, matches.Count(), query.Offset, query.Limit, items);
        }
    }
}
