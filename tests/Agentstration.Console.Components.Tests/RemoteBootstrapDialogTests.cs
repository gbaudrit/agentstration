using Agentstration.Web.Components.Bootstrap;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class RemoteBootstrapDialogTests
{
    [TestMethod]
    [DataRow("en-US", "Remote bootstrapping", "Instance")]
    [DataRow("fr-FR", "Bootstraping distant", "Tenant")]
    public void HostSuppliesTheTargetWithoutAWorkspaceAssumption(string cultureName, string title, string target)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = Context();
        var cut = context.Render<RemoteBootstrapDialog>(p => p.Add(x => x.IsOpen, true)
            .Add(x => x.TargetSelector, $"<p>{target}</p>"));
        Assert.AreEqual(title, cut.Find("h2").TextContent);
        Assert.Contains(target, cut.Markup);
        Assert.HasCount(0, cut.FindAll("select"));
        Assert.IsTrue(cut.Find("button.button-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    [DataRow("en-US", "Choose a ZIP package", "No package selected", ".zip file · up to 4 MiB")]
    [DataRow("fr-FR", "Choisir un paquet ZIP", "Aucun paquet sélectionné", "Fichier .zip · 4 Mio maximum")]
    public void ArchiveCardLocalizesItsActionAndKeepsTheNativeInputLabelled(string cultureName, string action, string empty, string limit)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = Context();
        var cut = context.Render<RemoteBootstrapDialog>(p => p.Add(x => x.IsOpen, true)
            .Add(x => x.TargetSelector, "<p>Instance</p>"));
        Assert.Contains(action, cut.Find(".remote-bootstrap-upload").TextContent);
        Assert.Contains(empty, cut.Find(".remote-bootstrap-upload").TextContent);
        var input = cut.Find("input[type='file']");
        Assert.HasCount(1, cut.FindAll($"label[for='{input.Id}']"));
        Assert.AreEqual(limit, cut.Find($"[id='{input.GetAttribute("aria-describedby")}']").TextContent);
        Assert.AreEqual(".zip,application/zip", input.GetAttribute("accept"));
    }

    [TestMethod]
    public void InspectionRequiresExactlyOneInputAndDoesNotClearTheDraftOnFailure()
    {
        using var context = Context();
        var requests = new List<RemoteBootstrapInput>();
        var cut = context.Render<RemoteBootstrapDialog>(p => p.Add(x => x.IsOpen, true)
            .Add(x => x.TargetSelector, "<p>Instance</p>").Add(x => x.CanInspect, true)
            .Add(x => x.OnInspect, input => requests.Add(input)));
        cut.Find("button.button-primary").Click();
        Assert.IsEmpty(requests);
        Assert.HasCount(1, cut.FindAll("[role='alert']"));
        cut.Find("textarea").Change("formatVersion: '2.0'");
        cut.Find("input[type='url']").Change("https://fixture.example/seed.yaml");
        cut.Find("button.button-primary").Click();
        Assert.IsEmpty(requests);
        cut.Find("input[type='url']").Change("");
        cut.Find("button.button-primary").Click();
        cut.Render(p => p.Add(x => x.Error, "seed_prerequisite_access_denied"));
        Assert.Contains("seed_prerequisite_access_denied", cut.Find("[role='alert']").TextContent);
        Assert.AreEqual("formatVersion: '2.0'", cut.Find("textarea").GetAttribute("value") ?? cut.Find("textarea").TextContent);
        cut.Find("button.button-primary").Click();
        Assert.HasCount(2, requests);
        Assert.AreEqual(requests[0], requests[1]);
        Assert.IsNull(requests[0].ArchiveBase64);
        Assert.IsNull(requests[0].DocumentUrl);
    }

    [TestMethod]
    public void ClosingRetainsTheDraftWhileChangingTargetRenewsTheRequestIdentity()
    {
        using var context = Context();
        var requests = new List<RemoteBootstrapInput>();
        var cut = context.Render<RemoteBootstrapDialog>(p => p.Add(x => x.IsOpen, true)
            .Add(x => x.TargetSelector, "<p>Tenant</p>").Add(x => x.TargetKey, "tenant-one")
            .Add(x => x.CanInspect, true).Add(x => x.OnInspect, input => requests.Add(input)));
        cut.Find("input[type='url']").Change("https://fixture.example/seed.yaml");
        cut.Find("button.button-primary").Click();
        cut.Render(p => p.Add(x => x.IsOpen, false));
        Assert.HasCount(0, cut.FindAll("dialog"));
        cut.Render(p => p.Add(x => x.IsOpen, true));
        cut.Find("button.button-primary").Click();
        Assert.AreEqual(requests[0], requests[1]);
        cut.Render(p => p.Add(x => x.TargetKey, "tenant-two"));
        cut.Find("button.button-primary").Click();
        Assert.AreNotEqual(requests[0].RequestId, requests[2].RequestId);
        Assert.AreEqual(requests[0].DocumentUrl, requests[2].DocumentUrl);
        cut.Render(p => p.Add(x => x.CompletedRequestId, requests[2].RequestId));
        cut.Find("button.button-primary").Click();
        Assert.HasCount(3, requests);
        Assert.HasCount(1, cut.FindAll("[role='alert']"));
    }

    [TestMethod]
    public void ArchiveInspectionIsBoundedAndOversizedReplacementCannotReusePreviousBytes()
    {
        using var context = Context();
        var requests = new List<RemoteBootstrapInput>();
        var cut = context.Render<RemoteBootstrapDialog>(p => p.Add(x => x.IsOpen, true)
            .Add(x => x.TargetSelector, "<p>Tenant</p>").Add(x => x.CanInspect, true)
            .Add(x => x.OnInspect, input => requests.Add(input)));
        var upload = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>();
        upload.UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "seed.zip", contentType: "application/zip"));
        Assert.AreEqual("seed.zip", cut.Find(".remote-bootstrap-file").TextContent);
        cut.Find("button.button-primary").Click();
        Assert.HasCount(1, requests);
        Assert.AreEqual("AQID", requests[0].ArchiveBase64);
        upload.UploadFiles(InputFileContent.CreateFromBinary(new byte[Agentstration.Bootstrap.Contracts.WorkspaceSeedFormat.MaximumArchiveBytes + 1], "too-large.zip"));
        cut.Find("button.button-primary").Click();
        Assert.HasCount(1, requests);
        Assert.HasCount(1, cut.FindAll("[role='alert']"));
        Assert.HasCount(0, cut.FindAll(".remote-bootstrap-file"));
    }

    [TestMethod]
    public void BusyInspectionLocksInputsAndDismissal()
    {
        using var context = Context();
        var closed = false;
        var cut = context.Render<RemoteBootstrapDialog>(p => p.Add(x => x.IsOpen, true)
            .Add(x => x.TargetSelector, "<p>Instance</p>").Add(x => x.CanInspect, true)
            .Add(x => x.Busy, true).Add(x => x.OnClose, () => closed = true));
        Assert.IsTrue(cut.Find("button.button-primary").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("textarea").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("input[type='file']").HasAttribute("disabled"));
        cut.Find("[data-modal-close]").Click();
        Assert.IsFalse(closed);
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
}
