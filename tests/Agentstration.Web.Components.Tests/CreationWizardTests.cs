using System.Globalization;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class CreationWizardTests
{
    [TestMethod]
    public async Task StepReadyRunsAfterTheAcceptedPaneFocusSoTheCallerCanFocusAValidationField()
    {
        using var context = Context();
        string? ready = null; var paneFocusCompleted = false;
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, Steps())
            .Add(c => c.OnStepReady, (string step) => { ready = step; paneFocusCompleted = context.JSInterop.Invocations.Any(call => call.Identifier == "Blazor._internal.domWrapper.focus"); }));
        await cut.Find("[data-testid=creation-wizard-next]").ClickAsync();
        Assert.AreEqual("configuration", ready);
        Assert.IsTrue(paneFocusCompleted);
    }

    [TestMethod]
    [DataRow("en-US", "Continue", "Previous", "Incomplete", "Complete")]
    [DataRow("fr-FR", "Continuer", "Précédent", "À compléter", "Complète")]
    public async Task StepsPreserveTheirContentAndFocusWhenNavigating(string culture, string next, string previous, string incomplete, string complete)
    {
        using var cultureScope = new CultureScope(culture);
        using var context = Context();
        var steps = Steps().Select(step => step with { IsComplete = false }).ToArray();
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, steps));
        Assert.Contains(incomplete, cut.Find("[data-wizard-step=identity]").GetAttribute("aria-label")!);
        Assert.IsFalse(cut.Find("[data-wizard-step=configuration]").HasAttribute("disabled"));
        Assert.AreEqual("identity", cut.Instance.CurrentStepId);
        Assert.Contains(next, cut.Find("[data-testid=creation-wizard-next]").TextContent);
        await cut.Find("input").InputAsync("my draft");
        var instance = cut.FindComponent<WizardProbe>().Instance;
        await cut.Find("[data-testid=creation-wizard-next-top]").ClickAsync();
        Assert.AreEqual("configuration", cut.Instance.CurrentStepId);
        Assert.IsTrue(cut.Find("[data-step-id=identity]").HasAttribute("hidden"));
        Assert.HasCount(1, cut.FindAll("[aria-current=step]"));
        Assert.IsTrue(context.JSInterop.Invocations.Any(call => call.Identifier == "Blazor._internal.domWrapper.focus"));
        Assert.Contains(previous, cut.Find("[data-testid=creation-wizard-previous]").TextContent);
        await cut.Find("[data-testid=creation-wizard-previous]").ClickAsync();
        Assert.AreSame(instance, cut.FindComponent<WizardProbe>().Instance);
        Assert.AreEqual("my draft", cut.FindComponent<WizardProbe>().Instance.Value);
        cut.Render(p => p.Add(c => c.Steps, new[] { steps[0] with { IsComplete = true }, steps[1] }));
        Assert.Contains(complete, cut.Find("[data-wizard-step=identity]").GetAttribute("aria-label")!);
        Assert.AreEqual("1", cut.Find("[data-wizard-step=identity] .creation-wizard-number").TextContent);
        Assert.HasCount(1, cut.FindAll("[data-wizard-step=identity] .creation-wizard-state svg"));
        Assert.AreEqual("true", cut.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        Assert.AreSame(instance, cut.FindComponent<WizardProbe>().Instance);
    }

    [TestMethod]
    public async Task ExplicitCallerCancellationCanStillPreventNavigation()
    {
        using var context = Context();
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, Steps())
            .Add(c => c.OnStepChanging, (CreationWizardStepChange change) => change.Cancel = true));
        await cut.InvokeAsync(() => cut.Instance.GoToStepAsync("configuration"));
        await cut.Find("[data-testid=creation-wizard-next]").ClickAsync();
        Assert.AreEqual("identity", cut.Instance.CurrentStepId);
        Assert.IsEmpty(cut.FindAll("[data-testid=creation-wizard-finish]"));
    }

    [TestMethod]
    public async Task AnAsyncTransitionIsSerializedAndCompletionRequiresTheLastStep()
    {
        using var context = Context();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        var finishes = 0;
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, Steps())
            .Add(c => c.OnStepChanging, async (CreationWizardStepChange _) => { validations++; await pending.Task; })
            .Add(c => c.OnFinish, (CreationWizardFinish _) => finishes++));
        await cut.InvokeAsync(cut.Instance.FinishAsync);
        Assert.AreEqual(0, finishes);
        var moving = cut.Find("[data-testid=creation-wizard-next-top]").ClickAsync();
        cut.WaitForAssertion(() => Assert.AreEqual(1, validations));
        Assert.IsTrue(cut.Find("[data-testid=creation-wizard-next-top]").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("[data-testid=creation-wizard-next]").HasAttribute("disabled"));
        await cut.InvokeAsync(cut.Instance.ContinueAsync);
        Assert.AreEqual(1, validations);
        pending.SetResult();
        await moving;
        await cut.Find("[data-testid=creation-wizard-finish]").ClickAsync();
        Assert.AreEqual(1, finishes);
    }

    [TestMethod]
    public async Task ConditionalStepsAndBusyStateDoNotPermitUnauthorizedCommands()
    {
        using var context = Context();
        var steps = Steps();
        var finishes = 0;
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, new[] { steps[0] with { Visible = false }, steps[1] })
            .Add(c => c.Busy, true).Add(c => c.OnFinish, (CreationWizardFinish _) => finishes++));
        Assert.AreEqual("configuration", cut.Instance.CurrentStepId);
        Assert.IsEmpty(cut.FindAll("[data-step-id=identity]"));
        await cut.InvokeAsync(cut.Instance.FinishAsync);
        Assert.AreEqual(0, finishes);
        cut.Render(p => p.Add(c => c.Busy, false).Add(c => c.CanFinish, false));
        await cut.InvokeAsync(cut.Instance.FinishAsync);
        Assert.AreEqual(0, finishes);
        Assert.IsTrue(cut.Find("[data-testid=creation-wizard-finish]").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("[data-testid=creation-wizard-finish-top]").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task DisposingDuringValidationDoesNotNavigateOrFocusDetachedContent()
    {
        using var context = Context();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, Steps())
            .Add(c => c.OnStepChanging, async (CreationWizardStepChange _) => { entered = true; await pending.Task; }));
        var instance = cut.Instance;
        var moving = cut.Find("[data-testid=creation-wizard-next]").ClickAsync();
        cut.WaitForAssertion(() => Assert.IsTrue(entered));
        await cut.InvokeAsync(instance.Dispose);
        pending.SetResult();
        await moving;
        Assert.AreEqual("identity", instance.CurrentStepId);
        Assert.IsFalse(context.JSInterop.Invocations.Any(call => call.Identifier == "Blazor._internal.domWrapper.focus"));
    }

    [TestMethod]
    public async Task ANewlyVisibleIncompleteStepDoesNotForceNavigationOrBlockOtherSteps()
    {
        using var context = Context();
        var steps = Steps();
        steps[0] = steps[0] with { IsComplete = false };
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, new[] { steps[0] with { Visible = false }, steps[1] }));
        Assert.AreEqual("configuration", cut.Instance.CurrentStepId);
        cut.Render(p => p.Add(c => c.Steps, steps));
        Assert.AreEqual("configuration", cut.Instance.CurrentStepId);
        await cut.Find("[data-wizard-step=identity]").ClickAsync();
        Assert.AreEqual("identity", cut.Instance.CurrentStepId);
        Assert.AreEqual("false", cut.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        await cut.Find("[data-wizard-step=configuration]").ClickAsync();
        Assert.AreEqual("configuration", cut.Instance.CurrentStepId);
    }

    [TestMethod]
    public async Task FinalValidationCanReturnToAnIncompleteStepAndFocusIt()
    {
        using var context = Context();
        var steps = Steps();
        var cut = context.Render<CreationWizard>(p => p.Add(c => c.Steps, steps)
            .Add(c => c.OnFinish, (CreationWizardFinish finish) => finish.ReturnToStepId = "identity"));
        await cut.Find("[data-wizard-step=configuration]").ClickAsync();
        await cut.Find("[data-testid=creation-wizard-finish]").ClickAsync();
        Assert.AreEqual("identity", cut.Instance.CurrentStepId);
        Assert.IsFalse(cut.Find("[data-step-id=identity]").HasAttribute("hidden"));
        Assert.IsTrue(context.JSInterop.Invocations.Any(call => call.Identifier == "Blazor._internal.domWrapper.focus"));
    }

    private static CreationWizardStep[] Steps() =>
    [
        new("identity", "Identity", builder => { builder.OpenComponent<WizardProbe>(0); builder.CloseComponent(); }),
        new("configuration", "Configuration", builder => builder.AddMarkupContent(0, "<p>Configuration</p>"))
    ];
    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous;
        public CultureScope(string name)
        {
            previous = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
    public sealed class WizardProbe : ComponentBase
    {
        public string Value { get; private set; } = string.Empty;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "value", Value);
            builder.AddAttribute(2, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, args => Value = args.Value?.ToString() ?? string.Empty));
            builder.CloseElement();
        }
    }
}
