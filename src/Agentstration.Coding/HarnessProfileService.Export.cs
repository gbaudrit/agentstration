using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public sealed partial class HarnessProfileService
{
    public async Task<Result<HarnessExportArchive>> ExportAsync(WorkspaceId workspaceId, HarnessExportRequest command, CancellationToken token)
    {
        var preview = await PreviewAsync(workspaceId, command, token);
        if (!preview.IsSuccess) return Result<HarnessExportArchive>.Failure(preview.ErrorCode!);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in preview.Value!.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(file.Path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0x81A4 << 16;
                await using var output = entry.Open();
                await output.WriteAsync(Encoding.UTF8.GetBytes(file.Content), token);
            }
        }
        if (buffer.Length > 128 * 1024) return Result<HarnessExportArchive>.Failure("harness_export_limit");
        var bytes = buffer.ToArray();
        return Result<HarnessExportArchive>.Success(new($"{command.Name}-{command.Version}.zip", bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), preview.Value!.Digest));
    }
}
