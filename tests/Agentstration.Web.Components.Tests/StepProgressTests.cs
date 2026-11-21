using System.Globalization;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class StepProgressTests
{
    [TestMethod]
    public async Task ViewportFollowingCanPauseForInspectionAndResumeAtTheNewActiveStep()
    {
        using var context = Context();
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps, Steps()).Add(x => x.ActiveStepId, "transfer").Add(x => x.FitToViewport, true));
        Assert.IsTrue(cut.Find("[data-step-progress]").ClassList.Contains("fit-viewport"));
        await cut.InvokeAsync(cut.Instance.PauseFollowingAsync);
        Assert.AreEqual("false", cut.Find("[data-step-progress]").GetAttribute("data-follow-active"));
        cut.Render(p => p.Add(x => x.ActiveStepId, "verify"));
        Assert.AreEqual("transfer", Panel(cut));
        cut.Find(".step-progress-follow").Click();
        Assert.AreEqual("verify", Panel(cut));
        Assert.AreEqual("true", cut.Find("[data-step-progress]").GetAttribute("data-follow-active"));
    }

    [TestMethod]
    public void SubstepsCollapseAndFollowWithoutChangingExecutionOrRenderingOtherFamilies()
    {
        using var context = Context();
        var steps = new ProgressStep[]
        {
            new("resources", "Resources", ProgressStepStatus.Running, builder => builder.AddContent(0, "Resource summary")),
            new("agents", "Agents", ProgressStepStatus.Running, builder => builder.AddContent(0, "Agent details")) { ParentId = "resources", Summary = "2/3" },
            new("flows", "Flows", ProgressStepStatus.Pending, builder => builder.AddContent(0, "Flow details")) { ParentId = "resources", Summary = "0/2" },
            new("ready", "Ready", ProgressStepStatus.Pending, builder => builder.AddContent(0, "Ready details"))
        };
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps, steps).Add(x => x.ActiveStepId, "agents"));
        Assert.AreEqual("agents", Panel(cut));
        Assert.AreEqual(2, cut.FindAll(".step-progress-row.is-child").Count);
        Assert.AreEqual(4, cut.FindAll("[role='tab']").Count);
        cut.Find(".step-progress-expand").Click();
        Assert.AreEqual("resources", Panel(cut));
        Assert.AreEqual(0, cut.FindAll(".step-progress-row.is-child").Count);
        Assert.AreEqual(2, cut.FindAll("[role='tab']").Count);
        cut.Find(".step-progress-follow").Click();
        Assert.AreEqual("agents", Panel(cut));
        Assert.AreEqual(2, cut.FindAll(".step-progress-row.is-child").Count);
        cut.Find("[data-step-id='agents']").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.AreEqual("flows", Panel(cut));
        Assert.AreEqual("step", cut.Find("[data-step-id='agents']").GetAttribute("aria-current"));
    }

    [TestMethod]
    public void CompletedGroupsCollapseOnceAndAnOperatorCanReopenThemWithoutPollingClosingThem()
    {
        using var context = Context();
        var steps = new ProgressStep[]
        {
            new("prepared", "Prepared", ProgressStepStatus.Succeeded, builder => builder.AddContent(0, "Prepared summary")),
            new("tenant", "Tenant", ProgressStepStatus.Succeeded, builder => builder.AddContent(0, "Tenant details")) { ParentId = "prepared" },
            new("resources", "Resources", ProgressStepStatus.Running, builder => builder.AddContent(0, "Current work"))
        };
        var initial = steps.Select(step => step with { Status = step.Id == "resources" ? ProgressStepStatus.Pending : ProgressStepStatus.Running }).ToArray();
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps, initial).Add(x => x.ActiveStepId, "tenant").Add(x => x.AutoCollapseCompletedGroups, true));
        Assert.AreEqual("tenant", Panel(cut));
        cut.Render(p => p.Add(x => x.Steps, steps));
        Assert.AreEqual("prepared", Panel(cut));
        Assert.AreEqual(0, cut.FindAll("[data-step-id='tenant']").Count);
        cut.Render(p => p.Add(x => x.ActiveStepId, "resources"));
        Assert.AreEqual(0, cut.FindAll("[data-step-id='tenant']").Count);
        cut.Find("[data-step-id='prepared']").Click();
        Assert.AreEqual(1, cut.FindAll("[data-step-id='tenant']").Count);
        cut.Render(p => p.Add(x => x.Steps, steps.ToArray()));
        Assert.AreEqual(1, cut.FindAll("[data-step-id='tenant']").Count);
        cut.Find("[data-step-id='tenant']").Click();
        Assert.AreEqual("tenant", Panel(cut));
        cut.Find(".step-progress-expand").Click();
        Assert.AreEqual("prepared", Panel(cut));
        Assert.AreEqual(0, cut.FindAll("[data-step-id='tenant']").Count);
    }

    [TestMethod]
    public void ActiveStepSelectsItsContentAndKeepsSelectionSeparateFromExecution()
    {
        using var context = Context();
        var selections = new List<string?>();
        var cut = context.Render<StepProgress>(p => p
            .Add(x => x.Steps, Steps())
            .Add(x => x.ActiveStepId, "transfer")
            .Add(x => x.SelectedStepIdChanged, selections.Add));
        Assert.AreEqual("transfer", Panel(cut));
        Assert.AreEqual("step", cut.Find("[data-step-id='transfer']").GetAttribute("aria-current"));
        Assert.AreEqual("true", cut.Find("[data-step-id='transfer']").GetAttribute("aria-selected"));
        Assert.AreEqual("Transfer details", cut.Find("[role='tabpanel']").TextContent);
        CollectionAssert.AreEqual(new string?[] { "transfer" }, selections);

        cut.Find("[data-step-id='prepare']").Click();
        Assert.AreEqual("prepare", Panel(cut));
        Assert.AreEqual("step", cut.Find("[data-step-id='transfer']").GetAttribute("aria-current"));
        Assert.AreEqual("false", cut.Find("[data-step-id='transfer']").GetAttribute("aria-selected"));
        cut.Render(p => p.Add(x => x.ActiveStepId, "verify"));
        Assert.AreEqual("prepare", Panel(cut), "A background update must not steal the inspected step.");
        cut.Find(".step-progress-follow").Click();
        Assert.AreEqual("verify", Panel(cut));
        cut.Render(p => p.Add(x => x.ActiveStepId, "transfer"));
        Assert.AreEqual("transfer", Panel(cut));
    }

    [TestMethod]
    public async Task BoundSelectionAndFollowModeRoundTripThroughARealParent()
    {
        using var context = Context();
        var cut = context.Render<BoundProgress>();
        Assert.AreEqual("transfer", cut.Instance.Selection);
        cut.Find("[data-step-id='prepare']").Click();
        Assert.AreEqual("prepare", cut.Instance.Selection);
        Assert.IsFalse(cut.Instance.Follow);
        await cut.InvokeAsync(() => cut.Instance.MoveTo("verify"));
        Assert.AreEqual("prepare", cut.Instance.Selection);
        cut.Find(".step-progress-follow").Click();
        Assert.AreEqual("verify", cut.Instance.Selection);
        Assert.IsTrue(cut.Instance.Follow);
        await cut.InvokeAsync(() => cut.Instance.MoveTo("transfer"));
        Assert.AreEqual("transfer", cut.Instance.Selection);
        Assert.IsTrue(cut.Instance.Follow);
        await cut.InvokeAsync(() => cut.Instance.MoveTo("verify"));
        Assert.AreEqual("verify", cut.Instance.Selection);
        await cut.InvokeAsync(() => cut.Instance.Inspect("prepare"));
        Assert.AreEqual("prepare", cut.Find("[role='tabpanel']").GetAttribute("data-selected-step"));
        Assert.IsFalse(cut.Instance.Follow);
        await cut.InvokeAsync(() => cut.Instance.Inspect("missing"));
        Assert.AreEqual("verify", cut.Instance.Selection, "A stale parent selection is normalized to an available step.");
        await cut.InvokeAsync(() => cut.Instance.Inspect("missing-again"));
        Assert.AreEqual("verify", cut.Instance.Selection, "Normalize the binding even when the fallback pane is already visible.");
    }

    [TestMethod]
    public void AutomaticFollowCanBeDisabledAndEnabledByTheParent()
    {
        using var context = Context();
        var cut = context.Render<StepProgress>(p => p
            .Add(x => x.Steps, Steps()).Add(x => x.ActiveStepId, "prepare")
            .Add(x => x.FollowActiveStep, false));
        cut.Render(p => p.Add(x => x.ActiveStepId, "transfer"));
        Assert.AreEqual("prepare", Panel(cut));
        cut.Render(p => p.Add(x => x.FollowActiveStep, true));
        Assert.AreEqual("transfer", Panel(cut));
    }

    [TestMethod]
    public void ReplacedAndReorderedStepsPreserveTheSelectedIdentityAndRefreshContent()
    {
        using var context = Context();
        var cut = context.Render<StepProgress>(p => p
            .Add(x => x.Steps, Steps()).Add(x => x.ActiveStepId, "transfer"));
        cut.Find("[data-step-id='prepare']").Click();
        var replacement = new[] { Steps()[2], Steps()[0] with { Content = Text("Updated preparation") }, Steps()[1] };
        cut.Render(p => p.Add(x => x.Steps, replacement));
        Assert.AreEqual("prepare", Panel(cut));
        Assert.AreEqual("Updated preparation", cut.Find("[role='tabpanel']").TextContent);
        Assert.AreEqual("true", cut.Find("[data-step-id='prepare']").GetAttribute("aria-selected"));
    }

    [TestMethod]
    public void RemovedOrDisabledSelectionFallsBackAndEmptyListsClearTheBinding()
    {
        using var context = Context();
        string? selection = null;
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps, Steps())
            .Add(x => x.ActiveStepId, "transfer").Add(x => x.SelectedStepIdChanged, value => selection = value));
        cut.Find("[data-step-id='prepare']").Click();
        cut.Render(p => p.Add(x => x.Steps, Steps().Skip(1).ToArray()));
        Assert.AreEqual("transfer", Panel(cut));
        cut.Render(p => p.Add(x => x.Steps, new[] { Steps()[1] with { Selectable = false }, Steps()[2] }));
        Assert.AreEqual("verify", Panel(cut));
        cut.Render(p => p.Add(x => x.Steps, Array.Empty<ProgressStep>()));
        Assert.IsNull(selection);
        Assert.IsEmpty(cut.FindAll("[role='tabpanel']"));
        StringAssert.Contains(cut.Markup, "No steps to display.");
    }

    [TestMethod]
    public void MissingActiveAndSelectedIdsUseTheFirstSelectableStep()
    {
        using var context = Context();
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps, Steps())
            .Add(x => x.ActiveStepId, "unavailable").Add(x => x.SelectedStepId, "stale"));
        Assert.AreEqual("prepare", Panel(cut));
        cut.Render(p => p.Add(x => x.Steps, Steps().Select(step => step with { Selectable = false }).ToArray()));
        Assert.IsEmpty(cut.FindAll("[role='tabpanel']"));
        Assert.AreEqual("No step is available for inspection.", cut.Find("[role='region']").TextContent);
    }

    [TestMethod]
    public void KeyboardNavigationSkipsDisabledStepsWrapsAndLinksTheVisiblePanel()
    {
        using var context = Context();
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps,
            new[] { Steps()[0], Steps()[1] with { Selectable = false }, Steps()[2] }));
        cut.Find("[data-step-id='prepare']").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.AreEqual("verify", Panel(cut));
        cut.Find("[data-step-id='verify']").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.AreEqual("prepare", Panel(cut));
        cut.Find("[data-step-id='prepare']").KeyDown(new KeyboardEventArgs { Key = "End" });
        Assert.AreEqual("verify", Panel(cut));
        cut.Find("[data-step-id='verify']").KeyDown(new KeyboardEventArgs { Key = "Home" });
        Assert.AreEqual("prepare", Panel(cut));
        cut.Find("[data-step-id='prepare']").KeyDown(new KeyboardEventArgs { Key = "ArrowUp", CtrlKey = true });
        Assert.AreEqual("prepare", Panel(cut));
        var tab = cut.Find("[aria-selected='true']");
        Assert.AreEqual(tab.Id, cut.Find("[role='tabpanel']").GetAttribute("aria-labelledby"));
        Assert.AreEqual(cut.Find("[role='tabpanel']").Id, tab.GetAttribute("aria-controls"));
        Assert.HasCount(1, cut.FindAll("[role='tab'][tabindex='0']"));
    }

    [TestMethod]
    public void ImmutableUpdatesKeepPanelStateButDifferentStepsDoNotShareChildInstances()
    {
        using var context = Context();
        RenderFragment content = builder =>
        {
            builder.OpenComponent<DraftPanel>(0);
            builder.CloseComponent();
        };
        var steps = new[]
        {
            new ProgressStep("one", "One", ProgressStepStatus.Running, content),
            new ProgressStep("two", "Two", ProgressStepStatus.Pending, content)
        };
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps, steps));
        var first = cut.FindComponent<DraftPanel>().Instance;
        cut.Find("input").Input("retained draft");
        cut.Render(p => p.Add(x => x.Steps, new[] { steps[1], steps[0] with { Summary = "Fresh observation" } }));
        Assert.AreSame(first, cut.FindComponent<DraftPanel>().Instance);
        Assert.AreEqual("retained draft", cut.Find("input").GetAttribute("value"));
        cut.Find("[data-step-id='two']").Click();
        Assert.AreNotSame(first, cut.FindComponent<DraftPanel>().Instance);
        Assert.AreEqual("initial", cut.Find("input").GetAttribute("value"));
    }

    [TestMethod]
    public void RightPaneAcceptsArbitraryInteractiveContentWithoutExecutingOtherSteps()
    {
        using var context = Context();
        var actions = 0;
        RenderFragment content = builder =>
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, () => actions++));
            builder.AddContent(2, "Inspect import");
            builder.CloseElement();
        };
        var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps,
            new[] { new ProgressStep("import", "Import", ProgressStepStatus.Waiting, content), Steps()[0] }));
        cut.Find("[role='tabpanel'] button").Click();
        Assert.AreEqual(1, actions);
        cut.Find("[data-step-id='prepare']").Click();
        Assert.AreEqual(1, actions, "Changing the inspected panel must not invoke a business action.");
        Assert.AreEqual("Preparation details", cut.Find("[role='tabpanel']").TextContent);
    }

    [TestMethod]
    [DataRow("en-US", "Preparation steps", "Waiting", "Follow the current step")]
    [DataRow("fr-FR", "Étapes de préparation", "En attente", "Suivre l’étape en cours")]
    public void LabelsAndStatusesAreLocalized(string culture, string title, string waiting, string follow)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var context = Context();
            var cut = context.Render<StepProgress>(p => p.Add(x => x.Steps,
                new[] { Steps()[0], Steps()[1] with { Status = ProgressStepStatus.Waiting } }).Add(x => x.ActiveStepId, "transfer"));
            Assert.AreEqual(title, cut.Find("h2").TextContent);
            Assert.AreEqual(waiting, cut.Find("[data-step-id='transfer'] .step-progress-state").TextContent);
            cut.Find("[data-step-id='prepare']").Click();
            Assert.AreEqual(follow, cut.Find(".step-progress-follow").TextContent);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    public void EveryStatusIsDistinctAndInstanceAriaIdsAreIsolated()
    {
        using var context = Context();
        var steps = Enum.GetValues<ProgressStepStatus>().Select(status =>
            new ProgressStep(status.ToString(), status.ToString(), status, Text(status.ToString()))).ToArray();
        var first = context.Render<StepProgress>(p => p.Add(x => x.Steps, steps));
        var second = context.Render<StepProgress>(p => p.Add(x => x.Steps, steps));
        Assert.AreNotEqual(first.Find("[role='tabpanel']").Id, second.Find("[role='tabpanel']").Id);
        Assert.AreEqual(8, first.FindAll(".step-progress-state").Select(element => element.TextContent).Distinct().Count());
    }

    [TestMethod]
    public void InvalidStepDescriptorsAreRejected()
    {
        using var context = Context();
        Assert.Throws<ArgumentException>(() => context.Render<StepProgress>(p => p.Add(x => x.Steps, new[] { Steps()[0], Steps()[0] })));
        Assert.Throws<ArgumentException>(() => context.Render<StepProgress>(p => p.Add(x => x.Steps,
            new[] { Steps()[0] with { Id = " " } })));
        Assert.Throws<ArgumentException>(() => context.Render<StepProgress>(p => p.Add(x => x.Steps,
            new[] { Steps()[0] with { Status = (ProgressStepStatus)999 } })));
    }

    private static string? Panel(IRenderedComponent<StepProgress> cut) => cut.Find("[role='tabpanel']").GetAttribute("data-selected-step");
    private static RenderFragment Text(string value) => builder => builder.AddContent(0, value);
    private static ProgressStep[] Steps() =>
    [
        new("prepare", "Prepare", ProgressStepStatus.Succeeded, Text("Preparation details")),
        new("transfer", "Transfer", ProgressStepStatus.Running, Text("Transfer details")),
        new("verify", "Verify", ProgressStepStatus.Pending, Text("Verification details"))
    ];

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/step-progress.js");
        module.Mode = JSRuntimeMode.Loose;
        return context;
    }

    public sealed class BoundProgress : ComponentBase
    {
        public string? Selection { get; private set; }
        public bool Follow { get; private set; } = true;
        private string active = "transfer";
        public void MoveTo(string step) { active = step; StateHasChanged(); }
        public void Inspect(string step) { Selection = step; StateHasChanged(); }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<StepProgress>(0);
            builder.AddAttribute(1, nameof(StepProgress.Steps), Steps());
            builder.AddAttribute(2, nameof(StepProgress.ActiveStepId), active);
            builder.AddAttribute(3, nameof(StepProgress.SelectedStepId), Selection);
            builder.AddAttribute(4, nameof(StepProgress.SelectedStepIdChanged), EventCallback.Factory.Create<string?>(this, value => Selection = value));
            builder.AddAttribute(5, nameof(StepProgress.FollowActiveStep), Follow);
            builder.AddAttribute(6, nameof(StepProgress.FollowActiveStepChanged), EventCallback.Factory.Create<bool>(this, value => Follow = value));
            builder.CloseComponent();
        }
    }

    public sealed class DraftPanel : ComponentBase
    {
        private string? draft = "initial";
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "value", draft);
            builder.AddAttribute(2, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, value => draft = value.Value?.ToString()));
            builder.CloseElement();
        }
    }
}
