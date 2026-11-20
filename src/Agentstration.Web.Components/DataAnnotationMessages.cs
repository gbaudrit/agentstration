using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Components;

internal static class DataAnnotationMessages
{
    public static string Format(IStringLocalizer<SharedFormStrings> strings, ValidationAttribute rule,
        ValidationResult result, string label, Func<string, string> fieldLabel)
    {
        // Explicit/custom error messages retain their author's localization and meaning.
        var builtInMessage = rule switch
        {
            EmailAddressAttribute => new EmailAddressAttribute().ErrorMessage,
            UrlAttribute => new UrlAttribute().ErrorMessage,
            PhoneAttribute => new PhoneAttribute().ErrorMessage,
            CreditCardAttribute => new CreditCardAttribute().ErrorMessage,
            EnumDataTypeAttribute enumeration => new EnumDataTypeAttribute(enumeration.EnumType).ErrorMessage,
            _ => null
        };
        if (rule.ErrorMessageResourceName is not null || rule.ErrorMessage is { } message && message != builtInMessage)
            return result.ErrorMessage!;
        return rule switch
        {
            RequiredAttribute => strings["Validation.Required", label],
            StringLengthAttribute { MinimumLength: > 0 } length => strings["Validation.StringLengthRange", label, length.MinimumLength, length.MaximumLength],
            StringLengthAttribute length => strings["Validation.StringLength", label, length.MaximumLength],
            MinLengthAttribute length => strings["Validation.MinLength", label, length.Length],
            MaxLengthAttribute length => strings["Validation.MaxLength", label, length.Length],
            RangeAttribute range => strings["Validation.Range", label, range.Minimum, range.Maximum],
            RegularExpressionAttribute => strings["Validation.Format", label],
            EmailAddressAttribute => strings["Validation.Email", label],
            UrlAttribute => strings["Validation.Url", label],
            PhoneAttribute => strings["Validation.Phone", label],
            CreditCardAttribute => strings["Validation.CreditCard", label],
            EnumDataTypeAttribute => strings["Validation.Selection", label],
            CompareAttribute compare => strings["Validation.Compare", label, fieldLabel(compare.OtherProperty)],
            _ => result.ErrorMessage!
        };
    }
}
