using System.Globalization;
using Agentstration.Web.Components.Localization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class CultureNavigationTests
{
    [TestMethod]
    public void InitialPreferredCultureRedirectRetainsTheResourceSectionAndCoordinates()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            using var context = new BunitContext();
            var navigation = context.Services.GetRequiredService<NavigationManager>();
            const string resource = "/agents/demo?namespace=example&scopeRef=%2Finstance&context=%2Finstance#definition/tools";
            navigation.NavigateTo(resource);
            Assert.IsTrue(CultureNavigation.NavigateToPreferredCulture(navigation, "fr-FR"));
            Assert.AreEqual(resource, QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query)["returnUrl"].ToString());
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
