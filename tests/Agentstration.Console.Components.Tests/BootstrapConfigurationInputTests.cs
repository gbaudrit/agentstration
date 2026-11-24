using System.Net;
using System.Text;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Bootstrap;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapConfigurationInputTests
{
    [TestMethod]
    [DataRow("en-US", "Required value missing")]
    [DataRow("fr-FR", "Valeur requise manquante")]
    public void MissingInputsReuseSharedTypedEditorAndKeepSecuredLiteralsUnavailable(string cultureName, string missing)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        using var http = new HttpClient(new EmptyCatalog()) { BaseAddress = new("http://localhost") };
        context.Services.AddSingleton<IParametersClient>(new ParametersApiClient(http));
        context.Services.AddSingleton<ISecretsClient>(new SecretsApiClient(http));
        var inputs = new BootstrapConfigurationInput[]
        {
            new("nantes", "password", "Agentstration:Bootstrap:Secrets:Password", "string", true, false, ResourceScopeRef.Instance, ResourceNamespace.Default),
            new("nantes", "port", "Agentstration:Bootstrap:Parameters:Port", "integer", false, false, ResourceScopeRef.Instance, ResourceNamespace.Default)
        };
        var cut = context.Render<BootstrapConfigurationInputs>(p => p.Add(x => x.Inputs, inputs));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindComponents<ValueBindingRequirementEditor>()));
        var editors = cut.FindComponents<ValueBindingRequirementEditor>();
        Assert.IsTrue(editors[0].Instance.Requirement.Secured);
        CollectionAssert.AreEqual(new[] { ValueBindingSourceKind.Secret }, editors[0].Instance.AllowedSources.ToArray());
        Assert.AreEqual("integer", editors[1].Instance.Requirement.ValueType);
        CollectionAssert.AreEqual(new[] { ValueBindingSourceKind.Literal }, editors[1].Instance.AllowedSources.ToArray());
        StringAssert.Contains(cut.Markup, missing);
        Assert.HasCount(1, cut.FindAll("[data-testid='bootstrap-input-binding-literal']"));
    }

    private sealed class EmptyCatalog : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
    }
}
