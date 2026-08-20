using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
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
        using var context = new BunitContext();
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

    private sealed class FakeModelProfilesClient : IModelProfilesClient
    {
        public Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelProfileSummaryResponse>>([
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
