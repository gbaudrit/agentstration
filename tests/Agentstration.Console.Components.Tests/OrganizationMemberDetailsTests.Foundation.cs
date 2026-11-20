using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Bunit;

namespace Agentstration.Web.Tests;

public sealed partial class OrganizationMemberDetailsTests
{
    [TestMethod]
    public async Task ChangedMembershipIsBufferedAndExplicitDiscardAdoptsTheSavedRole()
    {
        using var language = new CultureScope("en-US"); using var handler = new MemberHandler(); using var context = Context(handler);
        var cut = Render(context, handler.First.Id); var frame = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        Section(context, "membership"); cut.Find("select").Change(BuiltInIdentityRoleNames.Admin);
        handler.CurrentRole = BuiltInIdentityRoleNames.Member;
        await cut.InvokeAsync(() => frame.OnRefresh.InvokeAsync());
        Assert.AreEqual(BuiltInIdentityRoleNames.Admin, cut.Find("select").GetAttribute("value"));
        Assert.Contains("Updated member access is available", cut.Markup);
        Section(context, "overview"); Assert.Contains(BuiltInIdentityRoleNames.Viewer, cut.Find(".resource-configuration-summary").TextContent);
        cut.Find(".unsaved-changes-bar button").Click();
        Section(context, "membership"); Assert.AreEqual(BuiltInIdentityRoleNames.Member, cut.Find("select").GetAttribute("value"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty); Assert.IsNull(handler.WritePath);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task MemberRefreshRetainsRecoverableDraftAndClearsLostPrivateIdentity(HttpStatusCode status, bool retain)
    {
        using var handler = new MemberHandler(); using var context = Context(handler);
        var cut = Render(context, handler.First.Id); var frame = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        Section(context, "membership"); cut.Find("select").Change(BuiltInIdentityRoleNames.Admin);
        handler.ReadFailure = status; await cut.InvokeAsync(() => frame.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, cut.FindAll(".resource-identity-summary").Count > 0);
        Assert.AreEqual(retain, cut.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
        if (retain) Assert.AreEqual(BuiltInIdentityRoleNames.Admin, cut.Find("select").GetAttribute("value"));
        else Assert.DoesNotContain(handler.First.DisplayName, cut.Markup);
    }

    [TestMethod]
    public async Task MemberRefreshPreservesRoleEditsMadeWhileItsReadIsPending()
    {
        using var handler = new MemberHandler(); using var context = Context(handler);
        var cut = Render(context, handler.First.Id); var frame = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        Section(context, "membership");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); handler.PendingOrganization = pending.Task;
        var refresh = cut.InvokeAsync(() => frame.OnRefresh.InvokeAsync());
        cut.WaitForElement(".resource-refresh-status"); Assert.IsFalse(cut.Find("select").HasAttribute("disabled"));
        cut.Find("select").Change(BuiltInIdentityRoleNames.Admin); pending.SetResult(handler.OrganizationResponse()); await refresh;
        Assert.AreEqual(BuiltInIdentityRoleNames.Admin, cut.Find("select").GetAttribute("value"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty); Assert.IsNull(handler.WritePath);
    }

    [TestMethod]
    [DataRow("tenant")]
    [DataRow("operator")]
    [DataRow("workspace")]
    public void AForeignTenantOrCapabilityContextCannotExposeTheMemberOrEnableCommands(string changed)
    {
        using var handler = new MemberHandler { Administrator = true };
        if (changed == "tenant") handler.ResponseTenant = Guid.NewGuid();
        if (changed == "operator") handler.CapabilitiesPrincipal = Guid.NewGuid();
        if (changed == "workspace") handler.CapabilitiesWorkspace = Guid.NewGuid();
        using var context = Context(handler); var cut = Render(context, handler.First.Id);
        cut.WaitForElement(".error-panel"); Assert.IsEmpty(cut.FindAll(".resource-identity-summary")); Assert.DoesNotContain(handler.First.DisplayName, cut.Markup);
        Assert.AreEqual(0, handler.AdministratorReads); Assert.IsNull(handler.WritePath);
    }

    [TestMethod]
    public async Task AuthenticationInputsAndRoleDraftSurviveSectionChangesAndRefreshTogether()
    {
        using var handler = new MemberHandler { Administrator = true }; using var context = Context(handler);
        var cut = Render(context, handler.First.Id); var frame = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        Section(context, "membership"); cut.Find("select").Change(BuiltInIdentityRoleNames.Admin);
        Section(context, "authentication"); var inputs = cut.FindAll("input"); Assert.HasCount(2, inputs);
        inputs[0].Change("https://identity.example.invalid/realm"); inputs[1].Change("retained-subject");
        Section(context, "overview"); await cut.InvokeAsync(() => frame.OnRefresh.InvokeAsync());
        Section(context, "authentication"); Assert.AreEqual("retained-subject", cut.FindAll("input")[1].GetAttribute("value"));
        Section(context, "membership"); Assert.AreEqual(BuiltInIdentityRoleNames.Admin, cut.Find("select").GetAttribute("value"));
        cut.Find(".unsaved-changes-bar button").Click();
        Assert.AreEqual(BuiltInIdentityRoleNames.Viewer, cut.Find("select").GetAttribute("value")); Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
    }
    [TestMethod]
    [DataRow("en-US", "Could not confirm the member access change")]
    [DataRow("fr-FR", "La modification des accès du membre n’a pas pu être confirmée")]
    public void UnconfirmedMembershipWriteKeepsTheRoleAndShowsLocalizedFeedback(string locale, string message)
    {
        using var language = new CultureScope(locale); using var handler = new MemberHandler { WriteNetworkFailure = true }; using var context = Context(handler);
        var cut = Render(context, handler.First.Id); Section(context, "membership"); cut.Find("select").Change(BuiltInIdentityRoleNames.Admin);
        cut.Find("[data-testid=organization-member-save-role]").Click();
        cut.WaitForAssertion(() => Assert.Contains(message, cut.Find(".resource-details-content [role=alert]").TextContent));
        Assert.AreEqual(BuiltInIdentityRoleNames.Admin, cut.Find("select").GetAttribute("value"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty); Assert.IsNull(handler.SavedRole);
    }
    [TestMethod]
    public void RemovingWorkspaceMembershipReloadsTheSavedViewAndRetainsUnrelatedAuthenticationInputs()
    {
        using var language = new CultureScope("en-US"); using var handler = new MemberHandler { Administrator = true }; using var context = Context(handler);
        var cut = Render(context, handler.First.Id); Section(context, "authentication");
        cut.FindAll("input")[0].Change("https://identity.example.invalid/pending"); cut.FindAll("input")[1].Change("retained-after-membership-removal");
        Section(context, "membership"); cut.Find("[data-testid=organization-member-remove-membership]").Click();
        cut.WaitForAssertion(() => Assert.IsFalse(handler.MembershipPresent)); Assert.AreEqual(2, handler.OrganizationReads);
        Assert.IsEmpty(cut.FindAll("[data-testid=organization-member-remove-membership]"));
        Section(context, "authentication"); Assert.AreEqual("retained-after-membership-removal", cut.FindAll("input")[1].GetAttribute("value"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
    }
}
