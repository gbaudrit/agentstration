using System.Net;
using System.Net.Http.Json;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ModelProfileCollectionStatusTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void ApiAvailabilityStatesKeepLabelsAndTonesTogether(string culture)
    {
        using var cultureScope = new TestCultureScope(culture);
        var cases = new[]
        {
            ("Ready", "Ready", UiStatus.Success), ("ready", "Ready", UiStatus.Success),
            ("Unknown", "Unknown", UiStatus.Neutral), ("unknown", "Unknown", UiStatus.Neutral),
            ("ProviderUnavailable", "ProviderUnavailable", UiStatus.Warning),
            ("modelUnavailable", "ModelUnavailable", UiStatus.Warning),
            ("InvalidConfiguration", "InvalidConfiguration", UiStatus.Danger),
            ("available", "Available", UiStatus.Success), ("Available", "Available", UiStatus.Success),
            ("unavailable", "Unavailable", UiStatus.Warning), ("Incompatible", "Incompatible", UiStatus.Danger), ("Succeeded", "Unknown", UiStatus.Neutral),
            ("starting", "Unknown", UiStatus.Neutral), ("futureState", "Unknown", UiStatus.Neutral)
        };
        var profiles = cases.Select((item, index) => new ModelProfileSummaryResponse(
            $"profile-{index}", $"profile-{index}",
            new($"Profile {index}", null, new("provider", "provider", "Provider"), new("model"),
                new(), new(), new(), item.Item1, null), ScopeRef: ResourceScopeRef.Instance)).ToArray();
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        using var handler = new SummaryHandler(profiles);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        context.Services.AddSingleton<IModelProfilesClient>(new ModelProfilesApiClient(http));
        context.Services.AddSingleton<IModelProvidersClient>(new ModelProvidersApiClient(http));
        var cut = context.Render<Agentstration.Web.Components.Pages.ModelProfiles>();
        var strings = context.Services.GetRequiredService<IStringLocalizer<ModelProfilesStrings>>();
        cut.WaitForAssertion(() =>
        {
            var statuses = cut.FindAll("tbody .status-badge");
            Assert.HasCount(cases.Length, statuses);
            for (var index = 0; index < cases.Length; index++)
            {
                Assert.AreEqual(strings[$"Status.{cases[index].Item2}"].Value, statuses[index].TextContent);
                StringAssert.Contains(statuses[index].ClassName!, $"status-{cases[index].Item3.ToString().ToLowerInvariant()}");
            }
            Assert.AreEqual(2, handler.Calls, "Rendering states must not resolve a resource or scope per row.");
        });
        cut.FindAll("select")[1].Change("available");
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("tbody tr")));
        cut.FindAll("select")[1].Change(string.Empty);
        cut.Find("input[type='search']").Input("profile-0");
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("tbody tr")));
        cut.Find("input[type='search']").Input("no-such-profile");
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-collection")));
        cut.Find("input[type='search']").Input(string.Empty);
        cut.WaitForAssertion(() => Assert.HasCount(cases.Length, cut.FindAll("tbody tr")));
    }

    private sealed class SummaryHandler(ModelProfileSummaryResponse[] profiles) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("modelprofiles", StringComparison.Ordinal)
                    ? JsonContent.Create(new { value = profiles })
                    : JsonContent.Create(new { value = Array.Empty<ModelProviderResponse>() })
            });
        }
    }
}
