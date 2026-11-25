using System.Text;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.ResourceManagement;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed class YamlWorkDocumentCodec : IWorkDocumentCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public byte[] Write<T>(T value) => Utf8.GetBytes(ResourceManifestSerializer.ToYaml(value));

    public Result<T> Read<T>(ReadOnlyMemory<byte> content, int maximumBytes) where T : class
    {
        if (content.Length is < 1 || content.Length > maximumBytes)
            return Result<T>.Failure("work_document_size_limit");
        try
        {
            return Result<T>.Success(ResourceManifestSerializer.FromYamlStrict<T>(Utf8.GetString(content.Span)));
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or YamlDotNet.Core.YamlException)
        {
            return Result<T>.Failure("work_document_invalid");
        }
    }
}
