using System.Text.Json;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ContextualValueCreatorTests
{
    [TestMethod]
    public void ParameterCreatorWorksWithoutAModelProviderConsumer()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new ParameterClient(scope);
        ScopedResourceAddress? created = null;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IParametersClient>(client);

        var rendered = context.Render<ContextualParameterCreator>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.Context, new(scope, "retry-count", "Retry count", ValueType: ParameterValueType.WholeNumber, Format: "int64"))
            .Add(component => component.OnCreated, address => created = address));

        rendered.Find("[data-testid='contextual-parameter-value']").Change("12");
        rendered.Find("form").Submit();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(ParameterValueType.WholeNumber, client.Request?.Properties.ValueType);
            Assert.AreEqual(12, client.Request?.Properties.Value.GetInt32());
            Assert.AreEqual(new ScopedResourceAddress(scope, ResourceNamespace.Default, ParameterResourceKinds.Parameter, "retry-count"), created);
            StringAssert.Contains(rendered.Markup, "Format attendu : int64");
        });
    }

    [TestMethod]
    public void SecretCreatorWritesValueOnceAndReturnsExactReference()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new SecretClient(scope);
        ScopedResourceAddress? created = null;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);

        var rendered = context.Render<ContextualSecretCreator>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.Context, new(scope, "api-key", "API key"))
            .Add(component => component.OnCreated, address => created = address));

        rendered.WaitForElement("option[value*='local-vault']");
        rendered.Find("[data-testid='contextual-secret-value']").Change("do-not-render-this-value");
        rendered.Find("form").Submit();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("do-not-render-this-value", client.WrittenValue);
            Assert.AreEqual(new ScopedResourceAddress(scope, ResourceNamespace.Default, SecretResourceKinds.Secret, "api-key"), created);
            Assert.IsFalse(rendered.Markup.Contains("do-not-render-this-value", StringComparison.Ordinal));
            Assert.AreEqual(scope, client.Request?.ScopeRef);
        });
    }

    private sealed class ParameterClient(ResourceScopeRef scope) : IParametersClient
    {
        public CreateParameterRequest? Request { get; private set; }
        public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var resource = new ParameterResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ParameterResourceKinds.Parameter,
                Metadata = new() { Name = request.Name },
                ScopeRef = scope,
                Definition = request.Properties
            };
            return Task.FromResult(new ResourceSnapshot<ParameterResource>(resource, "\"parameter-etag\""));
        }
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef, PutParameterRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SecretClient(ResourceScopeRef scope) : ISecretsClient
    {
        public CreateSecretRequest? Request { get; private set; }
        public string? WrittenValue { get; private set; }
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VaultResponse>>([new(Vault(), "available")]);
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var resource = new SecretResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                Metadata = new() { Name = request.Name },
                ScopeRef = scope,
                Definition = request.Properties
            };
            return Task.FromResult(new ResourceSnapshot<SecretResource>(resource, "\"secret-etag\""));
        }
        public Task SetSecretValueAsync(string name, ResourceScopeRef scopeRef, string value, CancellationToken cancellationToken) { WrittenValue = value; return Task.CompletedTask; }
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSecretValueAsync(string name, string value, CancellationToken cancellationToken) => SetSecretValueAsync(name, scope, value, cancellationToken);
        public Task DeleteSecretValueAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        private VaultResource Vault() => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "local-vault" },
            ScopeRef = scope,
            Definition = new VaultProperties { DisplayName = "Local vault", ProviderType = "local", ProviderOptions = new Dictionary<string, JsonElement>() }
        };
    }
}
