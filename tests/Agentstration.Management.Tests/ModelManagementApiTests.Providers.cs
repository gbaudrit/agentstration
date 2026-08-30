using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class ModelManagementApiTests
{
    [TestMethod]
    public async Task ReadOnlyProviderApisExposeConfiguredProviderAndUnavailableDiscovery()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<ValueResponse<ModelProviderResponse>>("/api/modelproviders");
        var provider = providers!.Value.Single(value => value.Name == "ollama-local");
        var status = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        using var models = await client.GetAsync("/api/modelproviders/ollama-local/models");

        Assert.AreEqual("aspire", provider.Properties.RegistrationSource);
        Assert.AreEqual("unavailable", status!.Status);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, models.StatusCode);
        Assert.AreEqual("application/problem+json", models.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task ProviderValidationAndDeletionProtectionReturnProblemDetails()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var invalid = new CreateModelProviderRequest(
            "invalid-provider",
            new ModelProviderProperties
            {
                DisplayName = "Invalid",
                Extension = new ResourceReference("missing-extension"),
                ContributionId = "ollama"
            });

        using var invalidResponse = await client.PostAsJsonAsync("/api/modelproviders", invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);
        Assert.AreEqual("application/problem+json", invalidResponse.Content.Headers.ContentType?.MediaType);

        var usages = await client.GetFromJsonAsync<ModelProviderUsagesResponse>("/api/modelproviders/ollama-local/usages");
        Assert.IsTrue(usages!.Count >= 1);
        Assert.IsTrue(usages.Value.Any(usage => usage.ResourceType == ResourceKinds.ModelProfile));
        using var deleted = await client.DeleteAsync("/api/modelproviders/ollama-local");
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.AreEqual("application/problem+json", deleted.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public void LegacyProviderOptionsRemainReadableForExplicitMigration()
    {
        var options = JsonSerializer.Deserialize<VersionedExtensionOptions>("""{"minP":0.05,"repeatPenalty":1.1}""");

        Assert.IsNotNull(options);
        Assert.AreEqual(string.Empty, options.OptionSet);
        Assert.IsNotNull(options.LegacyValues);
        Assert.IsTrue(options.LegacyValues.ContainsKey("minP"));
    }

}

