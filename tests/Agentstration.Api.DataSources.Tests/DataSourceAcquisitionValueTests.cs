using System.Text;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DataSourceAcquisitionValueTests : ResourceManagementApiTestBase
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>(
        """{"type":"object","properties":{"dataset":{"type":"string","minLength":1}},"required":["dataset"],"additionalProperties":false}""");

    [TestMethod]
    public async Task TenantAdministrationCanPinAnExplicitlySharedInstanceParameterWithoutReadingItsRawInventory()
    {
        await using var factory = await ResourceServiceFixture.CreateAsync();
        var context = factory.Context;
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var tenant = ResourceScopeRef.Tenant(context.TenantId);
        Agentstration.ResourceManagement.StoredResource<ParameterResource> parameter;
        using (scopes.PushInstance(context))
        {
            var definition = Parameter(ResourceScopeRef.Instance, "shared-input", "shared");
            parameter = await factory.Services.GetRequiredService<ParameterManagementService>().CreateAsync(definition with
            { Definition = definition.Definition with { UsePolicy = new() { Grants = [new(tenant)] } } }, default);
        }
        using (scopes.PushSystem())
            await factory.Services.GetRequiredService<IResourceAccessPolicyService>().PutAsync(new(new(parameter.Value.Uid,
                ResourceScopeRef.Instance, parameter.Value.Namespace, parameter.Value.Kind),
                [new(tenant, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        using var request = scopes.PushTenantAdministration(context);
        var values = factory.Services.GetRequiredService<DataSourceAcquisitionValues>();
        var source = await PinAsync(values, Source(tenant, new("dataset", Parameter: new(parameter.Value.Address, ResourceScopeRef.Instance))));
        var prepared = await values.PrepareAsync(source, Schema, default);
        Assert.AreEqual("shared", prepared.Parameters.GetProperty("dataset").GetString());
    }

    [TestMethod]
    public async Task ParameterValuesAreResolvedAgainForNewAdmissionsAndReplacedIdentitiesAreRefused()
    {
        await using var factory = await ResourceServiceFixture.CreateAsync();
        var context = factory.Context;
        using var request = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var parameters = factory.Services.GetRequiredService<ParameterManagementService>();
        var parameter = await parameters.CreateAsync(Parameter(scope, "input", "first"), default);
        var source = Source(scope, new("dataset", Parameter: new(parameter.Value.Address, scope)));
        var values = factory.Services.GetRequiredService<DataSourceAcquisitionValues>();
        source = await PinAsync(values, source);
        var first = await values.PrepareAsync(source, Schema, default);
        Assert.AreEqual("first", first.Parameters.GetProperty("dataset").GetString());
        parameter = await parameters.PutExactAsync(scope, "input", parameter.Value.Definition with
        { Value = JsonSerializer.SerializeToElement("second") }, parameter.ETag, default);
        var second = await values.PrepareAsync(source, Schema, default);
        Assert.AreEqual("second", second.Parameters.GetProperty("dataset").GetString());
        Assert.AreEqual("first", first.Parameters.GetProperty("dataset").GetString());
        var resumed = await values.PrepareAsync(source, Schema, default, first.Parameters);
        Assert.AreEqual("first", resumed.Parameters.GetProperty("dataset").GetString());
        Assert.AreEqual(parameter.Value.Uid, source.Definition.Dataset!.Acquisition.ParameterBindings.Single().ResourceUid);
        await parameters.DeleteExactAsync(scope, "input", parameter.ETag, default);
        _ = await parameters.CreateAsync(Parameter(scope, "input", "replacement"), default);
        var replaced = await Assert.ThrowsAsync<DataSourceValidationException>(() => values.PrepareAsync(source, Schema, default));
        Assert.AreEqual("data_source_parameter_binding_invalid", replaced.Code);
    }

    [TestMethod]
    public async Task SecretRotationUsesTheSameDurableReferenceWithoutPersistingTheValue()
    {
        await using var factory = await ResourceServiceFixture.CreateAsync();
        var context = factory.Context;
        using var request = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var secrets = factory.Services.GetRequiredService<SecretManagementService>();
        await secrets.CreateVaultAsync(new VaultResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "input-vault" },
            ScopeRef = scope,
            Definition = new() { DisplayName = "Input vault", ProviderType = "local" }
        }, default);
        await secrets.InitializeVaultExactAsync(scope, "input-vault", default);
        var secret = await secrets.CreateSecretAsync(new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = "operation-input" },
            ScopeRef = scope,
            Definition = new() { DisplayName = "Operation input", Vault = new("input-vault", scope), Key = "fixture" }
        }, default);
        using (var value = new SecretValue(Encoding.UTF8.GetBytes("synthetic-first-input")))
            await secrets.SetValueExactAsync(scope, secret.Value.Name, value, default);
        var values = factory.Services.GetRequiredService<DataSourceAcquisitionValues>();
        var source = await PinAsync(values, Source(scope, new("dataset", Secret: new(secret.Value.Address, scope))));
        var prepared = await values.PrepareAsync(source, Schema, default);
        Assert.IsTrue(SecretInputs.IsReference(prepared.Parameters.GetProperty("dataset"), prepared.Secrets));
        var resolver = factory.Services.GetRequiredService<ISecretInputResolver>();
        using (var live = await resolver.ResolveAsync(prepared.Parameters, prepared.Secrets, default))
        {
            Assert.AreEqual("synthetic-first-input", live.Value.GetProperty("dataset").GetString());
            Assert.DoesNotContain("synthetic-first-input", live.Protect(live.Value)!.Value.GetRawText());
        }
        using (var value = new SecretValue(Encoding.UTF8.GetBytes("synthetic-rotated-input")))
            await secrets.SetValueExactAsync(scope, secret.Value.Name, value, default);
        using (var live = await resolver.ResolveAsync(prepared.Parameters, prepared.Secrets, default))
            Assert.AreEqual("synthetic-rotated-input", live.Value.GetProperty("dataset").GetString());
        var durable = JsonSerializer.Serialize(new { source, prepared.Parameters, prepared.Secrets });
        Assert.DoesNotContain("synthetic-first-input", durable);
        Assert.DoesNotContain("synthetic-rotated-input", durable);
        await secrets.DeleteValueExactAsync(scope, secret.Value.Name, default);
        await Assert.ThrowsAsync<DataSourceValidationException>(() => values.PrepareAsync(source, Schema, default));
    }

    [TestMethod]
    public async Task ConflictingLiteralsAndAncestorParametersWithoutUseGrantsAreRejected()
    {
        await using var factory = await ResourceServiceFixture.CreateAsync();
        var context = factory.Context;
        var parameters = factory.Services.GetRequiredService<ParameterManagementService>();
        Agentstration.ResourceManagement.StoredResource<ParameterResource> parameter;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context))
            parameter = await parameters.CreateAsync(Parameter(ResourceScopeRef.Instance, "private-input", "private"), default);
        using var request = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var binding = new DataSourceParameterBinding("dataset", Parameter: new(parameter.Value.Address, ResourceScopeRef.Instance), ResourceUid: parameter.Value.Uid);
        var values = factory.Services.GetRequiredService<DataSourceAcquisitionValues>();
        await Assert.ThrowsAsync<DataSourceValidationException>(() => values.PrepareAsync(Source(scope, binding), Schema, default));
        var source = Source(scope, binding);
        source = source with
        {
            Definition = source.Definition with
            {
                Dataset = source.Definition.Dataset! with
                { Acquisition = source.Definition.Dataset.Acquisition with { Parameters = JsonSerializer.SerializeToElement(new { dataset = "collision" }) } }
            }
        };
        await Assert.ThrowsAsync<DataSourceValidationException>(() => values.PrepareAsync(source, Schema, default));
    }

    private static ParameterResource Parameter(ResourceScopeRef scope, string name, string value) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ParameterResourceKinds.Parameter,
        Metadata = new() { Name = name },
        ScopeRef = scope,
        Definition = new() { DisplayName = name, ValueType = ParameterValueType.Text, Value = JsonSerializer.SerializeToElement(value) }
    };
    private static DataSourceResource Source(ResourceScopeRef scope, DataSourceParameterBinding binding) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSource,
        Metadata = new() { Name = "bound-input" },
        ScopeRef = scope,
        Definition = new()
        {
            DisplayName = "Bound input",
            Dataset = new(null, new(Write: true), new(null!, Empty)
            { ParameterBindings = [binding] })
        }
    };
    private static async Task<DataSourceResource> PinAsync(DataSourceAcquisitionValues values, DataSourceResource source) => source with
    {
        Definition = source.Definition with
        {
            Dataset = source.Definition.Dataset! with
            { Acquisition = source.Definition.Dataset.Acquisition with { ParameterBindings = await values.PinAsync(source, default) } }
        }
    };
}
