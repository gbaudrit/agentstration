using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Management.Abstractions;

namespace Agentstration.ModelProviders;

public sealed class AepSourceProviderMaterializer(IHttpClientFactory httpClients) : ISourceProviderMaterializer
{
    public async Task<ResolvedSourceRevision> ResolveAsync(
        SourceProviderInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Client(invocation).ResolveAsync(
                new(Map(invocation.Configuration)), cancellationToken);
            return new(result.Revision, new(result.Integrity.Algorithm, result.Integrity.Digest));
        }
        catch (AepProtocolException exception)
        {
            throw new SourceRetrievalException(exception.Code, exception.Message, exception);
        }
    }

    public async Task<MaterializedSourceRevision> MaterializeAsync(
        SourceProviderInvocation invocation,
        string revision,
        SourceMaterializationLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Client(invocation).MaterializeAsync(new(
                Map(invocation.Configuration),
                revision,
                new(limits.MaxArchiveBytes, limits.MaxEntries, limits.MaxExpandedBytes, limits.TimeoutSeconds)), cancellationToken);
            return new(
                result.Revision,
                result.Archive.MediaType,
                result.Archive.Content,
                result.Archive.ExpandedBytes,
                result.Archive.EntryCount,
                new(result.Archive.Integrity.Algorithm, result.Archive.Integrity.Digest));
        }
        catch (AepProtocolException exception)
        {
            throw new SourceRetrievalException(exception.Code, exception.Message, exception);
        }
    }

    private AepSourceProviderClient Client(SourceProviderInvocation invocation)
    {
        var http = httpClients.CreateClient("agentstration-aep");
        http.BaseAddress = invocation.Endpoint;
        return new AepClient(http).CreateSourceProvider(invocation.ContributionId);
    }

    private static AepVersionedOptions Map(SourceChannelConfiguration configuration) =>
        new(configuration.OptionSet, configuration.Version, configuration.SchemaDigest, configuration.Values.Clone());
}
