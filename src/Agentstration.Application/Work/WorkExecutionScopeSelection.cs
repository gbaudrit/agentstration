using Agentstration.Flows;
using Agentstration.Work;

namespace Agentstration.Application.Work;

internal static class WorkExecutionScopeSelection
{
    public static FlowRunScope? Select(IEnumerable<IWorkExecutionScopeAccessor> accessors)
    {
        var scopes = accessors.Select(accessor => accessor.Current).OfType<FlowRunScope>().ToArray();
        return scopes.FirstOrDefault(scope => scope.LogicalScopeRef is not null)
            ?? scopes.FirstOrDefault();
    }
}
