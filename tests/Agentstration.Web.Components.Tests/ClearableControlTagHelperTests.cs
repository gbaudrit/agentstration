using System.Globalization;
using System.Text.Encodings.Web;
using Agentstration.Web.Components.Forms;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ClearableControlTagHelperTests
{
    [TestMethod]
    [DataRow("en-US", "Clear Password")]
    [DataRow("fr-FR", "Vider Password")]
    public async Task ServerFieldPreservesPostBindingAndEncodesItsLocalizedClearLabel(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
            var helper = new ClearableControlTagHelper(services.GetRequiredService<IStringLocalizer<SharedFormStrings>>())
                { Label = "Password", ValidationId = "password-error" };
            var output = Output("input", new() { { "name", "Input.Password" }, { "id", "Input_Password" }, { "type", "password" }, { "aria-describedby", "password-help" } });
            await helper.ProcessAsync(Context(), output);
            Assert.AreEqual("Input.Password", output.Attributes["name"].Value);
            Assert.AreEqual("Input_Password", output.Attributes["id"].Value);
            Assert.AreEqual("password-help password-error", output.Attributes["aria-describedby"].Value);
            var html = output.PostElement.GetContent(HtmlEncoder.Default);
            StringAssert.Contains(html, $"aria-label=\"{expected}\"");
            StringAssert.Contains(html, "type=\"button\"");
            StringAssert.Contains(html, "hidden=\"hidden\"");
            Assert.IsFalse(html.Contains("type=\"submit\"", StringComparison.Ordinal));

            helper.Label = "\"<script>";
            var encoded = Output("input", new());
            await helper.ProcessAsync(Context(), encoded);
            Assert.IsFalse(encoded.PostElement.GetContent(HtmlEncoder.Default).Contains("<script>", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    public async Task ClearingServerSelectPostsAnExplicitEmptyValueWithoutChangingExistingChoices()
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var helper = new ClearableControlTagHelper(services.GetRequiredService<IStringLocalizer<SharedFormStrings>>()) { Label = "Expiry" };
        var output = Output("select", new() { { "name", "Input.ExpiresInDays" } }, "<option value=\"30\" selected>30 days</option>");
        await helper.ProcessAsync(Context(), output);
        StringAssert.Contains(output.PostContent.GetContent(HtmlEncoder.Default), "value=\"\"");
        Assert.IsFalse(output.PostContent.GetContent().Contains("disabled", StringComparison.Ordinal));
        var withEmpty = Output("select", new(), "<option value=\"\">Choose</option>");
        await helper.ProcessAsync(Context(), withEmpty);
        Assert.AreEqual(string.Empty, withEmpty.PostContent.GetContent());
    }

    [TestMethod]
    [DataRow("hidden")]
    [DataRow("checkbox")]
    [DataRow("radio")]
    [DataRow("file")]
    public async Task SpecializedNativeControlsKeepTheirOriginalSemantics(string type)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var helper = new ClearableControlTagHelper(services.GetRequiredService<IStringLocalizer<SharedFormStrings>>());
        var output = Output("input", new() { { "type", type } });
        await helper.ProcessAsync(Context(), output);
        Assert.AreEqual(string.Empty, output.PreElement.GetContent());
        Assert.AreEqual(string.Empty, output.PostElement.GetContent());
    }

    private static TagHelperContext Context() => new(new TagHelperAttributeList(), new Dictionary<object, object>(), "test");
    private static TagHelperOutput Output(string tag, TagHelperAttributeList attributes, string content = "") =>
        new(tag, attributes, (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent().SetHtmlContent(content)));
}
