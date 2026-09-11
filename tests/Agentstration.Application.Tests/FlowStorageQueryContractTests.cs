using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task SqliteFlowRunPaginationFiltersAndBoundsEveryPageInSql()
    {
        var commands = new FlowCommandInterceptor();
        var clock = new AdvancingTimeProvider(Now);
        await using var fixture = await FlowFixture.CreateAsync(commands, clock);
        var firstFlow = new FlowId("first-flow");
        var secondFlow = new FlowId("second-flow", new ResourceNamespace("team-a"));
        var runs = new List<FlowRun>();
        for (var index = 0; index < 421; index++)
        {
            var flowId = index % 2 == 0 ? firstFlow : secondFlow;
            var status = (FlowRunStatus)(index % Enum.GetValues<FlowRunStatus>().Length);
            var run = CreateStoredRun($"run-{index:D4}", flowId, status);
            runs.Add(run);
            await fixture.Repository.CreateRunAsync(run, default);
        }
        var changed = runs[0] with { Status = FlowRunStatus.Failed };
        var stored = await fixture.Repository.GetRunAsync(TestScope, changed.Id, default);
        await fixture.Repository.UpdateRunAsync(changed, stored!.ETag, default);
        runs[0] = changed;

        commands.Reset();
        var actual = await ReadAllAsync(firstFlow, FlowRunStatus.Succeeded, 23);
        var expected = runs.Where(value => value.FlowId == firstFlow && value.Status == FlowRunStatus.Succeeded)
            .OrderByDescending(value => value.Id, StringComparer.Ordinal)
            .Select(value => value.Id)
            .ToArray();

        CollectionAssert.AreEqual(expected, actual.Select(value => value.Value.Id).ToArray());
        Assert.AreEqual(actual.Count, actual.Select(value => value.Value.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("\"Status\" =", StringComparison.Ordinal)));
        Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("\"Namespace\" =", StringComparison.Ordinal)));
        Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("\"FlowId\" =", StringComparison.Ordinal)));
        AssertQueriesAreScoped();

        commands.Reset();
        var failed = await ReadAllAsync(null, FlowRunStatus.Failed, 31);
        CollectionAssert.AreEqual(
            runs.Where(value => value.Status == FlowRunStatus.Failed).OrderByDescending(value => value.Id, StringComparer.Ordinal).Select(value => value.Id).ToArray(),
            failed.Select(value => value.Value.Id).ToArray());
        Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("\"Status\" =", StringComparison.Ordinal)));
        AssertQueriesAreScoped();

        commands.Reset();
        var all = await ReadAllAsync(null, null, 37);
        CollectionAssert.AreEqual(
            runs.OrderByDescending(value => value.Id, StringComparer.Ordinal).Select(value => value.Id).ToArray(),
            all.Select(value => value.Value.Id).ToArray());
        Assert.AreEqual(runs.Count, all.Select(value => value.Value.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)));
        AssertQueriesAreScoped();

        void AssertQueriesAreScoped()
        {
            Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("\"TenantId\" =", StringComparison.Ordinal)));
            Assert.IsTrue(commands.CommandTexts.All(value => value.Contains("\"PrincipalId\" =", StringComparison.Ordinal)));
        }

        async Task<IReadOnlyList<Agentstration.Flow.Storage.Abstractions.StoredFlowRun>> ReadAllAsync(
            FlowId? flowId,
            FlowRunStatus? status,
            int take)
        {
            var items = new List<Agentstration.Flow.Storage.Abstractions.StoredFlowRun>();
            for (var skip = 0; ; skip += take)
            {
                var page = await fixture.Repository.ListRunsAsync(TestScope, flowId, status, skip, take, default);
                items.AddRange(page.Items);
                if (!page.HasMore) return items;
            }
        }
    }

    private static FlowRun CreateStoredRun(string id, FlowId flowId, FlowRunStatus status)
    {
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));
        var version = new FlowVersion(TestScope.WorkspaceId, flowId, "1.0.0", null, definition, new Dictionary<string, string>(), Now);
        return new FlowRun
        {
            WorkspaceId = TestScope.WorkspaceId,
            Id = id,
            FlowId = flowId,
            FlowVersion = version.Version,
            Status = status,
            Trigger = FlowRunTrigger.Manual,
            Scope = TestScope,
            Input = JsonSerializer.SerializeToElement(new { prompt = id }),
            CreatedAt = Now,
            DefinitionSnapshot = version
        };
    }

    private sealed class FlowCommandInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commandTexts = new();
        public IReadOnlyList<string> CommandTexts => _commandTexts.ToArray();
        public void Reset() => _commandTexts.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
