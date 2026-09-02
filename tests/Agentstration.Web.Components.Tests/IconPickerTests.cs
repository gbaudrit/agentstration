using Bunit;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class IconPickerTests
{
    [TestMethod]
    public void CatalogExposesThePinnedTablerOutlineSet()
    {
        Assert.IsGreaterThan(5_000, TablerIconCatalog.All.Count);
        Assert.IsTrue(TablerIconCatalog.TryNormalize("sparkles", out var name));
        Assert.AreEqual("sparkles", name);
        Assert.IsFalse(TablerIconCatalog.TryNormalize("../../unsafe#icon", out _));
        Assert.ThrowsExactly<ArgumentException>(() => TablerIconCatalog.SpriteHref("../../unsafe#icon"));
    }

    [TestMethod]
    public async Task PickerSearchesAndSelectsAnIcon()
    {
        using var context = new BunitContext();
        var selected = string.Empty;
        var rendered = context.Render<IconPicker>(parameters => parameters
            .Add(value => value.Value, selected)
            .Add(value => value.ValueChanged, (string value) => selected = value)
            .Add(value => value.SelectedLabel, "Selected icon")
            .Add(value => value.NoneLabel, "No icon")
            .Add(value => value.ClearLabel, "Clear")
            .Add(value => value.SearchLabel, "Icon")
            .Add(value => value.SearchPlaceholder, "Search icons")
            .Add(value => value.ChoicesLabel, "Available icons")
            .Add(value => value.NoResultsLabel, "No icon found")
            .Add(value => value.MoreResultsLabel, "Refine search"));

        await rendered.Find("input[type='search']").InputAsync(new ChangeEventArgs { Value = "message chatbot" });
        var option = rendered.Find("[role='option'][title='message-chatbot']");
        await option.ClickAsync(new());

        Assert.AreEqual("message-chatbot", selected);
        var href = option.QuerySelector("use")!.GetAttribute("href");
        Assert.AreEqual("_content/Agentstration.Web.Components/tabler/tabler-sprite.svg#tabler-message-chatbot", href);
    }
}
