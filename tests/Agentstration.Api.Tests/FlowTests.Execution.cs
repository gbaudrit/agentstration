using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Infrastructure.Flows;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public void DirectFlowRequiresExactlyOneTypedTarget()
    {
        var valid = Definition("direct", new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        FlowValidator.Validate(valid);
        var exception = Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("invalid", new DirectFlowDefinition(null!))));
        Assert.AreEqual("flow_target_required", exception.Code);
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("direct-flow",
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Flow, "child")))));
    }

}

