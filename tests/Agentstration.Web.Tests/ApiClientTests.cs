using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.Security;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class ApiClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static AgentResource CreateAgentResource(string name)
    {
        var etag = "\"stored\"";
        return new AgentResource
        {
            Metadata = new ResourceMetadata { Name = name },
            Kind = ResourceKinds.Agent,
            ApiVersion = ManagementApiVersions.V20260801,
            Generation = 1,
            ETag = etag,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted, ResourceVersion = etag },
            Definition = new AgentProperties
            {
                DisplayName = name,
                Instructions = "Help the user.",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        };
    }

    private static ModelProfileResource CreateModelProfile(string name) => new()
    {
        Metadata = new ResourceMetadata { Name = name },
        Kind = ResourceKinds.ModelProfile,
        ApiVersion = ManagementApiVersions.V20260801,
        Definition = new ModelProfileProperties
        {
            DisplayName = "Default reasoning",
            Provider = new ResourceReference("ollama-local"),
            Model = new ModelSelection { Name = "qwen3:4b" },
            Generation = new ModelGenerationOptions { Temperature = 0.2 }
        }
    };

    private static ModelProfileSummaryResponse Summary(string name, string provider, string model, string status) => new(
        name, name,
        new ModelProfileSummaryPropertiesResponse(name, null,
            new ModelProviderReferenceResponse(provider, provider),
            new ModelReferenceResponse(model), new ModelGenerationOptions(), new ModelReasoningOptions(), new ModelOutputOptions(), status, 0));

    private static AgentResourceRequest ToRequest(AgentResource resource) => new()
    {
        ApiVersion = resource.ApiVersion,
        Kind = resource.Kind,
        Metadata = resource.Metadata,
        Definition = resource.Definition
    };

    private static RuntimeRun CreateRun(string id) => new()
    {
        WorkspaceId = TestWorkspaceId,
        Scope = new RuntimeRunScope(Guid.Empty, TestWorkspaceId, Guid.Empty),
        Id = id,
        Name = id,
        Properties = new RuntimeRunProperties
        {
            Agent = new RuntimeAgentReference(CreateAgentResource("web-agent").Metadata.Name, 1),
            Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] },
            Execution = new RuntimeExecutionOptions()
        },
        Status = new RuntimeRunStatus { State = RuntimeRunState.Pending, CreatedAt = DateTimeOffset.UtcNow }
    };

    private static FlowRun CreateFlowRun(string id)
    {
        var flowId = new FlowId("paged-flow");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));
        var now = DateTimeOffset.UtcNow;
        return new FlowRun
        {
            WorkspaceId = TestWorkspaceId,
            Id = id,
            FlowId = flowId,
            FlowVersion = "1.0.0",
            Scope = new FlowRunScope(Guid.Empty, TestWorkspaceId, Guid.Empty),
            Input = JsonSerializer.SerializeToElement(new { }),
            CreatedAt = now,
            DefinitionSnapshot = new FlowVersion(TestWorkspaceId, flowId, "1.0.0", null, definition, new Dictionary<string, string>(), now)
        };
    }

    private static RuntimeRunEvent RunEvent(long sequence, RuntimeRunEventKind kind, string? content = null, RuntimeRunState? state = null) => new()
    {
        WorkspaceId = TestWorkspaceId,
        Sequence = sequence,
        EventId = Guid.NewGuid(),
        RunId = "run-test",
        Kind = kind,
        Timestamp = DateTimeOffset.UtcNow,
        Content = content,
        State = state
    };

    private static readonly Agentstration.Resources.WorkspaceId TestWorkspaceId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class StubHttpClientFactory(Func<string, HttpClient> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory(name);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = nameof(ApiClientTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
