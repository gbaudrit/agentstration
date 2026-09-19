using System.Text.Json;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

using ParametersPage = Agentstration.Web.Components.Pages.Parameters;

[TestClass]
public sealed class ParameterComponentTests
{
    [TestMethod]
    public void ListRendersVisibleTypedValueAndExactScope()
    {
        using var context = new BunitContext();
        var scope = ResourceScopeRef.Tenant(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IParametersClient>(new StubClient(new ParameterResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ParameterResourceKinds.Parameter,
            Metadata = new() { Name = "retry-count" },
            ScopeRef = scope,
            Definition = new() { DisplayName = "Retry count", ValueType = ParameterValueType.WholeNumber, Value = JsonSerializer.SerializeToElement(12) }
        }));

        var rendered = context.Render<ParametersPage>();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("12", rendered.Find("[data-testid='parameter-row'] code").TextContent);
            StringAssert.Contains(rendered.Markup, scope.Value);
            StringAssert.Contains(rendered.Markup, "/parameters/retry-count?scopeRef=");
        });
    }

    private sealed class StubClient(ParameterResource resource) : IParametersClient
    {
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ParameterResource>>([resource]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef, PutParameterRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
