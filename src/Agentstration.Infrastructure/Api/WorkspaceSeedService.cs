using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources;
using Agentstration.Sources.Contracts;
using Agentstration.Work;

namespace Agentstration.Web.Hosting;

internal sealed record WorkspaceSeedStoredContent(Guid ActorPrincipalId, string RequestFingerprint,
    WorkspaceSeedProvenance Provenance, WorkspaceSeedManifest Manifest, SourceSnapshotArtifactReference Artifact,
    BootstrapProfileSummary Profile);
internal sealed record WorkspaceSeedResource : Resource
{
    public required WorkspaceSeedStoredContent Definition { get; init; }
}

/// <summary>Acquires pinned content; all resource provisioning remains in the existing Bootstrap application.</summary>
public sealed partial class WorkspaceSeedService(IResourceStore resources, IIdentityStore identities,
    IPlatformAuthorizationService administrators, IRequestContextScopeFactory scopes,
    ISourceSnapshotArtifactStore artifacts, ISourceSnapshotContentReader content,
    SourceBootstrapProfileLoader profiles, IHttpClientFactory http, TimeProvider clock,
    IBootstrapExtensionReferenceResolver extensionReferences, IResourceAccessPolicyService access,
    Agentstration.Runtime.Core.RuntimeWorkerDispatchService workers, ICurrentRequestContext current)
{
    private const string ReceiptKind = "BootstrapSeedReceipt";
    internal const string HttpClientName = "workspace-seed-ingestion";
    private static readonly ActivitySource Activity = new("Agentstration.Bootstrap");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<WorkspaceSeedReceipt> IngestAsync(IngestWorkspaceSeedRequest request, Guid actor, CancellationToken token)
    {
        var target = await AuthorizeTargetAsync(request.Target, actor, token);
        if (request.RequestId == Guid.Empty || new[] { request.ArchiveBase64 is not null, request.Document is not null, request.DocumentUrl is not null }.Count(value => value) != 1
            || request.ArchiveBase64?.Length > (WorkspaceSeedFormat.MaximumArchiveBytes + 2) / 3 * 4
            || request.Document is { } document && Encoding.UTF8.GetByteCount(document) > WorkspaceSeedFormat.MaximumDocumentBytes)
            throw Invalid("seed_input_invalid");
        using var activity = Activity.StartActivity("bootstrap.workspace-seed.ingest");
        activity?.SetTag("workspace.id", target.WorkspaceId); activity?.SetTag("principal.id", actor); activity?.SetTag("bootstrap.seed.id", request.RequestId);
        using var scope = scopes.Push(target);
        var fingerprint = Digest(JsonSerializer.SerializeToUtf8Bytes(request, Json));
        var address = Address(target.WorkspaceId, request.RequestId);
        var existing = await resources.GetExactAsync<WorkspaceSeedResource>(address, token);
        if (existing is not null)
        {
            if (existing.Value.Definition.ActorPrincipalId != actor || existing.Value.Definition.RequestFingerprint != fingerprint)
                throw Invalid("seed_request_conflict");
            return Receipt(existing.Value.Definition);
        }

        byte[] bytes; Uri? archiveUrl = null; string? documentDigest = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            if (request.ArchiveBase64 is { } archive) bytes = Convert.FromBase64String(archive);
            else
            {
                var pointerBytes = request.Document is { } text ? Encoding.UTF8.GetBytes(text)
                    : await DownloadAsync(request.DocumentUrl!, ".json", WorkspaceSeedFormat.MaximumDocumentBytes, cancellation.Token);
                documentDigest = Digest(pointerBytes);
                var pointer = JsonSerializer.Deserialize<WorkspaceSeedPointer>(pointerBytes, Json);
                if (pointer is not { FormatVersion: WorkspaceSeedFormat.Version } || !DigestValue(pointer.Sha256)) throw Invalid("seed_pointer_invalid");
                archiveUrl = pointer.ArchiveUrl;
                bytes = await DownloadAsync(archiveUrl, ".zip", WorkspaceSeedFormat.MaximumArchiveBytes, cancellation.Token);
                if (Digest(bytes) != pointer.Sha256) throw Invalid("seed_integrity_mismatch");
            }
            if (bytes.Length is < 1 or > WorkspaceSeedFormat.MaximumArchiveBytes) throw Invalid("seed_archive_size_limit");
            var (expanded, entries) = ArchiveBounds(bytes);
            var digest = Digest(bytes);
            var artifact = await artifacts.SaveAsync(new(digest, "application/zip", bytes, expanded, entries, new("sha256", digest)), cancellation.Token);
            await VerifyArtifactAsync(artifact, cancellation.Token);
            await using var snapshot = await content.OpenAsync(artifact, cancellation.Token);
            var manifest = JsonSerializer.Deserialize<WorkspaceSeedManifest>(await snapshot.ReadBytesAsync(
                WorkspaceSeedFormat.ManifestPath, WorkspaceSeedFormat.MaximumDocumentBytes, cancellation.Token), Json);
            ValidateManifest(manifest);
            if (snapshot.Paths.Count(path => path.Equals("profile.yaml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/profile.yaml", StringComparison.OrdinalIgnoreCase)) != 1
                || !snapshot.Paths.Contains(manifest!.Path + "/profile.yaml", StringComparer.Ordinal))
                throw Invalid("seed_single_profile_required");
            var provenance = new WorkspaceSeedProvenance(request.RequestId, request.Target, actor, manifest.Version,
                manifest.ProviderRevision, digest, manifest.Profile, manifest.Path, request.DocumentUrl, archiveUrl, documentDigest, clock.GetUtcNow())
            { ImportPlans = manifest.ImportPlans };
            await using var loaded = await profiles.MaterializeSnapshotAsync(artifact, manifest.Profile, manifest.Path, null, null, cancellation.Token);
            ValidateProfile(loaded.Profiles.Single(), manifest);
            var stored = new WorkspaceSeedStoredContent(actor, fingerprint, provenance, manifest, artifact, loaded.Profiles.Single().Summary);
            var resource = new WorkspaceSeedResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ReceiptKind,
                ScopeRef = address.ScopeRef,
                Metadata = new() { Name = address.Name },
                Generation = 1,
                Definition = stored,
                Status = new() { ProvisioningState = ProvisioningState.Succeeded }
            };
            try { _ = await resources.PutExactAsync(address.ScopeRef, resource, null, true, cancellation.Token); }
            catch (ResourceConcurrencyException)
            {
                var replay = await resources.GetExactAsync<WorkspaceSeedResource>(address, cancellation.Token);
                if (replay?.Value.Definition is not { } saved || saved.ActorPrincipalId != actor || saved.RequestFingerprint != fingerprint)
                    throw Invalid("seed_request_conflict");
                return Receipt(saved);
            }
            return Receipt(stored);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested) { throw Invalid("seed_acquisition_timeout"); }
        catch (DeclarativeBootstrapException exception) when (!exception.Message.StartsWith("seed_", StringComparison.Ordinal)) { throw Invalid("seed_profile_invalid"); }
        catch (HttpRequestException) { throw Invalid("seed_download_unavailable"); }
        catch (SourceRetrievalException) { throw Invalid("seed_artifact_unavailable"); }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException or OverflowException or SourceValidationException)
        { throw Invalid("seed_content_invalid"); }
    }

    public async Task<WorkspaceSeedReceipt> GetAsync(Guid id, BootstrapApplicationTarget target, Guid actor, CancellationToken token)
    {
        var context = await AuthorizeTargetAsync(target, actor, token);
        using var scope = scopes.Push(context);
        return Receipt(await StoredAsync(id, target, actor, token));
    }

    private async Task<WorkspaceSeedStoredContent> StoredAsync(Guid id, BootstrapApplicationTarget target, Guid actor, CancellationToken token)
    {
        if (id == Guid.Empty || target.WorkspaceId is not { } workspace) throw Invalid("seed_target_invalid");
        var record = (await resources.GetExactAsync<WorkspaceSeedResource>(Address(workspace, id), token))?.Value;
        if (record is null || record.Definition.ActorPrincipalId != actor || record.Definition.Provenance.Target != target)
            throw Invalid("seed_not_found");
        return record.Definition;
    }

    private async Task<RequestContext> AuthorizeTargetAsync(BootstrapApplicationTarget? target, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || !await administrators.IsPlatformAdministratorAsync(actor, token)) throw new AuthorizationDeniedException("platform/admin");
        if (target is not { TenantId: { } tenantId, WorkspaceId: { } workspaceId }) throw Invalid("seed_workspace_required");
        var tenant = await identities.GetTenantAsync(tenantId, token);
        var workspace = await identities.GetWorkspaceAsync(tenantId, workspaceId, token);
        if (tenant is not { Status: TenantStatus.Active } || workspace is not { Status: WorkspaceStatus.Active }
            || OperationalTopologyIdentities.IsSystemTenant(tenant) || OperationalTopologyIdentities.IsOperationalWorkspace(workspace))
            throw Invalid("seed_target_invalid");
        return new(actor, tenantId, workspaceId);
    }

    private async Task<byte[]> DownloadAsync(Uri url, string suffix, int maximum, CancellationToken token)
    {
        if (url is not { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0 }
            || url.Port != 443 || url.AbsolutePath.Contains('%') || !url.AbsolutePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(url.IdnHost, out var address) && !SourceRegistryNetworkPolicy.IsAddressAllowed(address, false))
            throw Invalid("seed_url_invalid");
        using var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.ConnectionClose = true;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK) throw Invalid("seed_download_failed");
        if (response.Content.Headers.ContentLength > maximum) throw Invalid("seed_download_size_limit");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType != "application/octet-stream"
            && !(suffix == ".json" && mediaType == "application/json")
            && !(suffix == ".zip" && mediaType is "application/zip" or "application/x-zip-compressed")) throw Invalid("seed_media_type_invalid");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token); if (count == 0) break;
            if (output.Length + count > maximum) throw Invalid("seed_download_size_limit");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }

    private static (long Expanded, int Entries) ArchiveBounds(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > WorkspaceSeedFormat.MaximumEntries) throw Invalid("seed_archive_size_limit");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > WorkspaceSeedFormat.MaximumExpandedBytes) throw Invalid("seed_archive_size_limit");
        }
        return (expanded, archive.Entries.Count);
    }

    private async Task VerifyArtifactAsync(SourceSnapshotArtifactReference artifact, CancellationToken token)
    {
        await using var stream = await artifacts.OpenReadAsync(artifact, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long length = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token); if (count == 0) break;
            length += count;
            if (length > WorkspaceSeedFormat.MaximumArchiveBytes || length > artifact.Length) throw Invalid("seed_integrity_mismatch");
            hash.AppendData(buffer.AsSpan(0, count));
        }
        if (length != artifact.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != artifact.Sha256) throw Invalid("seed_integrity_mismatch");
    }

    private static void ValidateManifest(WorkspaceSeedManifest? value)
    {
        if (value is not { FormatVersion: WorkspaceSeedFormat.Version, Project: { } project, Requirements: { } requirements }
            || !SourceSemanticVersion.TryParse(value.Version, out _) || value.ProviderRevision is not { Length: > 0 and <= 128 } revision
            || revision.Any(char.IsControl) || !WorkProjectDefinition.ValidName(project.Name)
            || requirements.Contributions is not { Count: > 0 and <= 8 } || requirements.Contributions.Any(item => item is null)
            || !requirements.Contributions.Any(item => item.Kind == "Tool" && item.ExtensionId == "Agentstration.Extensions.Git" && item.ContributionId == "git.repository")
            || requirements.ModelProviders is not { Count: > 0 and <= 8 } || requirements.RuntimeProfiles is not { Count: > 0 and <= 8 }
            || requirements.Runtime is not { Name.Length: > 0 and <= 64, CapabilityVersion.Length: > 0 and <= 64, MaterialVersion.Length: > 0 and <= 64 }
            || value.ImportPlans is not { Count: <= 100 } || value.ImportPlans.Any(item => item is null || item.PlanId == Guid.Empty || !DigestValue(item.ExpectedDigest))
            || value.ImportPlans.Select(item => item.PlanId).Distinct().Count() != value.ImportPlans.Count)
            throw Invalid("seed_manifest_invalid");
        BootstrapProfileCatalog.ValidateProfileName(value.Profile);
        if (SourceDescendantPath.Normalize(value.Path, "Seed Bootstrap profile path") != value.Path) throw Invalid("seed_manifest_invalid");
    }

    private static void ValidateProfile(LoadedBootstrapProfile profile, WorkspaceSeedManifest manifest)
    {
        if (profile.Summary.Scope != BootstrapProfileScope.Workspace || profile.Resources.Any(value => value.Resource.Kind is
            "BootstrapProfile" or "Tenant" or "Workspace" or "PrincipalDefaultContext" or "ExtensionEnrollmentPolicy" or "ResourceAccessPolicy" or "ModelProvider" or "RuntimeProfile"))
            throw Invalid("seed_workspace_only");
        if (profile.Resources.Count(value => value.Resource.Kind == "ProjectSharing" && value.Resource.Metadata.Name == manifest.Project.Name
            && value.Resource.Metadata.Namespace == manifest.Project.Namespace) != 1) throw Invalid("seed_project_required");
    }

    private static ScopedResourceAddress Address(Guid workspace, Guid id) => ScopedResourceAddress.Create(ResourceScopeRef.Workspace(workspace),
        ResourceNamespace.Default, ReceiptKind, id.ToString("N"));
    private static WorkspaceSeedReceipt Receipt(WorkspaceSeedStoredContent value) => new(value.Provenance, value.Manifest, value.Profile);
    private static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static bool DigestValue(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static DeclarativeBootstrapException Invalid(string code) => new(code);
}
