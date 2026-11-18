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
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
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

    private static BunitContext Context(Client client, bool create)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/configuration", "definition/access" })
        {
            var target = "http://localhost/parameters/saved?scopeRef=%2Finstance#" + section;
            module.Setup<string>("navigate", i => i.Arguments[0] is string uri && uri.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IParametersClient>(client);
        context.ComponentFactories.AddStub<DescendantUseGrantEditor>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(create ? "/parameters/new" : "/parameters/saved?scopeRef=%2Finstance");
        return context;
    }

    private sealed class Client : IParametersClient
    {
        public ParameterResource Current { get; private set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ParameterResourceKinds.Parameter,
            Metadata = new() { Name = "saved" },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = "Saved parameter", ValueType = ParameterValueType.Text, Value = JsonSerializer.SerializeToElement("saved value") }
        };
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? LastEtag { get; private set; }
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ParameterResource>>([Current]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true)]);
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scope, CancellationToken token) { Reads++; return Task.FromResult(new ResourceSnapshot<ParameterResource>(Current, "\"initial\"")); }
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
