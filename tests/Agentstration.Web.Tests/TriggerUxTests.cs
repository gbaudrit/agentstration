using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.WorkOperations;
using Agentstration.Management.Abstractions;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TriggerUxTests
{
    [TestMethod]
    public void TimeZonePickerUsesIanaIdentifiersAndKeepsParisAvailable()
    {
        var zones = TriggerUi.IanaTimeZones();

        CollectionAssert.Contains(zones.ToArray(), "Europe/Paris");
        Assert.IsFalse(zones.Any(value => value.EndsWith("Standard Time", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void LocalScheduleRejectsAmbiguousAndInvalidDaylightSavingTimes()
    {
        Assert.ThrowsExactly<FormatException>(() => TriggerUi.ParseLocalInstant(new DateTime(2026, 10, 25, 2, 30, 0), "Europe/Paris"));
        Assert.ThrowsExactly<FormatException>(() => TriggerUi.ParseLocalInstant(new DateTime(2026, 3, 29, 2, 30, 0), "Europe/Paris"));
        Assert.AreEqual(TimeSpan.FromHours(2), TriggerUi.ParseLocalInstant(new DateTime(2026, 8, 21, 8, 0, 0), "Europe/Paris").Offset);
    }

    [TestMethod]
    public void OccurrencePreviewKeepsNegativeUtcOffsetSign()
    {
        var occurrence = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

        var label = TriggerUi.OccurrenceLabel(occurrence, "America/New_York", occurrence.AddHours(-1));

        StringAssert.Contains(label, "UTC-04:00");
    }

    [TestMethod]
    public void ScheduleAndOccurrenceLabelsUseTheSelectedFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddLocalization(options => options.ResourcesPath = "Resources");
            var services = collection.BuildServiceProvider();
            var strings = services.GetRequiredService<IStringLocalizer<TriggerStrings>>();
            var schedule = new TriggerSchedule { Type = TriggerScheduleType.Interval, Every = "PT2H", StartAt = DateTimeOffset.Parse("2026-08-21T10:00:00Z", CultureInfo.InvariantCulture) };

            StringAssert.StartsWith(TriggerUi.ScheduleLabel(schedule, strings), "Toutes les PT2H");
            StringAssert.Contains(TriggerUi.OccurrenceLabel(schedule.StartAt!.Value.AddHours(3), "Europe/Paris", schedule.StartAt.Value, strings), "dans 3 heures");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [TestMethod]
    public void TaskPendingActionMapsChoiceContractWithoutInventingInteractionState()
    {
        var field = new EntryFieldDefinition
        {
            Name = "approval",
            Type = EntryFieldType.Choice,
            Required = true,
            Options = [new("approve", "Approve"), new("reject", "Reject")]
        };
        var contract = new PendingActionContract(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "run-1", PendingActionKind.ChoiceRequired, PendingActionStatus.Pending, "Review", null, [field], DateTimeOffset.UtcNow, null, null, 1);

        var action = TaskPendingActionUi.ToAction(contract);

        var choice = Assert.IsInstanceOfType<RequestChoiceAction>(action);
        Assert.AreEqual(contract.Id, choice.PendingActionId.Value);
        Assert.AreEqual(field.Name, choice.FieldName);
        Assert.HasCount(2, choice.Options);
        Assert.AreEqual(string.Empty, choice.ResumeToken);
    }
}
