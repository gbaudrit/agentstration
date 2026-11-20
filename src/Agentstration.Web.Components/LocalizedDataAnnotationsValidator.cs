using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Components;

/// <summary>Validates a form's root model annotations using shared localized messages and field labels.</summary>
public sealed class LocalizedDataAnnotationsValidator : ComponentBase, IDisposable
{
    [CascadingParameter] public EditContext? CurrentEditContext { get; set; }
    [Parameter, EditorRequired] public IReadOnlyDictionary<string, string> FieldLabels { get; set; } = new Dictionary<string, string>();
    [Inject] private IStringLocalizer<SharedFormStrings> Localizer { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    private EditContext? subscribed;
    private ValidationMessageStore? messages;
    private PropertyInfo[] properties = [];
    private IReadOnlyDictionary<string, string> labels = new Dictionary<string, string>();
    private readonly HashSet<string> visited = new(StringComparer.Ordinal);
    private bool submitted;
    private string culture = string.Empty;

    protected override void OnParametersSet()
    {
        if (CurrentEditContext is null) throw new InvalidOperationException("Localized validation requires an EditContext.");
        var contextChanged = !ReferenceEquals(subscribed, CurrentEditContext);
        if (contextChanged)
        {
            Unsubscribe();
            subscribed = CurrentEditContext;
            messages = new(subscribed);
            properties = subscribed.Model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0).ToArray();
            visited.Clear(); submitted = false;
            subscribed.OnFieldChanged += FieldChanged;
            subscribed.OnValidationRequested += ValidationRequested;
        }
        foreach (var property in properties.Where(property => property.IsDefined(typeof(ValidationAttribute), true)))
        {
            if (!FieldLabels.TryGetValue(property.Name, out var label) || string.IsNullOrWhiteSpace(label))
                throw new ArgumentException($"Provide a localized field label for {property.Name}.", nameof(FieldLabels));
        }
        var presentationChanged = culture != CultureInfo.CurrentUICulture.Name || labels.Count != FieldLabels.Count
            || labels.Any(pair => !FieldLabels.TryGetValue(pair.Key, out var label) || label != pair.Value);
        labels = new Dictionary<string, string>(FieldLabels, StringComparer.Ordinal);
        culture = CultureInfo.CurrentUICulture.Name;
        if (!contextChanged && presentationChanged)
        {
            if (submitted) ValidateModel();
            else
            {
                foreach (var property in properties.Where(property => visited.Contains(property.Name))) ValidateProperty(property);
                subscribed!.NotifyValidationStateChanged();
            }
        }
    }

    private void FieldChanged(object? sender, FieldChangedEventArgs args)
    {
        if (subscribed is null || !ReferenceEquals(args.FieldIdentifier.Model, subscribed.Model)) return;
        var property = properties.FirstOrDefault(property => property.Name == args.FieldIdentifier.FieldName);
        if (property is null) return;
        visited.Add(property.Name);
        if (submitted) { ValidateModel(); return; }
        ValidateProperty(property);
        foreach (var dependent in properties.Where(candidate => visited.Contains(candidate.Name)
            && candidate.GetCustomAttributes<CompareAttribute>(true).Any(rule => rule.OtherProperty == property.Name)))
            ValidateProperty(dependent);
        subscribed.NotifyValidationStateChanged();
    }

    private void ValidationRequested(object? sender, ValidationRequestedEventArgs args)
    {
        submitted = true;
        ValidateModel();
    }

    private void ValidateModel()
    {
        messages!.Clear();
        var valid = true;
        foreach (var property in properties) valid &= ValidateProperty(property, clear: false);
        var context = new ValidationContext(subscribed!.Model, Services, null);
        if (valid)
        {
            foreach (var rule in subscribed.Model.GetType().GetCustomAttributes<ValidationAttribute>(true))
            {
                if (rule.GetValidationResult(subscribed.Model, context) is { } result)
                {
                    AddResult(result, string.Empty, result.ErrorMessage!);
                    valid = false;
                }
            }
        }
        if (valid && subscribed.Model is IValidatableObject model)
        {
            foreach (var result in model.Validate(context))
                if (result != ValidationResult.Success) AddResult(result, string.Empty, result.ErrorMessage!);
        }
        subscribed.NotifyValidationStateChanged();
    }

    private bool ValidateProperty(PropertyInfo property, bool clear = true)
    {
        var field = subscribed!.Field(property.Name);
        if (clear) messages!.Clear(field);
        var rules = property.GetCustomAttributes<ValidationAttribute>(true).ToArray();
        if (rules.Length == 0) return true;
        var context = new ValidationContext(subscribed.Model, Services, null) { MemberName = property.Name, DisplayName = labels[property.Name] };
        var value = property.GetValue(subscribed.Model);
        if (rules.OfType<RequiredAttribute>().FirstOrDefault() is { } required && !ValidateRule(required)) return false;
        var valid = true;
        foreach (var rule in rules.Where(rule => rule is not RequiredAttribute)) valid &= ValidateRule(rule);
        return valid;

        bool ValidateRule(ValidationAttribute rule)
        {
            if (rule.GetValidationResult(value, context) is not { } result) return true;
            var text = DataAnnotationMessages.Format(Localizer, rule, result, context.DisplayName,
                name => labels.TryGetValue(name, out var label) ? label : name);
            if (clear) messages!.Add(field, text);
            else AddResult(result, property.Name, text);
            return false;
        }
    }

    private void AddResult(ValidationResult result, string defaultField, string text)
    {
        var members = result.MemberNames.ToArray();
        foreach (var member in members.Length == 0 ? [defaultField] : members)
            messages!.Add(subscribed!.Field(member), text ?? Localizer["Validation.Invalid"]);
    }

    private void Unsubscribe()
    {
        if (subscribed is null) return;
        subscribed.OnFieldChanged -= FieldChanged;
        subscribed.OnValidationRequested -= ValidationRequested;
        var previous = subscribed;
        messages?.Clear();
        subscribed = null;
        messages = null;
        previous.NotifyValidationStateChanged();
    }
    public void Dispose() => Unsubscribe();
}
