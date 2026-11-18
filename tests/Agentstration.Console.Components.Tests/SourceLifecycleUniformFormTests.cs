using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SourceLifecycleUniformFormTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void LifecycleFieldsKeepChangeTimingAndIndependentHistoryRetention(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        string? ttl = null, retention = null;
        bool? history = null;
        var rendered = context.Render<SourceLifecycleEditor>(parameters => parameters
            .Add(value => value.Enabled, true)
            .Add(value => value.KeepHistory, true)
            .Add(value => value.TimeToLiveText, "01:00:00")
            .Add(value => value.HistoryRetentionText, "02:00:00")
            .Add(value => value.TimeToLiveTextChanged, value => ttl = value)
            .Add(value => value.HistoryRetentionTextChanged, value => retention = value)
            .Add(value => value.KeepHistoryChanged, value => history = value));

        Assert.HasCount(2, rendered.FindComponents<FormField>());
        var ttlInput = rendered.Find("[data-testid=lifecycle-ttl]");
        ttlInput.Input("03:00:00");
        Assert.IsNull(ttl);
        ttlInput.Change("03:00:00");
        Assert.AreEqual("03:00:00", ttl);
        Assert.IsNull(retention);
        rendered.Find("[data-testid=lifecycle-history]").Change(false);
        Assert.IsFalse(history);
    }
}
