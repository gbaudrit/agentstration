using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Components.Forms;

/// <summary>Applies the shared field affordance without changing server form binding.</summary>
[HtmlTargetElement("input", Attributes = "clear-label")]
[HtmlTargetElement("textarea", Attributes = "clear-label")]
[HtmlTargetElement("select", Attributes = "clear-label")]
public sealed class ClearableControlTagHelper(IStringLocalizer<SharedFormStrings> localizer) : TagHelper
{
    [HtmlAttributeName("clear-label")] public string Label { get; set; } = string.Empty;
    [HtmlAttributeName("validation-id")] public string? ValidationId { get; set; }
    public override int Order => 1000;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        output.Attributes.RemoveAll("clear-label");
        output.Attributes.RemoveAll("validation-id");
        if (!string.IsNullOrWhiteSpace(ValidationId))
        {
            var existing = output.Attributes["aria-describedby"]?.Value?.ToString();
            output.Attributes.SetAttribute("aria-describedby", string.IsNullOrWhiteSpace(existing) ? ValidationId : $"{existing} {ValidationId}");
        }
        var type = output.Attributes["type"]?.Value?.ToString();
        if (type is "hidden" or "checkbox" or "radio" or "file") return;

        var multiline = output.TagName == "textarea";
        var wrapper = multiline ? "clearable-field clearable-multiline native-form-control" : "clearable-field native-form-control";
        output.PreElement.AppendHtml($"<span class=\"{wrapper}\">");
        var button = new TagBuilder("button");
        button.Attributes["type"] = "button";
        button.Attributes["class"] = "field-clear";
        button.Attributes["data-native-field-clear"] = string.Empty;
        button.Attributes["aria-label"] = localizer["ClearField", Label];
        button.Attributes["hidden"] = "hidden";
        button.InnerHtml.AppendHtml("<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" aria-hidden=\"true\" focusable=\"false\"><path d=\"m6 6 12 12M18 6 6 18\" /></svg>");
        output.PostElement.AppendHtml(button);
        output.PostElement.AppendHtml("</span>");

        if (output.TagName == "select")
        {
            var content = await output.GetChildContentAsync();
            if (!content.GetContent(HtmlEncoder.Default).Contains("value=\"\"", StringComparison.Ordinal))
            {
                // A real empty option makes server model binding see a cleared
                // required value instead of silently receiving an omitted field.
                var empty = new TagBuilder("option");
                empty.Attributes["value"] = string.Empty;
                empty.Attributes["hidden"] = "hidden";
                empty.InnerHtml.Append(localizer["SelectValue"]);
                output.PostContent.AppendHtml(empty);
            }
        }
    }
}
