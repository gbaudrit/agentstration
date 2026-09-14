using Agentstration.Application.Work;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Work;

public sealed class CurrentWorkplaceContext(ICurrentRequestContext requestContext) : IWorkplaceContext
{
    public WorkspaceId WorkspaceId => new(requestContext.Current.WorkspaceId);
}
