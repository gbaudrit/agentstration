using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ExtensionConfigurationPanelTests
{
    [TestMethod]
    [DataRow("en-US", "Installation options", "Save and apply")]
    [DataRow("fr-FR", "Options de l’installation", "Enregistrer et appliquer")]
    public async Task PanelReusesTypedAndSecuredBindingEditorAndSavesReferences(string cultureName, string heading, string save)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new Handler();
        using var context = Context(handler);
        var page = context.Render<ExtensionConfigurationPanel>(p => p.Add(c => c.RegistrationName, "fixture")
            .Add(c => c.RegistrationUid, handler.RegistrationUid).Add(c => c.CanManage, true));
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindComponents<ValueBindingRequirementEditor>()));
        StringAssert.Contains(page.Markup, heading);
        StringAssert.Contains(page.Markup, save);
        var editors = page.FindComponents<ValueBindingRequirementEditor>();
        var domains = editors.Single(e => e.Instance.Requirement.Id == "AllowedDomains");
        Assert.AreEqual("array", domains.Instance.Requirement.ValueType);
        Assert.AreEqual(ParameterValueType.Array, domains.Instance.CreationContext!.ValueType);
        Assert.IsTrue(domains.Instance.Options.Any(o => o.Kind == ValueBindingSourceKind.Parameter && o.Address.Name == "domains"));
        var credential = editors.Single(e => e.Instance.Requirement.Id == "Credential");
        Assert.IsTrue(credential.Instance.Requirement.Secured);
        CollectionAssert.AreEqual(new[] { ValueBindingSourceKind.Secret }, credential.Instance.AllowedSources.ToArray());
        await page.InvokeAsync(() => domains.Instance.SelectionChanged.InvokeAsync(new(ValueBindingSourceKind.Parameter,
            new(ResourceScopeRef.Instance, ResourceNamespace.Default, ParameterResourceKinds.Parameter, "domains"))));
        await page.InvokeAsync(() => credential.Instance.SelectionChanged.InvokeAsync(new(ValueBindingSourceKind.Secret,
            new(ResourceScopeRef.Instance, ResourceNamespace.Default, SecretResourceKinds.Secret, "credential"))));
        await page.Find("form").SubmitAsync();
        page.WaitForAssertion(() => Assert.IsNotNull(handler.Saved));
        Assert.AreEqual(ModelProviderValueBindingKind.Parameter, handler.Saved!.Bindings.Single(b => b.RequirementId == "AllowedDomains").Kind);
        Assert.AreEqual(ModelProviderValueBindingKind.Secret, handler.Saved.Bindings.Single(b => b.RequirementId == "Credential").Kind);
        Assert.IsTrue(handler.Saved.Bindings.All(b => b.Literal is null));
        Assert.IsFalse(page.Markup.Contains("synthetic-value-not-for-ui", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LegacyAndRestartOnlyContractsRemainNonEditable()
    {
        using var handler = new Handler { Supported = false };
        using var context = Context(handler);
        var page = context.Render<ExtensionConfigurationPanel>(p => p.Add(c => c.RegistrationName, "fixture"));
        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "does not support operational configuration"));
        Assert.IsEmpty(page.FindComponents<ValueBindingRequirementEditor>());
        handler.Supported = true; handler.RestartRequired = true;
        page.Render(p => p.Add(c => c.ContextKey, "new-contract").Add(c => c.CanManage, true));
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindComponents<ValueBindingRequirementEditor>()));
        Assert.IsTrue(page.Find("button[type='submit']").HasAttribute("disabled"));
        StringAssert.Contains(page.Markup, "restart required");
    }

    private static BunitContext Context(Handler handler)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsApiClient(http));
        context.Services.AddSingleton<IParametersClient>(new ParametersApiClient(http));
        context.Services.AddSingleton<ISecretsClient>(new SecretsApiClient(http));
        return context;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Guid RegistrationUid { get; } = Guid.NewGuid();
        public bool Supported { get; set; } = true;
        public bool RestartRequired { get; set; }
        public PutExtensionConfigurationRequest? Saved { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object result;
            if (request.RequestUri!.AbsolutePath.EndsWith("/configuration", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Put) Saved = await request.Content!.ReadFromJsonAsync<PutExtensionConfigurationRequest>(cancellationToken);
                result = new ExtensionConfigurationView(Supported, !Supported ? null : new("fixture/options", "1.0.0", "sha256:fixture",
                    [new("AllowedDomains", JsonSerializer.SerializeToElement(new { type = "array", maxItems = 128, items = new { type = "string" } }), true, false,
                        ["Literal", "Parameter"], "Process", RestartRequired ? "RestartRequired" : "Immediate", null),
                     new("Credential", JsonSerializer.SerializeToElement(new { type = "string" }), true, true, ["Secret"], "Process", "Immediate", null)]),
                    null, null, true, new Dictionary<string, JsonElement> { ["AllowedDomains"] = JsonSerializer.SerializeToElement(new[] { "example.test" }) });
            }
            else if (request.RequestUri.AbsolutePath == "/api/parameters")
                result = new ParameterResource[] { new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = ParameterResourceKinds.Parameter,
                    ScopeRef = ResourceScopeRef.Instance, Metadata = new() { Name = "domains" },
                    Definition = new() { DisplayName = "Domains", ValueType = ParameterValueType.Array, Value = JsonSerializer.SerializeToElement(new[] { "example.test" }) } } };
            else
                result = new SecretResponse[] { new(new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Secret,
                    ScopeRef = ResourceScopeRef.Instance, Metadata = new() { Name = "credential" },
                    Definition = new() { DisplayName = "Credential", Vault = new("vault", ResourceScopeRef.Instance), Key = "credential" } }, "configured", true) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
}
