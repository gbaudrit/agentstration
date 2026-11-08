using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.Components.Values;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

using ConnectedServicesPage = Agentstration.Web.Components.Pages.ConnectedServices;
using ConnectedServiceDetailsPage = Agentstration.Web.Components.Pages.ConnectedServiceDetails;
using ConnectedServiceEditorPage = Agentstration.Web.Components.Pages.ConnectedServiceEditor;

[TestClass]
public sealed class ConnectedServiceComponentTests
{
    [TestMethod]
    public void CatalogRendersTypeAvailabilityAndExactScope()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new StubClient(Resource(scope)));

        var rendered = context.Render<ConnectedServicesPage>();

        rendered.WaitForAssertion(() =>
        {
            var row = rendered.Find("[data-testid='connected-service-row']");
            StringAssert.Contains(row.TextContent, "Redis");
            StringAssert.Contains(row.TextContent, "Ready");
            var nameLink = rendered.Find("[data-testid='connected-service-name-link']");
            Assert.AreEqual("resource-name-link", nameLink.ClassName);
            StringAssert.Contains(nameLink.GetAttribute("href")!, $"scopeRef={Uri.EscapeDataString(scope.Value)}");
            Assert.DoesNotContain("Inspect", row.TextContent, StringComparison.OrdinalIgnoreCase);
        });
    }

    [TestMethod]
    public void EditorUsesSharedBindingsAndOnlyOffersCompatibleUsableConfiguredResources()
    {
        using var context = new BunitContext();
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new EditorClient(TypeCatalog(), workspace));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub(
        [
            Parameter("workspace-endpoint", "Workspace endpoint", workspace, ParameterValueType.Text),
            Parameter("workspace-retries", "Workspace retries", workspace, ParameterValueType.WholeNumber),
            Parameter("granted-endpoint", "Granted endpoint", tenant, ParameterValueType.Text,
                new() { Grants = [new(workspace)] }),
            Parameter("wrong-type", "Wrong type", workspace, ParameterValueType.Logical),
            Parameter("not-granted", "Not granted", tenant, ParameterValueType.Text)
        ]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub(
        [
            Secret("configured-secret", "Configured secret", workspace, true),
            Secret("granted-secret", "Granted secret", tenant, true, new() { Grants = [new(workspace)] }),
            Secret("empty-secret", "Empty secret", workspace, false),
            Secret("not-granted-secret", "Not granted secret", tenant, true)
        ]));

        var rendered = context.Render<ConnectedServiceEditorPage>();

        rendered.WaitForAssertion(() =>
        {
            var editors = rendered.FindComponents<ValueBindingRequirementEditor>();
            Assert.HasCount(3, editors);

            var endpoint = editors.Single(value => value.Instance.Requirement.Id == "endpoint").Instance;
            CollectionAssert.AreEqual(
                new[] { ValueBindingSourceKind.Literal, ValueBindingSourceKind.Parameter, ValueBindingSourceKind.Secret },
                endpoint.AllowedSources.ToArray());
            Assert.AreEqual(ValueBindingSourceKind.Literal, endpoint.Selection.Kind);
            CollectionAssert.AreEquivalent(
                new[] { "workspace-endpoint", "granted-endpoint", "configured-secret", "granted-secret" },
                endpoint.Options.Select(value => value.Address.Name).ToArray());

            var retries = editors.Single(value => value.Instance.Requirement.Id == "retries").Instance;
            CollectionAssert.AreEqual(new[] { ValueBindingSourceKind.Literal, ValueBindingSourceKind.Parameter }, retries.AllowedSources.ToArray());
            CollectionAssert.AreEqual(new[] { "workspace-retries" }, retries.Options.Select(value => value.Address.Name).ToArray());

            var credential = editors.Single(value => value.Instance.Requirement.Id == "credential").Instance;
            CollectionAssert.AreEqual(new[] { ValueBindingSourceKind.Secret }, credential.AllowedSources.ToArray());
            CollectionAssert.AreEquivalent(new[] { "configured-secret", "granted-secret" }, credential.Options.Select(value => value.Address.Name).ToArray());
            Assert.IsNotNull(credential.CreationContext);
            Assert.AreEqual(workspace, credential.CreationContext.ScopeRef);
        });
    }

    [TestMethod]
    public void DetailsUseResourceTabsEmbedDefinitionAndDoNotExposeAnEditAction()
    {
        using var context = new BunitContext();
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var type = TypeCatalog();
        var resource = Resource(workspace, type);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new DetailsClient(resource, type, workspace));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/connected-services/{resource.Name}?scopeRef={Uri.EscapeDataString(workspace.Value)}");

        var rendered = context.Render<ConnectedServiceDetailsPage>(parameters => parameters
            .Add(component => component.Name, resource.Name));

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(4, rendered.FindAll(".resource-tabs button"));
            Assert.IsEmpty(rendered.FindAll("a[href*='/edit']"));
            Assert.DoesNotContain("Saved configuration and observed availability", rendered.Markup,
                StringComparison.Ordinal);
        });

        rendered.Find("[data-testid='connected-service-definition-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='connected-service-form']"));
            Assert.IsEmpty(rendered.FindAll(".form-alert-danger"));
            Assert.HasCount(3, rendered.FindComponents<ValueBindingRequirementEditor>());
        });

        rendered.Find("[data-testid='connected-service-yaml-tab']").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(
            rendered.Find("[data-testid='connected-service-yaml-viewer'] textarea").TextContent,
            "apiVersion"));
    }

    private static ConnectedServiceTypeResource TypeCatalog() => new()
    {
        Uid = Guid.NewGuid(),
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
        Metadata = new() { Name = "http" },
        ScopeRef = ResourceScopeRef.Instance,
        Definition = new()
        {
            DisplayName = "HTTP",
            PreferredVersion = "1.0.0",
            Versions =
            [
                new("1.0.0", "sha256:http", JsonSerializer.SerializeToElement(new { type = "object" }),
                [
                    new("endpoint", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard, "uri"),
                    new("retries", false, ConnectedServiceValueType.WholeNumber, ConnectedServiceValueProtection.Standard),
                    new("credential", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)
                ], [])
            ]
        }
    };

    private static ParameterResource Parameter(string name, string displayName, ResourceScopeRef scope,
        ParameterValueType type, DescendantUsePolicy? usePolicy = null) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ParameterResourceKinds.Parameter,
        Metadata = new() { Name = name },
        ScopeRef = scope,
        Definition = new()
        {
            DisplayName = displayName,
            ValueType = type,
            Value = type == ParameterValueType.WholeNumber
                ? JsonSerializer.SerializeToElement(3)
                : type == ParameterValueType.Logical
                    ? JsonSerializer.SerializeToElement(true)
                    : JsonSerializer.SerializeToElement("value"),
            UsePolicy = usePolicy ?? new()
        }
    };

    private static SecretResponse Secret(string name, string displayName, ResourceScopeRef scope, bool configured,
        DescendantUsePolicy? usePolicy = null) =>
        new(new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = name },
            ScopeRef = scope,
            Definition = new()
            {
                DisplayName = displayName,
                Vault = new ResourceReference("vault", scope),
                Key = name,
                UsePolicy = usePolicy ?? new()
            }
        }, configured ? "Configured" : "Missing", configured);

    private static ConnectedServiceResource Resource(ResourceScopeRef scope,
        ConnectedServiceTypeResource? type = null) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedService,
        Metadata = new() { Name = "redis" },
        ScopeRef = scope,
        Definition = new()
        {
            DisplayName = "Redis",
            Type = type is null
                ? new() { Type = new ResourceReference("redis", ResourceScopeRef.Instance, new("azure")), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "sha256:contract" }
                : new() { Type = new ResourceReference(type.Name, type.ScopeRef, type.Namespace), TypeUid = type.Uid, Version = "1.0.0", SchemaDigest = "sha256:http" },
            Options = JsonSerializer.SerializeToElement(new { })
        },
        ObservedState = new() { Availability = ConnectedServiceAvailability.Ready, ObservedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
    };

    private sealed class StubClient(ConnectedServiceResource resource) : IConnectedServicesClient
    {
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectedServiceResource>>([resource]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EditorClient(ConnectedServiceTypeResource type, ResourceScopeRef workspace) : IConnectedServicesClient
    {
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectedServiceTypeResource>>([type]);
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(workspace, workspace.Kind, "Workspace", true)]);
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DetailsClient(ConnectedServiceResource resource, ConnectedServiceTypeResource type,
        ResourceScopeRef workspace) : IConnectedServicesClient
    {
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectedServiceTypeResource>>([type]);
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(workspace, workspace.Kind, "Workspace", true)]);
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => Task.FromResult(new ResourceSnapshot<ConnectedServiceResource>(resource, "\"etag\""));
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => Task.FromResult(new ConnectedServiceUsagesResponse([], 0));
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ParametersClientStub(IReadOnlyList<ParameterResource> resources) : IParametersClient
    {
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken cancellationToken) => Task.FromResult(resources);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef, PutParameterRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SecretsClientStub(IReadOnlyList<SecretResponse> resources) : ISecretsClient
    {
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken cancellationToken) => Task.FromResult(resources);
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSecretValueAsync(string name, string value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretValueAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
