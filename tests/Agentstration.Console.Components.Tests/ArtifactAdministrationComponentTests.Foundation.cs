using System.Net;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ArtifactAdministrationComponentTests
{
    [TestMethod]
    [DataRow("en-US", "Labels")]
    [DataRow("fr-FR", "Étiquettes")]
    public async Task DurableArtifactsUseTheSharedReadOnlyFormAndExportOnlyPublicMetadata(string culture, string labels)
    {
        using var locale = new TestCultureScope(culture); using var context = DurableContext(out var client);
        client.Durable = client.Durable with { Metadata = client.Durable.Metadata with { Tags = new Dictionary<string, string> { ["team"] = "fixture" } } };
        var cut = RenderDurable(context, client);
        Assert.Contains(labels, cut.Find(".resource-identity-summary").TextContent);
        Assert.Contains("team", cut.Find(".resource-identity-summary").TextContent);
        await DurableSectionAsync(context, cut, "definition/identity");
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(adapter.SectionState, form.SectionState);
        Assert.AreSame(adapter.SectionPresentations, form.Sections);
        Assert.IsTrue(form.ReadOnly);
        Assert.IsTrue(cut.Find("#durable-artifact-inspection-form > fieldset").HasAttribute("disabled"));
        Assert.IsFalse(form.OnValidSubmit.HasDelegate);
        Assert.AreEqual(7, adapter.SectionPresentations["definition/identity"].Fields.Count);
        await DurableSectionAsync(context, cut, "definition/configuration");
        Assert.Contains(client.Durable.Receipt.MediaType, cut.Find("#durable-artifact-inspection-form").TextContent);
        await DurableSectionAsync(context, cut, "yaml");
        Assert.Contains("flowRunArtifact", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(client.Durable.ArtifactId.ToString(), cut.Find(".resource-yaml-editor textarea").GetAttribute("value")!);
        Assert.IsTrue(cut.Find(".resource-yaml-editor textarea").HasAttribute("readonly"));
        Assert.DoesNotContain("opaque-reference", cut.Markup);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task ArtifactRefreshRetainsReadOnlyFormContentAndIndependentPolicyDraft()
    {
        using var context = DurableContext(out var client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = RenderDurable(context, client);
        await DurableSectionAsync(context, cut, "definition/identity");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        var editContext = form.EditContext;
        await DurableSectionAsync(context, cut, "content");
        var content = cut.FindComponent<DurableArtifactContent>().Instance;
        await DurableSectionAsync(context, cut, "access");
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(component => component.DraftChanged).InvokeAsync(true));
        client.DurableReadError = new AgentstrationApiException("Unavailable", "fixture", HttpStatusCode.ServiceUnavailable);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsTrue(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.HasContent);
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreSame(editContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreSame(content, cut.FindComponent<DurableArtifactContent>().Instance);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        client.DurableReadError = null;
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRetry.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.Ready, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreSame(editContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task LostArtifactAccessClearsMetadataYamlContentAndPolicy(HttpStatusCode status)
    {
        using var context = DurableContext(out var client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        client.Durable = client.Durable with { Receipt = client.Durable.Receipt with { Provenance = new Dictionary<string, string> { ["fileName"] = "private-artifact-filename" } } };
        var cut = RenderDurable(context, client);
        await DurableSectionAsync(context, cut, "definition/identity");
        await DurableSectionAsync(context, cut, "content");
        await DurableSectionAsync(context, cut, "yaml");
        await DurableSectionAsync(context, cut, "access");
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(component => component.DraftChanged).InvokeAsync(true));
        client.DurableReadError = new AgentstrationApiException("Denied", "fixture", status);

        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());

        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.AreEqual(status == HttpStatusCode.NotFound ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Forbidden, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.DoesNotContain("private-artifact-filename", cut.Markup);
        Assert.IsEmpty(cut.FindAll(".resource-form, .resource-yaml-editor, [data-artifact-section]"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task AReplacementArtifactUidCannotReplaceTheSavedInspection()
    {
        using var context = DurableContext(out var client); var cut = RenderDurable(context, client);
        client.Durable = client.Durable with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.AreEqual(ResourceLoadStatus.NotFound, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
    }

    [TestMethod]
    public async Task ADelayedArtifactReadCannotReplaceAnotherArtifact()
    {
        using var context = DurableContext(out var client); var cut = RenderDurable(context, client);
        var previous = client.Durable;
        var pending = new TaskCompletionSource<FlowRunArtifactResource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NextDurableRead = pending;
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.AreEqual(2, client.DurableReadCount));
        var nextId = FlowRunArtifactId.New();
        client.Durable = previous with { ArtifactId = nextId, Uid = Guid.NewGuid(), Metadata = previous.Metadata with { Name = nextId.ToString() } };
        cut.Render(parameters => parameters.Add(component => component.Id, nextId.ToString()));
        cut.WaitForAssertion(() => Assert.AreEqual(nextId, ((FlowRunArtifactResource)cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource!).ArtifactId));
        pending.SetResult(previous); await refresh;
        Assert.AreEqual(nextId, ((FlowRunArtifactResource)cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource!).ArtifactId);
        Assert.AreEqual(ResourceLoadStatus.Ready, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
    }

    private static BunitContext DurableContext(out ArtifactClient client)
    {
        var context = CreateContext();
        client = new ArtifactClient("public fixture content");
        client.Durable = client.Durable with { Uid = Guid.NewGuid() };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        context.Services.AddSingleton<IConsoleNavigationContext>(new ArtifactNavigationContext(client.Durable.ScopeRef!.Value.Value));
        return context;
    }
    private static IRenderedComponent<FlowRunArtifactDetails> RenderDurable(BunitContext context, ArtifactClient client) =>
        context.Render<FlowRunArtifactDetails>(parameters => parameters.Add(component => component.Id, client.Durable.ArtifactId.ToString()));
    private static Task DurableSectionAsync(BunitContext context, IRenderedComponent<FlowRunArtifactDetails> cut, string path) => cut.InvokeAsync(() => SelectSection(context, path));
}
