using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ContractDirectionTabsTests
{
    [TestMethod]
    public void SwitchesBetweenInputAndOutputWithoutRenderingBothEditors()
    {
        using var context = new BunitContext();
        var rendered = context.Render<ContractDirectionTabs>(parameters => parameters
            .Add(component => component.Label, "Invocation contracts")
            .Add(component => component.InputLabel, "Input")
            .Add(component => component.OutputLabel, "Output")
            .Add(component => component.TestId, "agent-contract")
            .Add(component => component.InputContent,
                (RenderFragment)(builder => builder.AddMarkupContent(0, "<p data-testid='input-editor'>Input editor</p>")))
            .Add(component => component.OutputContent,
                (RenderFragment)(builder => builder.AddMarkupContent(0, "<p data-testid='output-editor'>Output editor</p>"))));

        Assert.AreEqual("true", rendered.Find("[data-testid='agent-contract-input-tab']").GetAttribute("aria-selected"));
        Assert.HasCount(1, rendered.FindAll("[data-testid='input-editor']"));
        Assert.IsEmpty(rendered.FindAll("[data-testid='output-editor']"));

        rendered.Find("[data-testid='agent-contract-output-tab']").Click();

        Assert.AreEqual("true", rendered.Find("[data-testid='agent-contract-output-tab']").GetAttribute("aria-selected"));
        Assert.IsEmpty(rendered.FindAll("[data-testid='input-editor']"));
        Assert.HasCount(1, rendered.FindAll("[data-testid='output-editor']"));
        Assert.AreEqual("agent-contract-output-tab",
            rendered.Find("[role='tabpanel']").GetAttribute("aria-labelledby"));
    }
}
