using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    public async Task OnlyLeftCardsSelectPreviewAndDraftChangesMarkItStale()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub());
        var owner = new ConsoleContextState(new TransformationContextProvider());
        await owner.EnsureLoadedAsync(default);
        context.Services.AddSingleton(owner);
        context.Services.AddSingleton<IConsoleNavigationContext>(new KnowledgeNavigationContext("/instance"));
        context.Services.AddSingleton<ITransformationFunctionsClient>(new FunctionClientStub());
        var authoring = new PreviewClient();
        context.Services.AddSingleton<ISourceAuthoringClient>(authoring);
        var operation = new ConnectedServiceOperationRevisionReference(new(ResourceScopeRef.Instance, ResourceNamespace.Default, "ConnectedServiceOperation", "operation"), Guid.NewGuid(), 1, "digest");
        var sample = new SourceSample("sample", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ResourceScopeRef.Instance,
            new(operation, JsonSerializer.SerializeToElement(new { })), SourceSampleState.Ready, DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
            JsonSerializer.SerializeToElement(new { a = 1, b = 2 }));
        IReadOnlyList<FlowCompositionCall> calls = [Call("/a"), Call("/b")];
        var rendered = context.Render<FlowSequenceEditor>(p => p.Add(c => c.Value, calls).Add(c => c.PreparedSample, sample)
            .Add(c => c.ValueChanged, next => calls = next));
        rendered.Find("[data-testid=source-preview-recalculate]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(1, authoring.Replays));
        rendered.FindAll("[data-testid=source-sequence-select]")[1].Click();
        var preview = rendered.Find("[data-testid=source-sequence-preview]");
        Assert.Contains("/b", preview.TextContent);
        Assert.IsEmpty(preview.QuerySelectorAll("select"));
        Assert.IsEmpty(preview.QuerySelectorAll("[data-testid=flow-sequence-up], [data-testid=flow-sequence-down]"));
        rendered.FindAll("[data-testid=flow-sequence-up]")[1].Click();
        rendered.Render(p => p.Add(c => c.Value, calls));
        Assert.AreEqual("/b", calls[0].Parameters.GetProperty("path").GetString());
        Assert.Contains("1", rendered.Find("[data-testid=source-sequence-preview] h3").TextContent);
        Assert.HasCount(1, rendered.FindAll("[data-testid=source-preview-stale]"));
        Assert.AreEqual(1, authoring.Replays);
        rendered.Find("[data-testid=source-preview-recalculate]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(2, authoring.Replays));
        Assert.HasCount(1, rendered.FindAll("[data-testid=source-sequence-preview] [data-testid=source-value-inspector]"));
        rendered.Render(p => p.Add(c => c.PreparedSample, sample with { Id = "expired", ExpiresAt = DateTimeOffset.MinValue }));
        Assert.IsEmpty(rendered.FindAll("[data-testid=source-sequence-preview] [data-testid=source-value-inspector]"));
        Assert.IsTrue(rendered.Find("[data-testid=source-preview-recalculate]").HasAttribute("disabled"));

        static FlowCompositionCall Call(string path) => new() { Function = new("select", "1.0.0", ResourceScopeRef.Instance), Parameters = JsonSerializer.SerializeToElement(new { path }) };
    }
    private sealed class PreviewClient : ISourceAuthoringClient
    {
        public int Replays { get; private set; }
        public Task<SourceSequenceAnalysis> AnalyzeAsync(SourceSequenceRequest request, CancellationToken ct) => Task.FromResult(new SourceSequenceAnalysis(request.Calls, []));
        public Task<SourceSequencePreview> ReplayAsync(string id, ReplaySourceSampleRequest request, CancellationToken ct)
        {
            Replays++;
            return Task.FromResult(new SourceSequencePreview("fingerprint", [JsonSerializer.SerializeToElement(new { }), .. request.Calls.Select(c => c.Parameters.GetProperty("path"))]));
        }
        public Task<SourceSample> AcquireAsync(AcquireSourceSampleRequest request, CancellationToken ct) => throw new AssertFailedException("Preview must not acquire data.");
        public Task<SourceSample> GetAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConsumedSampleCandidate>> CandidatesAsync(ResourceReference source, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceSample> PrepareConsumedAsync(PrepareConsumedSampleRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<Agentstration.Knowledge.Contracts.PreparedKnowledgePreview> PrepareKnowledgeAsync(Agentstration.Knowledge.Contracts.PrepareKnowledgePreviewRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
