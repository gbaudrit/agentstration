using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class VaultGrantPersistenceTests
{
    [TestMethod]
    public void NewSecretLoadsVaultsForItsSelectedConsumerScope()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);

        _ = context.Render<SecretEditor>();

        Assert.AreEqual(workspace, client.LastVaultConsumerScope);
    }

    [TestMethod]
    public void GrantIsSentBySaveAndShownWhenEditorIsReopened()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");

        var editor = context.Render<VaultEditor>(parameters => parameters
            .Add(component => component.Name, "shared"));
        Assert.AreEqual(0, editor.FindAll(".unsaved-changes-bar").Count);
        editor.WaitForElement(".grant-add select").Change(workspace.Value);
        Assert.AreEqual(0, editor.FindAll(".unsaved-changes-bar").Count);
        editor.Find(".grant-add-button").Click();
        Assert.AreEqual(0, editor.FindAll(".grant-save").Count);
        Assert.AreEqual("submit", editor.Find(".unsaved-changes-bar .button-primary").GetAttribute("type"));
        editor.Find("form").Submit();

        editor.WaitForAssertion(() => Assert.AreEqual(workspace, client.Saved.Definition.UsePolicy.Grants.Single().ScopeRef));
        Assert.AreEqual(0, editor.FindAll(".unsaved-changes-bar").Count);
        var reopened = context.Render<VaultEditor>(parameters => parameters
            .Add(component => component.Name, "shared"));
        reopened.WaitForAssertion(() => Assert.AreEqual("Default", reopened.Find(".grant-target-identity .disclosure-trigger").TextContent));
    }

    [TestMethod]
    public void DiscardRestoresPersistedGrantsAndHidesBar()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");

        var editor = context.Render<VaultEditor>(parameters => parameters.Add(component => component.Name, "shared"));
        editor.WaitForElement(".grant-add select").Change(workspace.Value);
        editor.Find(".grant-add-button").Click();
        editor.Find(".unsaved-changes-bar .button-secondary").Click();

        editor.WaitForAssertion(() => Assert.AreEqual(0, editor.FindAll(".grant-row").Count));
        Assert.AreEqual(0, editor.FindAll(".unsaved-changes-bar").Count);
        Assert.AreEqual(0, client.Saved.Definition.UsePolicy.Grants.Count);
    }

    [TestMethod]
    public void SecretGrantShowsTheSameBarAndPersistsOnSave()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");

        var editor = context.Render<SecretEditor>(parameters => parameters.Add(component => component.Name, "credential"));
        editor.WaitForElement(".grant-add select").Change(workspace.Value);
        editor.Find(".grant-add-button").Click();
        Assert.AreEqual("submit", editor.Find(".unsaved-changes-bar .button-primary").GetAttribute("type"));
        editor.Find("form").Submit();

        editor.WaitForAssertion(() => Assert.AreEqual(workspace, client.SavedSecret.Definition.UsePolicy.Grants.Single().ScopeRef));
        Assert.AreEqual(0, editor.FindAll(".unsaved-changes-bar").Count);
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void ClearingSecretKeyMarksMetadataDirtyAndRequiredValidationPreventsSave(string locale)
    {
        using var culture = new TestCultureScope(locale);
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid()));
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        var editor = context.Render<SecretEditor>(parameters => parameters.Add(component => component.Name, "credential"));
        var input = editor.WaitForElement("[data-testid='secret-vault-key']");
        var field = input.Closest(".form-field")!;
        Assert.AreEqual(input.Id, field.QuerySelector("label")!.GetAttribute("for"));
        Assert.IsTrue(input.GetAttribute("aria-describedby")!.Contains("-help", StringComparison.Ordinal));
        field.QuerySelector("button.field-clear")!.Click();

        Assert.AreEqual(string.Empty, editor.Find("[data-testid='secret-vault-key']").GetAttribute("value"));
        Assert.HasCount(1, editor.FindAll(".unsaved-changes-bar"));
        editor.Find("form").Submit();

        Assert.AreEqual(0, client.SecretMetadataSaves);
        Assert.AreEqual("credential", client.SavedSecret.Definition.Key);
        Assert.HasCount(1, editor.FindAll(".validation-errors li"));
        editor.Find(".unsaved-changes-bar .button-secondary").Click();
        editor.WaitForAssertion(() => Assert.AreEqual("credential", editor.Find("[data-testid='secret-vault-key']").GetAttribute("value")));
        Assert.IsEmpty(editor.FindAll(".unsaved-changes-bar"));
        Assert.IsEmpty(editor.FindAll(".validation-errors li"));
    }

    [TestMethod]
    public void ClearingWriteOnlyReplacementDisablesItsSaveWithoutDeletingStoredValue()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid())) { ValueConfigured = true };
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        var editor = context.Render<SecretEditor>(parameters => parameters.Add(component => component.Name, "credential"));
        editor.WaitForElement("section.resource-section .button-primary").Click();
        editor.Find("input[type='password']").Input("unsaved-replacement");
        editor.Find("input[type='password']").Closest(".form-field")!.QuerySelector(".field-clear")!.Click();

        Assert.AreEqual(string.Empty, editor.Find("input[type='password']").GetAttribute("value"));
        Assert.IsTrue(editor.Find("section.resource-section .button-primary").HasAttribute("disabled"));
        Assert.AreEqual(0, client.ValueWrites);
        Assert.AreEqual(0, client.ValueDeletes);
        Assert.AreEqual(0, client.SecretMetadataSaves);
    }

    [TestMethod]
    public void ClearingVaultDisplayNameMarksDraftDirtyAndDiscardRestoresItsIdentity()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid()));
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        var editor = context.Render<VaultEditor>(parameters => parameters.Add(component => component.Name, "shared"));
        editor.WaitForElement("[data-testid='vault-display-name']").Closest(".form-field")!.QuerySelector(".field-clear")!.Click();
        Assert.HasCount(1, editor.FindAll(".unsaved-changes-bar"));
        editor.Find("form").Submit();
        Assert.AreEqual("Shared", client.Saved.Definition.DisplayName);
        editor.Find(".unsaved-changes-bar .button-secondary").Click();
        editor.WaitForAssertion(() => Assert.AreEqual("Shared", editor.Find("[data-testid='vault-display-name']").GetAttribute("value")));
        Assert.AreEqual("shared", editor.Find("[data-testid='vault-name']").GetAttribute("value"));
        Assert.IsEmpty(editor.FindAll(".unsaved-changes-bar"));
    }

    private sealed class StubSecretsClient(ResourceScopeRef tenant, ResourceScopeRef workspace) : ISecretsClient
    {
        public bool ValueConfigured { get; init; }
        public int SecretMetadataSaves { get; private set; }
        public int ValueWrites { get; private set; }
        public int ValueDeletes { get; private set; }
        public ResourceScopeRef? LastVaultConsumerScope { get; private set; }
        public VaultResource Saved { get; private set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "shared" },
            ScopeRef = tenant,
            Definition = new() { DisplayName = "Shared", ProviderType = "local" }
        };
        public SecretResource SavedSecret { get; private set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = "credential" },
            ScopeRef = tenant,
            Definition = new() { DisplayName = "Credential", Vault = new("shared", tenant), Key = "credential" }
        };

        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(string kind, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([
                new(tenant, ResourceScopeKind.Tenant, "Development", true),
                new(workspace, ResourceScopeKind.Workspace, "Default", true)
            ]);
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
            Task.FromResult(new ResourceSnapshot<VaultResponse>(new(Saved, "available"), "\"1\""));
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, ResourceScopeRef scopeRef, PutVaultRequest request, string etag, CancellationToken token)
        {
            Saved = Saved with { Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<VaultResource>(Saved, "\"2\""));
        }

        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<VaultResponse>>([new(Saved, "available")]);
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(ResourceScopeRef consumerScopeRef, CancellationToken token)
        {
            LastVaultConsumerScope = consumerScopeRef;
            return GetVaultsAsync(token);
        }
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
            Task.FromResult(new ResourceSnapshot<SecretResponse>(new(SavedSecret, ValueConfigured ? "Configured" : "Missing", ValueConfigured), "\"1\""));
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, ResourceScopeRef scopeRef, PutSecretRequest request, string etag, CancellationToken token)
        {
            SecretMetadataSaves++;
            SavedSecret = SavedSecret with { Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<SecretResource>(SavedSecret, "\"2\""));
        }
        public Task SetSecretValueAsync(string name, string value, CancellationToken token) { ValueWrites++; return Task.CompletedTask; }
        public Task SetSecretValueAsync(string name, ResourceScopeRef scopeRef, string value, CancellationToken token) { ValueWrites++; return Task.CompletedTask; }
        public Task DeleteSecretValueAsync(string name, CancellationToken token) { ValueDeletes++; return Task.CompletedTask; }
        public Task DeleteSecretValueAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) { ValueDeletes++; return Task.CompletedTask; }
        public Task DeleteSecretAsync(string name, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
            Task.FromResult(new SecretUsagesResponse([], 0));
    }
}
