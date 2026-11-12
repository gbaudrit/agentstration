using System.Net;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StorageLocalizationTests
{
    [TestMethod]
    [DataRow("en-US", "Storages", "Cancel")]
    [DataRow("fr-FR", "Stockages", "Annuler")]
    public void CatalogLocalizesStatusesAndActionableFailures(string cultureName, string title, string cancel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
        var strings = services.GetRequiredService<IStringLocalizer<StorageStrings>>();
        Assert.AreEqual(title, strings["Storages"].Value);
        Assert.AreEqual(cancel, strings["Cancel"].Value);
        foreach (var status in Enum.GetValues<StorageAvailability>()) Assert.IsFalse(strings[$"Availability.{status}"].ResourceNotFound);
        foreach (var key in new[] { "Documents.Collections", "Documents.Access", "Documents.QueryPayload", "Documents.PublishToolHelp", "Documents.InvalidJson", "Type.Create", "Type.Publish", "Type.ImmutableHelp", "Type.ManagedHelp", "Type.AccessHelp", "Type.ReviewCurrent", "Type.DisableConfirm", "Type.ConfigurationSchema", "Type.Requirements", "Value.Yes", "Value.No" })
            Assert.IsFalse(strings[key].ResourceNotFound, key);
        var accessStrings = services.GetRequiredService<IStringLocalizer<ResourceAccessPanelStrings>>();
        foreach (var key in new[] { "CurrentPolicy", "ReviewedPolicy", "ExactTargetOnly" })
            Assert.IsFalse(accessStrings[key].ResourceNotFound, key);
        foreach (var key in new[] { "Title", "Help", "Allowed", "Denied", "SameScope", "ExactGrant", "PropagatedGrant", "PolicyMissing", "GrantMissing", "Stale", "Unavailable", "Unsaved", "Failed", "Action" })
            Assert.IsFalse(accessStrings[$"Evaluation.{key}"].ResourceNotFound, key);
        foreach (var code in new[] { "storage_version_conflict", "storage_mapping_incomplete", "storage_exports_stale", "storage_service_stale", "storage_service_unavailable", "storage_type_stale", "storage_provider_unavailable", "storage_configuration_invalid", "storage_in_use", "storage_access_denied", "unknown" })
        {
            var key = StorageErrorKey.For(new AgentstrationApiException("PRIVATE", "id", HttpStatusCode.Conflict, code));
            Assert.IsFalse(strings[key].ResourceNotFound, key);
            Assert.DoesNotContain("PRIVATE", strings[key].Value, StringComparison.Ordinal);
        }
    }
}
