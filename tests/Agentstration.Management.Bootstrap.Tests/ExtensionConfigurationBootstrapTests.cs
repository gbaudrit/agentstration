using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task OperationalConfigurationUsesPublicSelectorAndNeverWritesDuringPreview()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "profile.yaml"), Profile(Path.GetFileName(directory.Path), "instance"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "10-options.yaml"), """
            apiVersion: agentstration.io/v1
            kind: ExtensionConfiguration
            metadata:
              name: initial-options
            definition:
              extension:
                id: extension.public
              optionSet: fixture/options
              version: 1.0.0
              bindings:
                - requirementId: AllowedDomains
                  kind: literal
                  literal: [example.invalid]
            """);
        var configurations = new RecordingConfigurations();
        var handler = new ExtensionConfigurationBootstrapHandler(configurations);
        var service = ExtensionService(directory.Path, handler, new RecordingExtensionResolver());
        var selection = new BootstrapProfileSelection([Path.GetFileName(directory.Path)]);
        var preview = await service.PreviewAsync(selection, default);
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual("actual-local-registration", configurations.RegistrationName);
        Assert.AreEqual(0, configurations.Writes);
        var result = await service.ExecuteAsync(selection, default);
        Assert.IsNull(result.Error);
        Assert.AreEqual(1, configurations.Writes);
        Assert.AreEqual("initial-options", configurations.Saved!.Name);
        var replay = await service.ExecuteAsync(selection, default);
        Assert.IsNull(replay.Error);
        Assert.AreEqual(1, configurations.Writes);
        Assert.IsFalse(handler.SupportsProfileScope(BootstrapProfileScope.Tenant));
        Assert.IsFalse(handler.SupportsProfileScope(BootstrapProfileScope.Workspace));
    }

    private sealed class RecordingConfigurations : IExtensionConfigurationService
    {
        public string? RegistrationName { get; private set; }
        public int Writes { get; private set; }
        public PutExtensionConfigurationRequest? Saved { get; private set; }
        private ExtensionConfigurationResource? resource;
        private static readonly ExtensionConfigurationContract Contract = new("fixture/options", "1.0.0", "sha256:fixture",
            [new("AllowedDomains", JsonSerializer.SerializeToElement(new { type = "array" }), true, false,
                ["Literal", "Parameter"], "Process", "Immediate", null)]);
        public Task<ExtensionConfigurationView> GetAsync(ResourceNamespace resourceNamespace, string registrationName, CancellationToken cancellationToken)
        {
            RegistrationName = registrationName;
            return Task.FromResult(new ExtensionConfigurationView(true, Contract, resource, resource is null ? null : "\"etag\"", true,
                new Dictionary<string, JsonElement>()));
        }
        public Task<ExtensionConfigurationView> PutAsync(ResourceNamespace resourceNamespace, string registrationName,
            PutExtensionConfigurationRequest request, string? ifMatch, CancellationToken cancellationToken)
        {
            Writes++; Saved = request;
            resource = new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionConfiguration,
                ScopeRef = ResourceScopeRef.Instance,
                Metadata = new() { Name = request.Name! },
                Definition = new(Guid.NewGuid(), "extension.public", request.OptionSet, request.Version,
                    request.SchemaDigest, request.Bindings),
                Observation = new(1, "active", "applied")
            };
            return GetAsync(resourceNamespace, registrationName, cancellationToken);
        }
        public Task<ExtensionConfigurationView> ApplyAsync(ResourceNamespace resourceNamespace, string registrationName, string ifMatch, CancellationToken cancellationToken) =>
            GetAsync(resourceNamespace, registrationName, cancellationToken);
    }
}
