using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.ModelProfiles;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ModelProfilePickerTests
{
    [TestMethod]
    public void NewAgentSelectionReplacesAMissingDefaultWithTheDisplayedProfile()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        string? selectedName = null;
        ModelProfileSummaryResponse? selectedProfile = null;

        var rendered = context.Render<ModelProfilePicker>(parameters => parameters
            .Add(component => component.Value, "reasoning-default")
            .Add(component => component.Namespace, "default")
            .Add(component => component.SelectFirstWhenMissing, true)
            .Add(component => component.ValueChanged, value => selectedName = value)
            .Add(component => component.SelectedChanged, value => selectedProfile = value));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("default-reasoning", selectedName);
            Assert.AreEqual("default", selectedProfile?.Namespace);
            Assert.AreEqual("default-reasoning", selectedProfile?.Name);
            StringAssert.Contains(rendered.Markup, "default/default-reasoning");
        });
    }

    [TestMethod]
    public void HomonymousProfilesDisplayAndSelectTheirExactScope()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var workspaceScope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient([
            Summary("shared-reasoning", "Instance reasoning", ResourceScopeRef.Instance),
            Summary("shared-reasoning", "Workspace reasoning", workspaceScope)
        ]));
        ModelProfileSummaryResponse? selectedProfile = null;

        var rendered = context.Render<ModelProfilePicker>(parameters => parameters
            .Add(component => component.Value, "shared-reasoning")
            .Add(component => component.Namespace, "default")
            .Add(component => component.ScopeRef, ResourceScopeRef.Instance)
            .Add(component => component.SelectedChanged, value => selectedProfile = value));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(ResourceScopeRef.Instance, selectedProfile?.ScopeRef);
            StringAssert.Contains(rendered.Markup, "Instance reasoning — Instance");
            StringAssert.Contains(rendered.Markup, "Workspace reasoning — Workspace");
            StringAssert.Contains(rendered.Markup, "resource-scope-badge");
        });

        rendered.Find("[data-testid='model-profile-select']")
            .Change($"{workspaceScope.Value}|default|shared-reasoning");

        Assert.AreEqual(workspaceScope, selectedProfile?.ScopeRef);
    }

    [TestMethod]
    public void AgentRequestPinsSelectedModelAndRuntimeProfileScopes()
    {
        var workspaceScope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var model = new AgentEditorModel
        {
            Name = "scoped-agent",
            DisplayName = "Scoped agent",
            Instructions = "Use the exact profiles.",
            RuntimeProfileName = "maf-builtin",
            RuntimeProfileNamespace = "default",
            RuntimeProfileScopeRef = ResourceScopeRef.Instance
        };

        Assert.IsTrue(model.SelectModelProfile(Summary("reasoning", "Workspace reasoning", workspaceScope)));

        var request = model.ToRequest();
        Assert.AreEqual(workspaceScope, request.Definition.ModelProfile.ScopeRef);
        Assert.AreEqual(ResourceScopeRef.Instance, request.Definition.RuntimeProfile.ScopeRef);
    }

    private static ModelProfileSummaryResponse Summary(string name, string displayName, ResourceScopeRef scopeRef) =>
        new(
            name,
            name,
            new ModelProfileSummaryPropertiesResponse(
                displayName,
                null,
                new ModelProviderReferenceResponse("ollama-local", "ollama-local", "Ollama local"),
                new ModelReferenceResponse("qwen3:1.7b"),
                new ModelGenerationOptions(),
                new ModelReasoningOptions(),
                new ModelOutputOptions(),
                "available",
                0),
            ScopeRef: scopeRef);

    private sealed class FakeModelProfilesClient(IReadOnlyList<ModelProfileSummaryResponse>? profiles = null) : IModelProfilesClient
    {
        public Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken) =>
            Task.FromResult(profiles ?? (IReadOnlyList<ModelProfileSummaryResponse>)[
                new(
                    "default-reasoning",
                    "default-reasoning",
                    new ModelProfileSummaryPropertiesResponse(
                        "Default reasoning",
                        null,
                        new ModelProviderReferenceResponse("ollama-local", "ollama-local", "Ollama local"),
                        new ModelReferenceResponse("qwen3:1.7b"),
                        new ModelGenerationOptions(),
                        new ModelReasoningOptions(),
                        new ModelOutputOptions(),
                        "available",
                        0))
            ]);

        public Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>> PreviewOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> ApplyOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
