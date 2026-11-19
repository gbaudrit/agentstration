using System.Text.Json;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ParameterResourceScreenTests
{
    [TestMethod]
    public async Task CreationAllowsIncompleteNavigationRetainsValuesAndValidatesBeforeWriting()
    {
        var client = new Client();
        using var context = Context(client, true);
        var cut = context.Render<ParameterEditor>();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[data-testid=parameter-form]")));
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("configuration"));
        cut.Find("[data-testid=parameter-value]").Change("new value");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.AreEqual(0, client.Writes);
        cut.Find("[data-testid=parameter-display-name]").Change("New parameter");
        cut.Find("[data-testid=parameter-name]").Change("new-parameter");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("configuration"));
        Assert.AreEqual("new value", cut.Find("[data-testid=parameter-value]").GetAttribute("value"));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual(1, client.Writes);
        StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, "/parameters/new-parameter");
    }

    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task SavedOverviewDoesNotLeakDraftAndSectionsRetainTheFormAndExactUpdate(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        var client = new Client();
        using var context = Context(client, false);
        var cut = context.Render<ParameterEditor>(p => p.Add(c => c.Name, "saved"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".resource-identity-summary")));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        cut.Find("[data-testid=parameter-display-name]").Change("Draft parameter");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.Contains("Saved parameter", cut.Find(".resource-identity-summary").TextContent);
        Assert.DoesNotContain("Draft parameter", cut.Find(".resource-identity-summary").TextContent);
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual(1, client.Reads);
        await cut.Find("#parameter-resource-form").SubmitAsync();
        Assert.AreEqual("Draft parameter", client.Current.Definition.DisplayName);
        Assert.AreEqual("\"initial\"", client.LastEtag);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=parameter-value]").Change("unsaved value");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        cut.Find(".unsaved-changes-bar button[type=button]").Click();
        Assert.AreEqual("saved value", cut.Find("[data-testid=parameter-value]").GetAttribute("value"));
        Assert.AreEqual(1, client.Writes);
    }

    [TestMethod]
    public async Task InvalidHiddenIdentityRoutesBackBeforeAnyWriteAndUsesTheSharedState()
    {
        var client = new Client(); using var context = Context(client, false);
        var cut = context.Render<ParameterEditor>(p => p.Add(c => c.Name, "saved"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        cut.Find("[data-testid=parameter-display-name]").Change("");
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        await cut.Find("#parameter-resource-form").SubmitAsync();
        Assert.AreEqual("definition/identity", navigation.CurrentPath);
        Assert.AreEqual(0, client.Writes);
        Assert.AreSame(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionState, cut.FindComponent<ResourceDefinitionForm>().Instance.SectionState);
        Assert.AreSame(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionPresentations, cut.FindComponent<ResourceDefinitionForm>().Instance.Sections);
        Assert.IsTrue(cut.FindComponent<ResourceIdentityEditor>().Instance.LabelsReadOnly);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshKeepsAnInvalidVisitedDraftAndItsEtagUntilExplicitDiscard(bool discard)
    {
        var client = new Client(); using var context = Context(client, false);
        var cut = context.Render<ParameterEditor>(p => p.Add(c => c.Name, "saved"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        var pending = new TaskCompletionSource<ResourceSnapshot<ParameterResource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Loader = () => pending.Task;
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Refreshing, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status));
        Assert.IsFalse(cut.Find("[data-testid=parameter-value]").HasAttribute("disabled"));
        cut.Find("[data-testid=parameter-value-type]").Change(ParameterValueType.WholeNumber.ToString());
        cut.Find("[data-testid=parameter-value]").Change("not-an-integer");
        client.Current = client.Current with { Definition = client.Current.Definition with { DisplayName = "New server title" } };
        await cut.InvokeAsync(() => pending.SetResult(new(client.Current, "\"external\"")));
        await refresh;
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual("not-an-integer", cut.Find("[data-testid=parameter-value]").GetAttribute("value"));
        await cut.Find("#parameter-resource-form").SubmitAsync();
        Assert.AreEqual(0, client.Writes);
        Assert.IsNotNull(cut.Find(".validation-message"));
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.Contains("Saved parameter", cut.Find(".resource-identity-summary").TextContent);
        if (discard) cut.Find(".unsaved-changes-bar button[type=button]").Click();
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=parameter-value]").Change("5");
        await cut.Find("#parameter-resource-form").SubmitAsync();
        Assert.AreEqual(discard ? "\"external\"" : "\"initial\"", client.LastEtag);
        Assert.AreEqual(discard ? "New server title" : "Saved parameter", client.Current.Definition.DisplayName);
    }

    [TestMethod]
    public async Task ConsultationKeepsStandardDefinitionButCannotSubmitWithoutWritePermission()
    {
        var client = new Client { CanWrite = false }; using var context = Context(client, false);
        var cut = context.Render<ParameterEditor>(p => p.Add(c => c.Name, "saved"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        Assert.IsTrue(cut.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        Assert.IsNotNull(cut.Find(".resource-readonly-notice"));
        Assert.IsEmpty(cut.FindAll("[data-testid=parameter-delete]"));
        await cut.Find("#parameter-resource-form").SubmitAsync();
        Assert.AreEqual(0, client.Writes);
    }

    [TestMethod]
    public async Task RefreshCannotKeepThePrivateDraftWhenTheResourceWasReplaced()
    {
        var client = new Client(); using var context = Context(client, false);
        var cut = context.Render<ParameterEditor>(p => p.Add(c => c.Name, "saved"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=parameter-value]").Change("private pending value");
        client.Current = client.Current with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.NotFound, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsEmpty(cut.FindAll("[data-testid=parameter-form], .unsaved-changes-bar"));
        Assert.DoesNotContain("private pending value", cut.Markup);
        Assert.AreEqual(0, client.Writes);
    }

    private static BunitContext Context(Client client, bool create)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/identity", "definition/configuration", "definition/use-policy", "access", "yaml" })
        {
            var target = "http://localhost/parameters/saved?scopeRef=%2Finstance#" + section;
            module.Setup<string>("navigate", i => i.Arguments[0] is string uri && uri.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IParametersClient>(client);
        context.ComponentFactories.AddStub<DescendantUseGrantEditor>();
        context.ComponentFactories.AddStub<ResourceScopeFormField>();
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(create ? "/parameters/new" : "/parameters/saved?scopeRef=%2Finstance");
        return context;
    }

    private sealed class Client : IParametersClient
    {
        public ParameterResource Current { get; set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ParameterResourceKinds.Parameter,
            Metadata = new() { Name = "saved" },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = "Saved parameter", ValueType = ParameterValueType.Text, Value = JsonSerializer.SerializeToElement("saved value") }
        };
        public Func<Task<ResourceSnapshot<ParameterResource>>>? Loader { get; set; }
        public bool CanWrite { get; set; } = true;
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? LastEtag { get; private set; }
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ParameterResource>>([Current]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", CanWrite)]);
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scope, CancellationToken token) { Reads++; return Loader?.Invoke() ?? Task.FromResult(new ResourceSnapshot<ParameterResource>(Current, "\"initial\"")); }
        public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scope, CancellationToken token) => Task.FromResult(new ParameterUsagesResponse([], 0));
        public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken token)
        {
            Writes++; Current = Current with { Metadata = new() { Name = request.Name }, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<ParameterResource>(Current, "\"created\""));
        }
        public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scope, PutParameterRequest request, string etag, CancellationToken token)
        {
            Assert.AreEqual("saved", name); Assert.AreEqual(ResourceScopeRef.Instance, scope);
            Writes++; LastEtag = etag; Current = Current with { Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<ParameterResource>(Current, "\"updated\""));
        }
        public Task DeleteParameterAsync(string name, ResourceScopeRef scope, string etag, CancellationToken token) => Task.CompletedTask;
    }
}
