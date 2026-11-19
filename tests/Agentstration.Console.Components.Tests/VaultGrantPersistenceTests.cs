using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components;
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
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);

        _ = context.Render<SecretEditor>();

        Assert.AreEqual(workspace, client.LastVaultConsumerScope);
    }

    [TestMethod]
    public async Task GrantIsSentBySaveAndShownWhenEditorIsReopened()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureVaultNavigation(context);

        var editor = context.Render<VaultEditor>(parameters => parameters
            .Add(component => component.Name, "shared"));
        await editor.InvokeAsync(() => editor.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
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
        await reopened.InvokeAsync(() => reopened.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        reopened.WaitForAssertion(() => Assert.AreEqual("Default", reopened.Find(".grant-target-identity .disclosure-trigger").TextContent));
    }

    [TestMethod]
    public async Task DiscardRestoresPersistedGrantsAndHidesBar()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureVaultNavigation(context);

        var editor = context.Render<VaultEditor>(parameters => parameters.Add(component => component.Name, "shared"));
        await editor.InvokeAsync(() => editor.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
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
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureSecretNavigation(context);

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
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureSecretNavigation(context);
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
        Assert.HasCount(1, editor.FindAll(".validation-message"));
        editor.Find(".unsaved-changes-bar .button-secondary").Click();
        editor.WaitForAssertion(() => Assert.AreEqual("credential", editor.Find("[data-testid='secret-vault-key']").GetAttribute("value")));
        Assert.IsEmpty(editor.FindAll(".unsaved-changes-bar"));
        Assert.IsEmpty(editor.FindAll(".validation-message"));
    }

    [TestMethod]
    public void ClearingWriteOnlyReplacementDisablesItsSaveWithoutDeletingStoredValue()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid())) { ValueConfigured = true };
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureSecretNavigation(context);
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
    public async Task ClearingVaultDisplayNameMarksDraftDirtyAndDiscardRestoresItsIdentity()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid()));
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureVaultNavigation(context);
        var editor = context.Render<VaultEditor>(parameters => parameters.Add(component => component.Name, "shared"));
        await editor.InvokeAsync(() => editor.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        editor.WaitForElement("[data-testid='vault-display-name']").Closest(".form-field")!.QuerySelector(".field-clear")!.Click();
        Assert.HasCount(1, editor.FindAll(".unsaved-changes-bar"));
        editor.Find("form").Submit();
        Assert.AreEqual("Shared", client.Saved.Definition.DisplayName);
        editor.Find(".unsaved-changes-bar .button-secondary").Click();
        editor.WaitForAssertion(() => Assert.AreEqual("Shared", editor.Find("[data-testid='vault-display-name']").GetAttribute("value")));
        Assert.AreEqual("shared", editor.Find("[data-testid='vault-name']").GetAttribute("value"));
        Assert.IsEmpty(editor.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task VaultCreationRetainsIdentityWhileFreelyNavigatingIncompleteSteps()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid())));
        var cut = context.Render<VaultEditor>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        cut.Find("[data-testid=vault-display-name]").Change("My vault");
        cut.Find("[data-testid=vault-name]").Change("my-vault");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("grants"));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("identity"));
        Assert.AreEqual("My vault", cut.Find("[data-testid=vault-display-name]").GetAttribute("value"));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.Contains("/vaults/my-vault", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task VaultOverviewUsesSavedIdentityAndRetainsDraftAcrossSections(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid())));
        var uri = $"http://localhost/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/identity", "definition/configuration", "master-key" })
        {
            var target = uri + "#" + section;
            module.Setup<string>("navigate", invocation => invocation.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
        var cut = context.Render<VaultEditor>(p => p.Add(c => c.Name, "shared"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        cut.Find("[data-testid=vault-display-name]").Change("Draft vault");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.Contains("Shared", cut.Find(".resource-identity-summary").TextContent);
        Assert.DoesNotContain("Draft vault", cut.Find(".resource-identity-summary").TextContent);
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.HasCount(1, cut.FindAll(".resource-details-main > .unsaved-changes-bar"));
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.AreEqual("Shared", cut.Find("[data-testid=vault-display-name]").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task SecretWizardValidatesTheVaultAndCreatesOnlyMetadata()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid()));
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        var cut = context.Render<SecretEditor>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        cut.Find("[data-testid=secret-display-name]").Change("My credential");
        cut.Find("[data-testid=secret-name]").Change("my-credential");
        cut.Find("[data-testid=resource-scope-picker]").Change(tenant.Value);
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("vault", wizard.CurrentStepId);
        cut.Find("[data-testid=secret-vault-select]").Change($"{tenant.Value}|default|shared");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("my-credential", client.SavedSecret.Name);
        Assert.AreEqual("my-credential", client.SavedSecret.Definition.Key);
        Assert.AreEqual(0, client.ValueWrites);
        Assert.AreEqual(0, client.ValueDeletes);
        Assert.Contains("/secrets/my-credential", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task SecretOverviewUsesSavedMetadataAndNeverRevealsTheWriteOnlyDraft(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid())) { ValueConfigured = true };
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureSecretNavigation(context);
        var cut = context.Render<SecretEditor>(p => p.Add(c => c.Name, "credential"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=secret-display-name]").Change("Draft credential");
        await cut.InvokeAsync(() => navigation.NavigateSection("value"));
        cut.Find("section.resource-section .button-primary").Click();
        cut.Find("input[type=password]").Input("unsaved-only-secret");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.Contains("Credential", cut.Find(".resource-identity-summary").TextContent);
        Assert.DoesNotContain("Draft credential", cut.Find(".resource-identity-summary").TextContent);
        Assert.DoesNotContain("unsaved-only-secret", cut.Find(".resource-configuration-summary").TextContent);
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await cut.InvokeAsync(() => navigation.NavigateSection("value"));
        Assert.AreEqual("unsaved-only-secret", cut.Find("input[type=password]").GetAttribute("value"));
        cut.Find("section.resource-section .button-secondary").Click();
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.AreEqual(0, client.ValueWrites);
        Assert.AreEqual(0, client.ValueDeletes);
        Assert.AreEqual(0, client.SecretMetadataSaves);
    }

    [TestMethod]
    public async Task VaultAccessDraftSurvivesMetadataSaveAndRemainsGuarded()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid()); var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace); var policies = new AccessPolicies();
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new AccessScopes(client, tenant, workspace));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureVaultNavigation(context);
        var cut = context.Render<VaultEditor>(p => p.Add(c => c.Name, "shared"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        cut.Find("[data-testid=vault-display-name]").Change("Updated vault");
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        var panel = cut.FindComponent<ResourceAccessPanel>().Instance;
        cut.Find("[data-testid=resource-access-target]").Change(workspace.Value);
        cut.Find("[data-testid=resource-access-add]").Click();
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        await cut.Find("#vault-resource-form").SubmitAsync();
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        Assert.AreSame(panel, cut.FindComponent<ResourceAccessPanel>().Instance);
        Assert.HasCount(1, cut.FindAll("[data-testid=resource-access-grant]"));
        Assert.IsNull(policies.Saved);
        cut.Find("[data-testid=resource-access-save]").Click();
        Assert.AreEqual(workspace, policies.Saved!.Grants.Single().TargetScopeRef);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task ManagedVaultKeepsDefinitionReadOnlyAndOwnerAccessAvailable()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid())) { ManagedVault = true });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureVaultNavigation(context);
        var cut = context.Render<VaultEditor>(p => p.Add(c => c.Name, "shared"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.IsTrue(cut.Find("fieldset").HasAttribute("disabled"));
        Assert.HasCount(1, cut.FindAll("a[data-resource-section=access]"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task SecretAccessDraftSurvivesMetadataSaveAndRemainsGuarded()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid()); var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new StubSecretsClient(tenant, workspace); var policies = new AccessPolicies();
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new AccessScopes(client, tenant, workspace));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureSecretNavigation(context);
        var cut = context.Render<SecretEditor>(p => p.Add(c => c.Name, "credential"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=secret-display-name]").Change("Updated secret");
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        var panel = cut.FindComponent<ResourceAccessPanel>().Instance;
        cut.Find("[data-testid=resource-access-target]").Change(workspace.Value);
        cut.Find("[data-testid=resource-access-add]").Click();
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        await cut.Find("#secret-resource-form").SubmitAsync();
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        Assert.AreSame(panel, cut.FindComponent<ResourceAccessPanel>().Instance);
        Assert.HasCount(1, cut.FindAll("[data-testid=resource-access-grant]"));
        Assert.IsNull(policies.Saved);
        cut.Find("[data-testid=resource-access-save]").Click();
        Assert.AreEqual(workspace, policies.Saved!.Grants.Single().TargetScopeRef);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public void ManagedSecretKeepsDefinitionReadOnlyAndOwnerAccessAvailable()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISecretsClient>(new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid())) { ManagedSecret = true });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/secrets/credential?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureSecretNavigation(context);
        var cut = context.Render<SecretEditor>(p => p.Add(c => c.Name, "credential"));
        Assert.IsTrue(cut.Find("fieldset").HasAttribute("disabled"));
        Assert.HasCount(1, cut.FindAll("a[data-resource-section=access]"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    private sealed class AccessPolicies : IResourceAccessPolicyClient
    {
        public ResourceAccessPolicy? Saved { get; private set; }
        public Task<ResourceAccessDecision> EvaluateAsync(EvaluateResourceAccessRequest request, CancellationToken cancellationToken) => Task.FromResult(new ResourceAccessDecision(false, ResourceAccessDecisionReasons.GrantMissing));
        public Task<ResourceAccessPolicySnapshot?> GetAsync(Guid uid, CancellationToken token) => Task.FromResult<ResourceAccessPolicySnapshot?>(null);
        public Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag, CancellationToken token) => Task.FromResult(new ResourceAccessPolicySnapshot(Saved = policy, "\"policy\""));
    }
    private sealed class AccessScopes(StubSecretsClient client, ResourceScopeRef tenant, ResourceScopeRef workspace) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken token) => Task.FromResult(new ResourceScopeInventoryResponse([
            new(tenant, ResourceScopeKind.Tenant, "Tenant", ResourceScopeRef.Instance, true, [new(client.Saved.Uid, ResourceNamespace.Default, SecretResourceKinds.Vault, "shared", DateTimeOffset.UnixEpoch), new(client.SavedSecret.Uid, ResourceNamespace.Default, SecretResourceKinds.Secret, "credential", DateTimeOffset.UnixEpoch)]),
            new(workspace, ResourceScopeKind.Workspace, "Workspace", tenant, true, [])]));
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken token) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(tenant, ResourceScopeKind.Tenant, "Tenant", true), new(workspace, ResourceScopeKind.Workspace, "Workspace", true)]);
    }

    [TestMethod]
    public async Task VaultRefreshRetainsInvalidIdentityAndAdoptsTheNewEtagOnlyOnExplicitDiscard()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid()); var client = new StubSecretsClient(tenant, ResourceScopeRef.Workspace(Guid.NewGuid()));
        using var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources"); context.Services.AddSingleton<ISecretsClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/vaults/shared?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        ConfigureVaultNavigation(context);
        var cut = context.Render<VaultEditor>(p => p.Add(c => c.Name, "shared"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        cut.Find("[data-testid=vault-display-name]").Change("");
        await cut.Find("#vault-resource-form").SubmitAsync();
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        client.VaultLoader = () => Task.FromResult(new ResourceSnapshot<VaultResponse>(new(client.Saved with { Definition = client.Saved.Definition with { DisplayName = "New server vault" } }, "available"), "\"external\""));
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual("", cut.Find("[data-testid=vault-display-name]").GetAttribute("value"));
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.Contains("Shared", cut.Find(".resource-identity-summary").TextContent);
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        Assert.AreEqual("New server vault", cut.Find("[data-testid=vault-display-name]").GetAttribute("value"));
        cut.Find("[data-testid=vault-display-name]").Change("Saved after discard");
        await cut.Find("#vault-resource-form").SubmitAsync();
        Assert.AreEqual("\"external\"", client.LastVaultEtag);
        Assert.AreEqual("Saved after discard", client.Saved.Definition.DisplayName);
    }

    private static void ConfigureSecretNavigation(BunitContext context)
    {
        var uri = context.Services.GetRequiredService<NavigationManager>().Uri.Split('#')[0];
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/configuration", "definition/grants", "value", "usages", "access" })
        {
            var target = uri + "#" + section;
            module.Setup<string>("navigate", invocation => invocation.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
    }

    private static void ConfigureVaultNavigation(BunitContext context)
    {
        var uri = context.Services.GetRequiredService<NavigationManager>().Uri.Split('#')[0];
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/identity", "definition/configuration", "master-key", "access", "yaml" })
        {
            var target = uri + "#" + section;
            module.Setup<string>("navigate", invocation => invocation.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
    }

    private sealed class StubSecretsClient(ResourceScopeRef tenant, ResourceScopeRef workspace) : ISecretsClient
    {
        public Func<Task<ResourceSnapshot<VaultResponse>>>? VaultLoader { get; set; }
        public string? LastVaultEtag { get; private set; }
        public bool ValueConfigured { get; init; }
        public bool ManagedVault { get; init; }
        public bool ManagedSecret { get; init; }
        public int SecretMetadataSaves { get; private set; }
        public int ValueWrites { get; private set; }
        public int ValueDeletes { get; private set; }
        public ResourceScopeRef? LastVaultConsumerScope { get; private set; }
        public VaultResource Saved { get; private set; } = new()
        {
            Uid = Guid.NewGuid(),
            Generation = 2,
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "shared" },
            ScopeRef = tenant,
            Definition = new() { DisplayName = "Shared", ProviderType = "local" }
        };
        public SecretResource SavedSecret { get; private set; } = new()
        {
            Uid = Guid.NewGuid(),
            Generation = 2,
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
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken token)
        {
            Saved = Saved with { Metadata = new() { Name = request.Name }, ScopeRef = request.ScopeRef, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<VaultResource>(Saved, "\"created\""));
        }
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
            VaultLoader?.Invoke() ?? Task.FromResult(new ResourceSnapshot<VaultResponse>(new(ManagedVault ? Saved with { Governance = ResourceGovernance.Core("vault") } : Saved, "available"), "\"1\""));
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, ResourceScopeRef scopeRef, PutVaultRequest request, string etag, CancellationToken token)
        {
            LastVaultEtag = etag;
            Saved = Saved with { Definition = request.Properties, Generation = Saved.Generation + 1 };
            return Task.FromResult(new ResourceSnapshot<VaultResource>(Saved, "\"2\""));
        }

        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<VaultResponse>>([new(ManagedVault ? Saved with { Governance = ResourceGovernance.Core("vault") } : Saved, "available")]);
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(ResourceScopeRef consumerScopeRef, CancellationToken token)
        {
            LastVaultConsumerScope = consumerScopeRef;
            return GetVaultsAsync(token);
        }
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
            Task.FromResult(new ResourceSnapshot<SecretResponse>(new(ManagedSecret ? SavedSecret with { Governance = ResourceGovernance.Core("secret") } : SavedSecret, ValueConfigured ? "Configured" : "Missing", ValueConfigured), "\"1\""));
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken token)
        {
            SavedSecret = SavedSecret with { Metadata = new() { Name = request.Name }, ScopeRef = request.ScopeRef, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<SecretResource>(SavedSecret, "\"created\""));
        }
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, ResourceScopeRef scopeRef, PutSecretRequest request, string etag, CancellationToken token)
        {
            SecretMetadataSaves++;
            SavedSecret = SavedSecret with { Definition = request.Properties, Generation = SavedSecret.Generation + 1 };
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
