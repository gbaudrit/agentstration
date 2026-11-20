using System.Globalization;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TaskDetailsLocalizationTests
{
    [TestMethod]
    public void CatalogsUseTheSelectedFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
            var task = services.GetRequiredService<IStringLocalizer<TaskDetailsStrings>>();
            var run = services.GetRequiredService<IStringLocalizer<TaskFlowRunDetailsStrings>>();

            Assert.AreEqual("Annuler cette tâche ?", task["CancelTitle"].Value);
            Assert.AreEqual("Tâche", task["Task"].Value);
            Assert.AreEqual("Terminée", task["ActionStatus.Completed"].Value);
            Assert.AreEqual("Saisie requise", task["ActionKind.InputRequired"].Value);
            Assert.AreEqual("Résultat structuré", task["ResultKind.Structured"].Value);
            Assert.AreEqual("Tâche terminée", task["ActivityType.TaskCompleted"].Value);
            StringAssert.Contains(task["OperationUnconfirmed"].Value, "Votre réponse est conservée");
            Assert.AreEqual("Ouvrir la tâche dans le Workplace", task["OpenTaskInWorkplace"].Value);
            Assert.AreEqual("FlowRun introuvable", run["NotFoundTitle"].Value);
            Assert.AreEqual("Entrée résolue", run["ResolvedInput"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
