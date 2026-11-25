using Agentstration.Work;

namespace Agentstration.Application.Work;

/// <summary>Bounded portable document encoding; Work remains independent of YAML libraries.</summary>
public interface IWorkDocumentCodec
{
    byte[] Write<T>(T value);
    Result<T> Read<T>(ReadOnlyMemory<byte> content, int maximumBytes) where T : class;
}
