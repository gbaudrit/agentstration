using Agentstration.DataSources.Contracts;

namespace Agentstration.DataSources;

public interface IDataSourceHistoryMaintenance
{
    Task MaintainAsync(DataSourceAcquisitionResource completed, CancellationToken cancellationToken);
}
