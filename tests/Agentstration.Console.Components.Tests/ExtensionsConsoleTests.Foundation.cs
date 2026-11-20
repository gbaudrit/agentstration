using System.Net;
using Agentstration.Extensions.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ExtensionsConsoleTests
{
    private static void ConfigureExtensionNavigation(BunitContext context)
    {
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri);
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "enrollment", "contributions", "resources", "options", "usages", "access", "yaml" })
            module.Setup<string>("navigate", call => call.Arguments[0] is string target && target.EndsWith("#" + path, StringComparison.Ordinal)).SetResult(navigation.Uri.Split('#')[0] + "#" + path);
    }
    private static Task SelectExtensionSectionAsync(IRenderedComponent<ExtensionDetails> page, string path) =>
        page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
    private static async Task<BunitContext> RegistrationContext(RegistrationFoundationClient client, string? route = null, ConsoleContextState? contextState = null, bool loadContext = true)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        if (contextState is not null) context.Services.AddSingleton(contextState);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(route ?? "/extensions/manual?namespace=team&scopeRef=%2Finstance&context=%2Finstance");
        ConfigureExtensionNavigation(context);
        if (loadContext) await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(default);
        return context;
    }
    private static IRenderedComponent<ExtensionDetails> RenderRegistration(BunitContext context) => context.Render<ExtensionDetails>(p => p.Add(c => c.RegistrationName, "manual"));

    [TestMethod]
    public async Task ExtensionRegistrationUsesExactSavedIdentityAndTheLayoutsStateAndDeclarations()
    {
        var client = new RegistrationFoundationClient();
        using var context = await RegistrationContext(client);
        var page = RenderRegistration(context);
        Assert.Contains("Saved registration", page.Find(".resource-header").TextContent);
        var identity = page.FindComponent<ResourceIdentitySummary>().Instance;
        Assert.AreEqual("manual", identity.TechnicalName);
        Assert.AreEqual("team", identity.Namespace);
        Assert.AreEqual("manual", page.Find("[data-testid='extension-identity-summary'] .copyable-value code").TextContent);
        Assert.AreEqual("team/manual", page.Find(".resource-header-technical code").TextContent);
        var copy = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js").SetupVoid("copyText", "team/manual");
        copy.SetVoidResult();
        await page.Find(".resource-header .copy-value").ClickAsync(new());
        Assert.HasCount(1, copy.Invocations);
        Assert.Contains("fixture", page.Find("[data-testid='extension-identity-summary']").TextContent);
        await SelectExtensionSectionAsync(page, "definition/identity");
        var layout = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        var form = page.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(layout.SectionState, form.SectionState);
        Assert.AreSame(layout.SectionPresentations, form.Sections);
        Assert.HasCount(1, page.FindAll("[data-testid='extension-identity'] [data-testid='labels-editor']"));
        Assert.IsEmpty(page.FindAll("[data-testid='extension-identity'] [data-testid='label-add']"));
        Assert.AreEqual(client.Snapshot.Value.ScopeRef, client.ReadOwner);
        Assert.AreEqual(client.Snapshot.Value.Uid, layout.Resource!.Uid);
    }

    [TestMethod]
    public async Task ExtensionRegistrationRetainsInvalidFormAndActivationAcknowledgesOnlyEnablement()
    {
        var client = new RegistrationFoundationClient();
        using var context = await RegistrationContext(client);
        var page = RenderRegistration(context);
        await SelectExtensionSectionAsync(page, "definition/identity");
        page.Find("[data-testid='extension-display-name']").Input("Pending title");
        await SelectExtensionSectionAsync(page, "definition/configuration");
        page.Find("[data-testid='extension-endpoint']").Input("");
        await page.Find("#extension-registration-form").SubmitAsync();
        var edit = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        Assert.IsNotEmpty(edit.GetValidationMessages());
        await page.Find("[data-testid='extension-enabled']").ChangeAsync(false);
        Assert.IsFalse(client.LastWrite!.Properties.Enabled);
        Assert.AreEqual("Saved registration", client.LastWrite.Properties.DisplayName);
        Assert.AreEqual("https://extension.example.test/", client.LastWrite.Properties.Endpoint.AbsoluteUri);
        Assert.AreEqual("Pending title", page.Find("[data-testid='extension-display-name']").GetAttribute("value"));
        Assert.AreEqual(string.Empty, page.Find("[data-testid='extension-endpoint']").GetAttribute("value"));
        Assert.AreSame(edit, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsNotEmpty(edit.GetValidationMessages());
        Assert.HasCount(1, page.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task ExtensionRegistrationRefreshBuffersNewSnapshotUntilExplicitDiscard()
    {
        var client = new RegistrationFoundationClient();
        using var context = await RegistrationContext(client);
        var page = RenderRegistration(context);
        await SelectExtensionSectionAsync(page, "definition/identity");
        page.Find("[data-testid='extension-display-name']").Input("Local pending");
        var edit = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        client.Snapshot = new(client.Snapshot.Value with { Generation = 2, Definition = client.Snapshot.Value.Definition with { DisplayName = "New saved title" } }, "\"server-newer\"");
        await page.Find("[data-testid='extension-refresh']").ClickAsync(new());
        Assert.AreEqual("Local pending", page.Find("[data-testid='extension-display-name']").GetAttribute("value"));
        Assert.AreSame(edit, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.Contains("Saved registration", page.Find(".resource-header").TextContent);
        await page.Find(".unsaved-changes-bar .text-button").ClickAsync(new());
        Assert.AreEqual("New saved title", page.Find("[data-testid='extension-display-name']").GetAttribute("value"));
        Assert.IsEmpty(page.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task ExtensionRegistrationFailedWriteKeepsDraftAndOriginalEtag()
    {
        var client = new RegistrationFoundationClient { WriteError = new AgentstrationApiException("Rejected", "fixture-rejected", HttpStatusCode.UnprocessableEntity) };
        using var context = await RegistrationContext(client);
        var page = RenderRegistration(context);
        await SelectExtensionSectionAsync(page, "definition/identity");
        page.Find("[data-testid='extension-display-name']").Input("Keep rejected edit");
        await page.Find("#extension-registration-form").SubmitAsync();
        Assert.AreEqual("\"saved-1\"", client.LastEtag);
        Assert.AreEqual("Keep rejected edit", page.Find("[data-testid='extension-display-name']").GetAttribute("value"));
        Assert.Contains("Rejected", page.Find("[role=alert]").TextContent);
        Assert.HasCount(1, page.FindAll(".unsaved-changes-bar"));
        client.WriteError = null;
        await page.Find("#extension-registration-form").SubmitAsync();
        Assert.AreEqual("\"saved-1\"", client.LastEtag);
        Assert.AreEqual("Keep rejected edit", client.Snapshot.Value.Definition.DisplayName);
        Assert.IsEmpty(page.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task ExtensionRegistrationReadLossClearsPrivateIdentityAndDraft(HttpStatusCode status)
    {
        var client = new RegistrationFoundationClient();
        using var context = await RegistrationContext(client);
        var page = RenderRegistration(context);
        await SelectExtensionSectionAsync(page, "definition/identity");
        page.Find("[data-testid='extension-display-name']").Input("Private draft");
        client.ReadError = new AgentstrationApiException("Unavailable", "fixture-loss", status);
        await page.Find("[data-testid='extension-refresh']").ClickAsync(new());
        Assert.IsEmpty(page.FindAll("[data-testid='extension-display-name']"));
        Assert.IsEmpty(page.FindAll(".unsaved-changes-bar"));
        Assert.DoesNotContain("Private draft", page.Markup);
        Assert.DoesNotContain("Saved registration", page.Markup);
        Assert.IsEmpty(page.FindAll("[data-testid='extension-enabled']"));
    }


    [TestMethod]
    public async Task EnrollmentRouteKeepsItsOwnSavedIdentityAndOmitsPrivateVerifierFieldsFromYaml()
    {
        var instance = Guid.NewGuid();
        var request = new AepEnrollmentRequestResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.AepEnrollmentRequest, Uid = Guid.NewGuid(), ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = instance.ToString("N") }, Generation = 2,
            Definition = new() { InstanceId = instance, TargetScopeRef = ResourceScopeRef.Instance, ExtensionId = "example.extension", ExtensionName = "Saved enrollment identity", ExtensionVersion = "1.2.3",
                Endpoint = new("https://extension.example.test/"), State = AepEnrollmentState.Available, AnnouncedAt = DateTimeOffset.UnixEpoch,
                RegistrationName = "manual", CodeSalt = "PRIVATE-SALT", CodeDigest = "PRIVATE-DIGEST", CompletionDigest = "PRIVATE-COMPLETION", CredentialSecretName = "PRIVATE-CREDENTIAL-NAME" }
        };
        var client = new RegistrationFoundationClient { Enrollment = request };
        using var context = await RegistrationContext(client, $"/extensions/enrollment/{instance:D}?context=%2Finstance");
        var page = context.Render<ExtensionDetails>(p => p.Add(c => c.EnrollmentInstanceId, instance));
        var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreEqual(request.Uid, frame.Resource!.Uid);
        Assert.AreNotEqual(client.Snapshot.Value.Uid, frame.Resource.Uid);
        Assert.Contains("Saved enrollment identity", page.Find(".resource-header").TextContent);
        Assert.IsEmpty(page.FindAll("[data-testid='extension-enabled']"));
        Assert.IsFalse(frame.AllowAccess);
        await SelectExtensionSectionAsync(page, "definition/identity");
        Assert.AreEqual("Saved enrollment identity", page.Find("[data-testid='extension-display-name']").GetAttribute("value"));
        Assert.IsTrue(page.Find("#extension-registration-form > fieldset").HasAttribute("disabled"));
        await SelectExtensionSectionAsync(page, "yaml");
        var yaml = frame.Yaml;
        Assert.Contains("example.extension", yaml);
        foreach (var value in new[] { "PRIVATE-SALT", "PRIVATE-DIGEST", "PRIVATE-COMPLETION", "PRIVATE-CREDENTIAL-NAME" }) Assert.DoesNotContain(value, yaml);
        Assert.IsNull(client.LastWrite);
    }

    [TestMethod]
    public async Task ExtensionRegistrationRendersAfterDeferredContextAndOwningReadComplete()
    {
        var client = new RegistrationFoundationClient();
        var inventory = await client.GetExtensionInventoryAsync(default);
        var pending = new TaskCompletionSource<IReadOnlyList<ExtensionInventoryItemResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.InventoryLoader = () => { readStarted.TrySetResult(); return pending.Task; };
        var snapshot = new TaskCompletionSource<ConsoleContextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ConsoleContextState(new DeferredContextProvider(snapshot.Task));
        using var context = await RegistrationContext(client, contextState: state, loadContext: false);
        var page = RenderRegistration(context);
        Assert.AreEqual(ResourceLoadStatus.Loading, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        snapshot.SetResult(await new WritableContextProvider().GetAsync(default));
        page.WaitForAssertion(() => Assert.IsTrue(readStarted.Task.IsCompleted));
        pending.SetResult(inventory);
        page.WaitForAssertion(() =>
        {
            Assert.AreEqual(ResourceLoadStatus.Ready, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
            Assert.Contains("Saved registration", page.Find(".resource-header").TextContent);
            Assert.IsEmpty(page.FindAll("[aria-busy='true']"));
        });
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden)]
    [DataRow(HttpStatusCode.InternalServerError, ResourceLoadStatus.Failed)]
    public async Task ExtensionRegistrationKeepsAuthorizedIdentityWhenAssociatedEnrollmentReadFails(HttpStatusCode status, ResourceLoadStatus expected)
    {
        var client = new RegistrationFoundationClient
        {
            Enrollment = RegistrationEnrollment(Guid.NewGuid()),
            EnrollmentError = new AgentstrationApiException("Private enrollment transport detail", "fixture-enrollment", status)
        };
        using var context = await RegistrationContext(client);
        var page = RenderRegistration(context);
        Assert.AreEqual(ResourceLoadStatus.Ready, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.AreEqual(client.Snapshot.Value.Uid, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource!.Uid);
        await SelectExtensionSectionAsync(page, "enrollment");
        Assert.AreEqual(expected, page.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private enrollment transport detail", page.Markup);
        Assert.IsEmpty(page.FindAll("[aria-busy='true']"));
        client.EnrollmentError = null;
        await page.Find("[data-testid='extension-refresh']").ClickAsync(new());
        Assert.IsEmpty(page.FindComponents<ResourceLoadFeedback>());
        Assert.Contains(client.Enrollment!.Definition.InstanceId.ToString(), page.Markup);
    }

    [TestMethod]
    public async Task DirectExtensionEnrollmentReadDenialRemainsBlockingAndPurgesPrivateContent()
    {
        var instance = Guid.NewGuid();
        var client = new RegistrationFoundationClient
        {
            Enrollment = RegistrationEnrollment(instance),
            EnrollmentError = new AgentstrationApiException("Private enrollment detail", "fixture-enrollment", HttpStatusCode.Forbidden)
        };
        using var context = await RegistrationContext(client, $"/extensions/enrollment/{instance:D}?context=%2Finstance");
        var page = context.Render<ExtensionDetails>(p => p.Add(c => c.EnrollmentInstanceId, instance));
        var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreEqual(ResourceLoadStatus.Forbidden, frame.LoadState!.Status);
        Assert.IsNull(frame.Resource);
        Assert.IsEmpty(page.FindComponents<ResourceIdentitySummary>());
        Assert.IsEmpty(page.FindAll("[data-testid='extension-enabled'], .unsaved-changes-bar"));
    }

    private static AepEnrollmentRequestResource RegistrationEnrollment(Guid instance) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.AepEnrollmentRequest, Uid = Guid.NewGuid(), ScopeRef = ResourceScopeRef.Instance,
        Metadata = new() { Name = instance.ToString("N"), Namespace = ResourceNamespace.Parse("team") }, Generation = 1,
        Definition = new()
        {
            InstanceId = instance, TargetScopeRef = ResourceScopeRef.Instance, ExtensionId = "example.extension", ExtensionName = "Saved enrollment identity", ExtensionVersion = "1.2.3",
            Endpoint = new("https://extension.example.test/"), State = AepEnrollmentState.Available, AnnouncedAt = DateTimeOffset.UnixEpoch, RegistrationName = "manual"
        }
    };

    private sealed class RegistrationFoundationClient : IExtensionsClient
    {
        public ResourceSnapshot<ExtensionRegistrationResource> Snapshot { get; set; } = new(new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.ExtensionRegistration,
            ScopeRef = ResourceScopeRef.Instance, Generation = 1, Uid = Guid.NewGuid(),
            Metadata = new() { Name = "manual", Namespace = ResourceNamespace.Parse("team"), Tags = new Dictionary<string,string> { ["owner"] = "fixture" } },
            Definition = new() { DisplayName = "Saved registration", Endpoint = new("https://extension.example.test/"), Enabled = true, Source = ExtensionRegistrationSource.Manual }
        }, "\"saved-1\"");
        public AepEnrollmentRequestResource? Enrollment { get; set; }
        public Exception? EnrollmentError { get; set; }
        public Func<Task<IReadOnlyList<ExtensionInventoryItemResponse>>>? InventoryLoader { get; set; }
        public Task<IReadOnlyList<AepEnrollmentRequestResource>> GetEnrollmentsAsync(CancellationToken cancellationToken) => EnrollmentError is { } error
            ? Task.FromException<IReadOnlyList<AepEnrollmentRequestResource>>(error) : Task.FromResult<IReadOnlyList<AepEnrollmentRequestResource>>(Enrollment is { } request ? [request] : []);
        public Exception? ReadError { get; set; }
        public Exception? WriteError { get; set; }
        public PutExtensionRegistrationRequest? LastWrite { get; private set; }
        public string? LastEtag { get; private set; }
        public ResourceScopeRef? ReadOwner { get; private set; }
        public Task<IReadOnlyList<ExtensionInventoryItemResponse>> GetExtensionInventoryAsync(CancellationToken cancellationToken)
        {
            if (ReadError is { } error) return Task.FromException<IReadOnlyList<ExtensionInventoryItemResponse>>(error);
            if (InventoryLoader is { } loader) return loader();
            return Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([new("manual", "manual", "team", ResourceScopeRef.Instance, Enrollment?.Definition.InstanceId,
                "Observed extension", "example.extension", "1.2.3", Snapshot.Value.Definition.Endpoint, "manual", Snapshot.Value.Definition.Enabled, "available", null, null, null, [])]);
        }
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExtensionResponse>>([]);
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([Snapshot.Value]);
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) { ReadOwner = scopeRef; return Task.FromResult(Snapshot); }
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken)
        {
            LastWrite = request; LastEtag = etag;
            if (WriteError is { } error) return Task.FromException<ResourceSnapshot<ExtensionRegistrationResource>>(error);
            Snapshot = new(Snapshot.Value with { Definition = request.Properties, Generation = Snapshot.Value.Generation + 1 }, "\"ack-2\""); return Task.FromResult(Snapshot);
        }
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
