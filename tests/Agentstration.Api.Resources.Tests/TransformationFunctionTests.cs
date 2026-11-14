using System.Text.Json;
using Agentstration.Functions;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Builtins;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class TransformationFunctionTests : ResourceManagementApiTestBase
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [TestMethod]
    public async Task BootstrapPlansDependentFunctionsBeforeTheNativeKindExistsAndReplaysWithoutRewriting()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var service = factory.Services.GetRequiredService<TransformationFunctionService>();
        var kindHandler = new FunctionKindBootstrapResourceHandler(service);
        var functionHandler = new TransformationFunctionBootstrapResourceHandler(service);
        var operation = new BootstrapResourceOperationContext("native-test", Path.GetTempPath(), BootstrapProfileScope.Workspace,
            new BootstrapApplicationTarget(context.TenantId, context.WorkspaceId));
        var planning = new BootstrapPlanningContext();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var kind = new BootstrapResourceDocument { ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.FunctionKind,
            Metadata = new() { Name = "native" }, Definition = JsonSerializer.SerializeToElement(new FunctionKindProperties("Native", [new("1.0.0", "native", NativeFunctionKindContract.DefinitionSchema)]), json) };
        var function = new BootstrapResourceDocument { ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.TransformationFunction,
            Metadata = new() { Name = "select" }, Definition = JsonSerializer.SerializeToElement(new TransformationFunctionProperties("Select", [new("1.0.0", new("native", "1.0.0"),
                Json("""{"implementation":{"id":"json.select","version":"1.0.0"}}"""))]), json) };
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await kindHandler.PlanAsync(kind, operation, planning, default)).Disposition);
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await functionHandler.PlanAsync(function, operation, planning, default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await kindHandler.ApplyAsync(kind, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await functionHandler.ApplyAsync(function, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await functionHandler.ApplyAsync(function, operation, default));
        var resolved = await service.ResolveAsync(new("select", "1.0.0"), ResourceScopeRef.Workspace(context.WorkspaceId), default);
        Assert.IsNotNull(resolved.Reference.Digest);
        Assert.IsNotNull(resolved.FunctionKind.Uid);
    }

    [TestMethod]
    public async Task NativeFunctionsUseExactJsonPointersAndSemanticDeduplication()
    {
        var selected = await new JsonSelectFunction().ExecuteAsync(Json("""{"value":{"a/b":{"~":[1,2]}},"path":"/a~1b/~0/1"}"""), default);
        Assert.AreEqual(2, selected.GetInt32());
        var bad = Assert.ThrowsExactly<TransformationFunctionException>(() => FunctionJsonPointer.Select(Json("[1]"), "/00"));
        Assert.AreEqual("function_path_not_found", bad.Code);
        Assert.ThrowsExactly<TransformationFunctionException>(() => FunctionJsonPointer.Select(Json("{}"), "/~2"));
        var distinct = await new CollectionDistinctFunction().ExecuteAsync(Json("""{"value":[{"key":{"a":1,"b":2},"n":1},{"key":{"b":2,"a":1},"n":2}],"path":"/key"}"""), default);
        Assert.AreEqual(1, distinct.GetArrayLength());
        Assert.AreEqual(1, distinct[0].GetProperty("n").GetInt32());
        var filtered = await new CollectionFilterFunction().ExecuteAsync(Json("""{"value":[{"ok":false},{"ok":true}],"path":"/ok","equals":true}"""), default);
        Assert.AreEqual(1, filtered.GetArrayLength());
        var projection = await new JsonProjectFunction().ExecuteAsync(Json("""{"value":{"id":"a","title":"Title"},"properties":[{"name":"code","path":"/id"},{"name":"label","path":"/title"}]}"""), default);
        Assert.AreEqual("a", projection.GetProperty("code").GetString());
    }

    [TestMethod]
    public void EveryRegisteredPureFunctionHasExecutableSupportedContracts()
    {
        INativeTransformationFunction[] functions = [new JsonSelectFunction(), new JsonProjectFunction(), new CollectionFilterFunction(), new CollectionDistinctFunction()];
        foreach (var function in functions)
        {
            _ = Agentstration.Schemas.DataContracts.Declared(function.Descriptor.InputSchema, function.Descriptor.Id);
            _ = Agentstration.Schemas.DataContracts.Declared(function.Descriptor.OutputSchema, function.Descriptor.Id);
        }
        Assert.ThrowsExactly<TransformationFunctionException>(() => new NativeFunctionRegistry([new JsonSelectFunction(), new JsonSelectFunction()]));
    }

    [TestMethod]
    public async Task ResourcesResolveExactNativeImplementationsAndPreserveImmutableVersions()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var service = factory.Services.GetRequiredService<TransformationFunctionService>();
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var kind = await service.PutKindAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.FunctionKind, ScopeRef = owner,
            Metadata = new() { Name = "native" },
            Definition = new("Native", [new("1.0.0", "native", TransformationFunctionService.NativeDefinitionSchema)])
        }, null, true, default);
        var version = new TransformationFunctionVersion("1.0.0", new("native", "1.0.0", owner, Uid: kind.Value.Uid),
            Json("""{"implementation":{"id":"json.select","version":"1.0.0"}}"""));
        var function = await service.PutAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.TransformationFunction, ScopeRef = owner,
            Metadata = new() { Name = "select" }, Definition = new("Select", [version])
        }, null, true, default);
        var resolved = await service.ResolveAsync(new("select", "1.0.0", owner), owner, default);
        var result = await service.InvokeAsync(resolved, owner, Json("""{"value":{"results":[1,2]},"path":"/results"}"""), default);
        Assert.AreEqual(2, result.GetArrayLength());
        var outside = await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => service.ResolveAsync(resolved.Reference, ResourceScopeRef.Workspace(Guid.NewGuid()), default));
        Assert.AreEqual("function_use_denied", outside.Code);
        var forged = await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => service.InvokeAsync(resolved with { ImplementationId = "json.project" }, owner, Json("{}"), default));
        Assert.AreEqual("function_contract_changed", forged.Code);
        Assert.AreEqual(FunctionDigests.Compute(Json("""{"a":1,"b":2}""")), FunctionDigests.Compute(Json("""{"b":2,"a":1}""")));
        var conflict = await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => service.PutAsync(function.Value with
        { Definition = function.Value.Definition with { Versions = [version with { Configuration = Json("""{"implementation":{"id":"json.project","version":"1.0.0"}}""") }] } }, function.ETag, false, default));
        Assert.AreEqual("function_version_immutable", conflict.Code);
        await service.PutAsync(function.Value with { Definition = function.Value.Definition with { Enabled = false } }, function.ETag, false, default);
        var disabled = await Assert.ThrowsExactlyAsync<TransformationFunctionException>(() => service.InvokeAsync(resolved, owner, Json("{}"), default));
        Assert.AreEqual("function_disabled", disabled.Code);
    }
}
