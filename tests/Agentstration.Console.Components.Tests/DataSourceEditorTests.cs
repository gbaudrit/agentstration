using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceEditorTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [TestMethod]
    public void DetailsExposeOperationalOverviewAndConsistentForms()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IDataSourcesClient>(new SourceClientStub());
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}");

        var rendered = context.Render<DataSourceEditor>(parameters => parameters
            .Add(component => component.Name, "documentation"));

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(4, rendered.FindAll(".metric-card"));
            StringAssert.Contains(rendered.Markup, "Génération");
            StringAssert.Contains(rendered.Markup, "Dernière acquisition");
            StringAssert.Contains(rendered.Markup, "Réussie");
            StringAssert.Contains(rendered.Markup, "1 acquisition(s)");
            Assert.AreEqual("data-source-web-acquisition-builtin", rendered.Find("[data-testid='data-source-acquisition-flow-link'] strong").TextContent.Trim());
            Assert.AreEqual("/flows/data-source-web-acquisition-builtin", rendered.Find("[data-testid='data-source-acquisition-flow-link']").GetAttribute("href"));
        });

        rendered.FindAll("[role='tab']").Single(value => value.TextContent == "Définition").Click();
        rendered.WaitForAssertion(() =>
        {
            var enabled = rendered.Find("[data-testid='data-source-enabled-option']");
            Assert.IsTrue(enabled.ClassList.Contains("data-source-choice"));
            Assert.IsTrue(rendered.Markup.IndexOf("data-source-enabled-option", StringComparison.Ordinal)
                < rendered.Markup.IndexOf("Configuration (JSON)", StringComparison.Ordinal));
        });

        rendered.FindAll("[role='tab']").Single(value => value.TextContent == "Acquisitions").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='data-source-acquisitions']"));
            Assert.IsTrue(rendered.Find(".data-source-parameters").QuerySelector("textarea") is not null);
            Assert.AreEqual("Réussie", rendered.Find(".status-badge").TextContent.Trim());
            Assert.AreEqual("flowrun-data-source", rendered.Find(".data-source-flow-run").GetAttribute("title"));
        });

        rendered.FindAll("[role='tab']").Single(value => value.TextContent == "YAML").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='data-source-yaml-editor']"));
            Assert.IsTrue(rendered.Find(".data-source-yaml-field").QuerySelector("textarea") is not null);
            StringAssert.Contains(rendered.Markup, "YAML de la source de données");
        });
    }

    private static DataSourceProfileProperties ProfileProperties => new()
    {
        DisplayName = "Web Builtin",
        Version = "1.0.1",
        AcquisitionFlow = new() { Name = "data-source-web-acquisition-builtin", Version = "1.0.1", UseActiveVersion = false }
    };

    private static DataSourceResource Source => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSource,
        Metadata = new() { Name = "documentation" },
        ScopeRef = Scope,
        Generation = 4,
        Definition = new()
        {
            DisplayName = "Documentation Agentstration",
            Description = "Documentation publique",
            Enabled = true,
            Profile = new("web-builtin", Scope),
            Configuration = JsonSerializer.SerializeToElement(new { url = "https://docs.agentstration.io" })
        }
    };

    private static ResolvedDataSourceProfile ResolvedProfile => new()
    {
        Name = "web-builtin",
        Namespace = ResourceNamespace.Default,
        ScopeRef = Scope,
        Uid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        Generation = 1,
        Version = "1.0.1",
        DefinitionHash = "sha256:test",
        Definition = ProfileProperties
    };

    private static DataSourceAcquisitionResource Acquisition => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSourceAcquisition,
        Metadata = new() { Name = "acquisition-1" },
        ScopeRef = Scope,
        DataSourceScopeRef = Scope,
        DataSourceUid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        DataSourceName = "documentation",
        DataSourceNamespace = ResourceNamespace.Default,
        DataSourceGeneration = 4,
        Composition = new()
        {
            Profile = ResolvedProfile,
            Flow = new("data-source-web-acquisition-builtin", ResourceNamespace.Default, "1.0.1", false, null, null, DataSourceFlowContracts.Acquisition)
        },
        FlowRunId = "flowrun-data-source",
        State = DataSourceAcquisitionState.Succeeded,
        CorrelationId = "correlation",
        RequestHash = "sha256:request",
        CreatedBy = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        TenantId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
        WorkspaceId = Scope.TargetId!.Value,
        CreatedAt = new DateTimeOffset(2026, 10, 8, 9, 57, 0, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 10, 8, 9, 58, 0, TimeSpan.Zero),
        Manifest = new() { Artifacts = [new() { ArtifactId = "artifact-1", Kind = DataSourceArtifactKind.Durable, Disposition = DataSourceArtifactDisposition.Publishable }] }
    };

    private sealed class SourceClientStub : IDataSourcesClient
    {
        public Task<IReadOnlyList<DataSourceResource>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceResource>>([Source]);
        public Task<ResourceSnapshot<DataSourceResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult(new ResourceSnapshot<DataSourceResource>(Source, "\"etag\""));
        public Task<DataSourceReadiness> GetReadinessAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult(new DataSourceReadiness(true, true, ResolvedProfile, []));
        public Task<IReadOnlyList<DataSourceAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceAcquisitionResource>>([Acquisition]);
        public Task<ResourceSnapshot<DataSourceResource>> CreateAsync(CreateDataSourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PutDataSourceRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, StartDataSourceAcquisitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ProfileClientStub : IDataSourceProfilesClient
    {
        private static readonly DataSourceProfileResource Profile = new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceProfile, Metadata = new() { Name = "web-builtin" }, ScopeRef = Scope, Definition = ProfileProperties, ActiveVersion = "1.0.1" };
        public Task<IReadOnlyList<DataSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileResource>>([Profile]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileResource>> CreateAsync(CreateDataSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PutDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileRevisionResource>> PublishAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PublishDataSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileResource>> ActivateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, ActivateDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
