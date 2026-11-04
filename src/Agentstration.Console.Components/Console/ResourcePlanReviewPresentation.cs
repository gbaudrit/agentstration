using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Console;

internal sealed class ResourcePlanReviewPresentation(IStringLocalizer<ResourcePlansStrings> localizer)
{
    public string OperationCountLabel(ResourceChangeOperation operation, int count) => T($"Operation{(count == 1 ? string.Empty : "Plural")}.{operation}");
    public string DisplayValue(string path, string? value)
    {
        if (value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) return T("Yes");
        if (value?.Equals("false", StringComparison.OrdinalIgnoreCase) == true) return T("No");
        if (value is not null)
        {
            var localized = localizer[$"Value.{path}.{value}"];
            if (!localized.ResourceNotFound) return localized.Value;
        }
        return ResourcePlanReviewProjection.FormatValue(value);
    }
    public string ResourceKindLabel(string kind) => kind switch
    {
        "Agent" => T("Kind.Agent"),
        "Flow" => T("Kind.Flow"),
        "Entry" => T("Kind.Entry"),
        _ => kind
    };
    public string FieldLabel(string path)
    {
        var value = localizer[$"Field.{path}"];
        if (!value.ResourceNotFound) return value.Value;
        if (path.StartsWith("definition.spec.", StringComparison.Ordinal)) return T("Field.FlowSpec");
        if (path.StartsWith("definition.presentation.", StringComparison.Ordinal)) return T("Field.Presentation");
        if (path.StartsWith("definition.binding.", StringComparison.Ordinal)) return T("Field.Binding");
        return T("Field.Other");
    }
    public string IssueTitle(string code) => code switch
    {
        "resource_change_model_profile_invalid" => T("Issue.ModelProfile.Title"),
        "resource_change_canonical_invalid" => T("Issue.Canonical.Title"),
        "resource_change_create_conflict" => T("Issue.Conflict.Title"),
        "resource_change_current_missing" or "resource_change_stale" => T("Issue.Stale.Title"),
        "resource_change_binding_stale" => T("Issue.BindingStale.Title"),
        "resource_change_dependency_missing" or "resource_change_dependency_order_invalid" => T("Issue.Dependency.Title"),
        _ => T("Issue.Generic.Title")
    };
    public string IssueDescription(string code, ResourceChange? change) => code switch
    {
        "resource_change_model_profile_invalid" => T("Issue.ModelProfile.Description", change is null ? T("UnknownProfile") : ResourcePlanReviewProjection.ModelProfile(change) ?? T("UnknownProfile")),
        "resource_change_canonical_invalid" => T("Issue.Canonical.Description"),
        "resource_change_create_conflict" => T("Issue.Conflict.Description"),
        "resource_change_current_missing" or "resource_change_stale" => T("Issue.Stale.Description"),
        "resource_change_binding_stale" => T("Issue.BindingStale.Description"),
        "resource_change_dependency_missing" or "resource_change_dependency_order_invalid" => T("Issue.Dependency.Description"),
        _ => T("Issue.Generic.Description")
    };

    public static UiStatus OperationTone(ResourceChangeOperation operation) => operation switch { ResourceChangeOperation.Create => UiStatus.Success, ResourceChangeOperation.Update => UiStatus.Info, ResourceChangeOperation.Delete => UiStatus.Danger, _ => UiStatus.Neutral };
    private string T(string key, params object[] args) => localizer[key, args].Value;
}
