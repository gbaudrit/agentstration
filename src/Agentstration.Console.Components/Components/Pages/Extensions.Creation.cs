using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Components.Pages;

public partial class Extensions
{
    private EditContext? registrationContext;
    private ValidationMessageStore? registrationMessages;
    private CreationWizard? registrationWizard;
    private ResourceDefinitionForm? registrationDefinitionForm;
    private bool registrationInvalidInput, registrationFocusPending, registrationInitialScope;
    private string? registrationBaseline, registrationError;
    private long registrationRevision;
    private string RegistrationFingerprint => JsonSerializer.Serialize(form);
    private bool HasRegistrationDraft => editing && registrationBaseline is not null && (registrationInvalidInput || registrationBaseline != RegistrationFingerprint);
    private EditContext RegistrationContext
    {
        get
        {
            if (registrationContext is not null && ReferenceEquals(registrationContext.Model, form)) return registrationContext;
            if (registrationContext is not null) { registrationContext.OnValidationRequested -= RegistrationValidationRequested; registrationContext.OnFieldChanged -= RegistrationFieldChanged; }
            registrationContext = new(form); registrationMessages = new(registrationContext);
            registrationContext.OnValidationRequested += RegistrationValidationRequested; registrationContext.OnFieldChanged += RegistrationFieldChanged;
            return registrationContext;
        }
    }
    private void RegistrationValidationRequested(object? sender, ValidationRequestedEventArgs args) => registrationMessages?.Clear();
    private void RegistrationFieldChanged(object? sender, FieldChangedEventArgs args)
    {
        registrationMessages?.Clear(args.FieldIdentifier);
        if (args.FieldIdentifier.FieldName == nameof(form.AuthenticationMode))
            foreach (var field in new[] { nameof(form.CredentialName), nameof(form.CredentialNamespace), nameof(form.CredentialScopeRef) }) registrationMessages?.Clear(RegistrationContext.Field(field));
        RegistrationContext.NotifyValidationStateChanged();
    }
    private IReadOnlyDictionary<string, ResourceSectionPresentation> RegistrationPresentations => new Dictionary<string, ResourceSectionPresentation>(StringComparer.Ordinal)
    {
        ["definition/identity"] = new() { Fields = [nameof(form.Name), nameof(form.DisplayName), nameof(form.Namespace), nameof(form.ScopeRef)] },
        ["definition/configuration"] = new() { Fields = [nameof(form.Endpoint), nameof(form.ExpectedExtensionId), nameof(form.Enabled)] },
        ["definition/authentication"] = new() { Fields = [nameof(form.AuthenticationMode), nameof(form.CredentialName), nameof(form.CredentialNamespace), nameof(form.CredentialScopeRef)] }
    };
    private IReadOnlyDictionary<string, string> RegistrationFieldLabels => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(form.Name)] = IdentityLocalizer["TechnicalName"],
        [nameof(form.DisplayName)] = IdentityLocalizer["DisplayName"],
        [nameof(form.Namespace)] = T("Namespace"),
        [nameof(form.ScopeRef)] = Shared["Scope"],
        [nameof(form.Endpoint)] = T("AepEndpoint"),
        [nameof(form.AuthenticationMode)] = T("AuthenticationMode"),
        [nameof(form.CredentialName)] = T("CredentialName"),
        [nameof(form.CredentialNamespace)] = T("CredentialNamespace"),
        [nameof(form.CredentialScopeRef)] = T("CredentialScopeRef")
    };
    private IReadOnlyList<CreationWizardStep> RegistrationSteps =>
    [
        new("identity", Shared["Identity"], RegistrationIdentity) { IsComplete = RegistrationIdentityComplete },
        new("connection", T("Connection"), RegistrationConnection) { IsComplete = RegistrationConnectionComplete },
        new("authentication", T("AuthenticationMode"), RegistrationAuthentication) { IsComplete = RegistrationAuthenticationComplete },
        new("review", Shared["Verification"], RegistrationReview) { IsComplete = RegistrationIdentityComplete && RegistrationConnectionComplete && RegistrationAuthenticationComplete && !registrationInvalidInput }
    ];
    private bool RegistrationIdentityComplete => PropertyValid(nameof(form.Name), form.Name) && PropertyValid(nameof(form.DisplayName), form.DisplayName)
        && PropertyValid(nameof(form.Namespace), form.Namespace) && PropertyValid(nameof(form.ScopeRef), form.ScopeRef)
        && !form.ValidationIssues().Any(value => value.Field is nameof(form.Namespace) or nameof(form.ScopeRef));
    private bool RegistrationConnectionComplete => PropertyValid(nameof(form.Endpoint), form.Endpoint) && !form.ValidationIssues().Any(value => value.Field == nameof(form.Endpoint));
    private bool RegistrationAuthenticationComplete => !form.ValidationIssues().Any(value => value.Field is nameof(form.AuthenticationMode) or nameof(form.CredentialName) or nameof(form.CredentialNamespace) or nameof(form.CredentialScopeRef));
    private bool PropertyValid(string name, object? value) => Validator.TryValidateProperty(value, new(form) { MemberName = name }, null);
    private void InitializeRegistrationTracking()
    {
        ++registrationRevision;
        if (registrationContext is not null) { registrationContext.OnValidationRequested -= RegistrationValidationRequested; registrationContext.OnFieldChanged -= RegistrationFieldChanged; }
        registrationContext = null; registrationMessages = null; registrationInvalidInput = registrationFocusPending = false;
        registrationError = null; registrationBaseline = RegistrationFingerprint; registrationInitialScope = form.ScopeRef is null;
    }
    private void StartCreate()
    {
        if (disposed || loading || saving || editing || !CanRegister) return;
        activeTab = ExtensionTab.Catalog; creating = editing = true; form = new(); etag = null; InitializeRegistrationTracking();
    }
    private void RegistrationScopeChanged(ResourceScopeRef? scope)
    {
        var unchanged = !HasRegistrationDraft;
        form.ScopeRef = scope;
        RegistrationContext.NotifyFieldChanged(RegistrationContext.Field(nameof(form.ScopeRef)));
        if (registrationInitialScope && unchanged) registrationBaseline = RegistrationFingerprint;
        registrationInitialScope = false;
    }
    private void ResetRegistrationEditor()
    {
        ++registrationRevision;
        editing = creating = saving = false; form = new(); etag = null; registrationBaseline = registrationError = null;
        registrationInvalidInput = registrationFocusPending = registrationInitialScope = false;
        if (registrationContext is not null) { registrationContext.OnValidationRequested -= RegistrationValidationRequested; registrationContext.OnFieldChanged -= RegistrationFieldChanged; }
        registrationContext = null; registrationMessages = null;
    }
    private void CancelEdit() { if (!saving) ResetRegistrationEditor(); }
    private bool ValidateRegistration()
    {
        var context = RegistrationContext;
        var valid = context.Validate();
        foreach (var issue in form.ValidationIssues()) registrationMessages!.Add(context.Field(issue.Field), T(issue.MessageKey));
        context.NotifyValidationStateChanged();
        return valid && !context.GetValidationMessages().Any();
    }
    private string RegistrationInvalidStep() => ResourceDefinitionForm.FirstInvalidSection(RegistrationContext, RegistrationPresentations) switch
    {
        "definition/identity" => "identity",
        "definition/authentication" => "authentication",
        _ => "connection"
    };
    private Task RegistrationStepReady(string step)
    {
        if (registrationFocusPending)
        {
            registrationFocusPending = false; registrationDefinitionForm?.RequestValidationFocus();
        }
        return Task.CompletedTask;
    }
    private async Task InvalidRegistrationAsync(EditContext context)
    {
        if (!CanRegister || saving || registrationWizard is null) return;
        registrationFocusPending = true;
        await registrationWizard.GoToStepAsync(RegistrationInvalidStep());
        if (!disposed) registrationDefinitionForm?.RequestValidationFocus();
    }
    private async Task SubmitRegistrationAsync(EditContext context)
    {
        if (!ValidateRegistration()) { await InvalidRegistrationAsync(context); return; }
        await SaveRegistrationAsync();
    }
    private async Task FinishRegistrationAsync(CreationWizardFinish finish)
    {
        if (disposed || !CanRegister || saving) return;
        if (!ValidateRegistration())
        {
            registrationFocusPending = true; finish.ReturnToStepId = RegistrationInvalidStep(); return;
        }
        await SaveRegistrationAsync();
    }
    private bool IsCurrentRegistration(long revision, string context, RegistrationForm model) => !disposed && revision == registrationRevision && context == CurrentCatalogContext && ReferenceEquals(model, form);
    private async Task SaveRegistrationAsync()
    {
        if (disposed || !CanRegister || saving || !editing) return;
        var revision = registrationRevision; var context = CurrentCatalogContext; var model = form;
        var owner = model.ScopeRef; var properties = model.Properties(); var name = model.Name; var resourceNamespace = ResourceNamespace.Parse(model.Namespace);
        saving = true; registrationError = null;
        try
        {
            var saved = creating
                ? await Client.CreateRegistrationAsync(new(name, properties, model.Namespace, owner), cancellation.Token)
                : await Client.UpdateRegistrationAsync(resourceNamespace, name, new(properties), etag!, cancellation.Token);
            if (!IsCurrentRegistration(revision, context, model)) return;
            if (saved.Value.Name != name || saved.Value.Namespace != resourceNamespace || saved.Value.ScopeRef != owner)
                throw new AgentstrationApiException(T("IdentityMismatch"), string.Empty);
            ResetRegistrationEditor();
            await LoadAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or ArgumentException or HttpRequestException)
        { if (IsCurrentRegistration(revision, context, model)) registrationError = exception.Message; }
        finally { if (IsCurrentRegistration(revision, context, model)) saving = false; }
    }
    private sealed record RegistrationValidationIssue(string Field, string MessageKey);
    private sealed class RegistrationForm
    {
        [Required] public string Name { get; set; } = string.Empty;
        [Required] public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
        [Required] public string DisplayName { get; set; } = string.Empty;
        [Required] public ResourceScopeRef? ScopeRef { get; set; }
        [Required, Url] public string Endpoint { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string? ExpectedExtensionId { get; set; }
        public AepTransportAuthenticationMode AuthenticationMode { get; set; }
        public string CredentialName { get; set; } = string.Empty;
        public string CredentialNamespace { get; set; } = ResourceNamespace.DefaultValue;
        public string CredentialScopeRef { get; set; } = string.Empty;
        public IEnumerable<RegistrationValidationIssue> ValidationIssues()
        {
            if (ScopeRef is { Kind: not ResourceScopeKind.Tenant }) yield return new(nameof(ScopeRef), "RegistrationScopeRequired");
            if (!ValidNamespace(Namespace)) yield return new(nameof(Namespace), "RegistrationNamespaceInvalid");
            if (!string.IsNullOrWhiteSpace(Endpoint) && !Uri.TryCreate(Endpoint, UriKind.Absolute, out _)) yield return new(nameof(Endpoint), "EndpointMustBeAbsolute");
            if (AuthenticationMode != AepTransportAuthenticationMode.StaticBearer) yield break;
            if (string.IsNullOrWhiteSpace(CredentialName)) yield return new(nameof(CredentialName), "CredentialRequired");
            if (!ValidNamespace(CredentialNamespace)) yield return new(nameof(CredentialNamespace), "RegistrationCredentialNamespaceInvalid");
            if (!ValidCredentialScope(CredentialScopeRef)) yield return new(nameof(CredentialScopeRef), "RegistrationCredentialScopeInvalid");
        }
        private static bool ValidNamespace(string value) { try { _ = ResourceNamespace.Parse(value); return true; } catch (ArgumentException) { return false; } }
        private static bool ValidCredentialScope(string value) { try { if (!string.IsNullOrWhiteSpace(value)) _ = ResourceScopeRef.Parse(value); return true; } catch (Exception exception) when (exception is ArgumentException or FormatException) { return false; } }
        public ExtensionRegistrationProperties Properties() => new()
        {
            DisplayName = DisplayName,
            Endpoint = new(Endpoint, UriKind.Absolute),
            Enabled = Enabled,
            ExpectedExtensionId = ExpectedExtensionId,
            AuthenticationMode = AuthenticationMode,
            Credential = AuthenticationMode != AepTransportAuthenticationMode.StaticBearer ? null : new(CredentialName,
                string.IsNullOrWhiteSpace(CredentialScopeRef) ? null : ResourceScopeRef.Parse(CredentialScopeRef), ResourceNamespace.Parse(CredentialNamespace))
        };
        public static RegistrationForm From(ExtensionRegistrationResource resource) => new()
        {
            Name = resource.Name,
            Namespace = resource.Namespace.Value,
            DisplayName = resource.Definition.DisplayName,
            ScopeRef = resource.ScopeRef,
            Endpoint = resource.Definition.Endpoint.AbsoluteUri,
            Enabled = resource.Definition.Enabled,
            ExpectedExtensionId = resource.Definition.ExpectedExtensionId,
            AuthenticationMode = resource.Definition.AuthenticationMode,
            CredentialName = resource.Definition.Credential?.Name ?? string.Empty,
            CredentialNamespace = resource.Definition.Credential?.Namespace?.Value ?? resource.Namespace.Value,
            CredentialScopeRef = resource.Definition.Credential?.ScopeRef?.Value ?? string.Empty
        };
    }
}
