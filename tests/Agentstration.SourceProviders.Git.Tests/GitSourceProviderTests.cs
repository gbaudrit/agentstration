using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Git;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Agentstration.SourceProviders.Git.Tests;

[TestClass]
public sealed class GitSourceProviderTests
{
    [TestMethod]
    public async Task ExtensionAdvertisesGitContributionAndVersionedChannelOptions()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        var client = new AepClient(factory.CreateClient());

        var manifest = await client.GetManifestAsync();
        var provider = (await client.ListSourceProvidersAsync()).Single();
        var optionSet = (await client.GetConfigurationAsync()).OptionSets.Single();

        Assert.IsTrue(manifest.Capabilities.ContainsKey(AepCapabilityNames.SourceProvider));
        Assert.AreEqual(GitSourceProvider.ContributionId, provider.Id);
        Assert.AreEqual(GitSourceOptionContracts.SourceChannelOptionSet, optionSet.Id);
        Assert.AreEqual(AepOptionScopes.SourceChannel, optionSet.Scope);
        Assert.AreEqual(AepContributionKinds.SourceProvider, optionSet.ContributionKind);
    }

    [TestMethod]
    public async Task CanonicalAepClientResolvesAndMaterializesGitFixtureOffline()
    {
        await using var repository = await GitFixture.CreateAsync();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GitSourceProvider:AllowLocalRepositories", "true");
            builder.UseSetting("GitSourceProvider:TemporaryDirectory", Path.Combine(repository.Root, "aep-work"));
        });
        var provider = new AepClient(factory.CreateClient()).CreateSourceProvider(GitSourceProvider.ContributionId);
        var configuration = Configuration(repository.Uri, "refs/heads/main", "catalog");

        var resolved = await provider.ResolveAsync(new(configuration));
        var materialized = await provider.MaterializeAsync(new(
            configuration,
            resolved.Revision,
            new(1024 * 1024, 10, 1024 * 1024, 10)));

        Assert.AreEqual(repository.FirstCommit, resolved.Revision);
        Assert.AreEqual(resolved.Revision, materialized.Revision);
        Assert.AreEqual(AepContentIntegrity.Sha256(materialized.Archive.Content), materialized.Archive.Integrity);
    }

    [TestMethod]
    public async Task BranchTagAndCommitResolveToImmutableCommit()
    {
        await using var repository = await GitFixture.CreateAsync();
        var provider = Provider(repository.Root);

        var branch = await provider.ResolveAsync(new(Configuration(repository.Uri, "refs/heads/main")), default);
        var tag = await provider.ResolveAsync(new(Configuration(repository.Uri, "refs/tags/v1")), default);
        var commit = await provider.ResolveAsync(new(Configuration(repository.Uri, repository.FirstCommit)), default);

        Assert.AreEqual(repository.FirstCommit, branch.Revision);
        Assert.AreEqual(repository.FirstCommit, tag.Revision);
        Assert.AreEqual(repository.FirstCommit, commit.Revision);
        Assert.AreEqual("git-commit-sha1", branch.Integrity.Algorithm);
    }

    [TestMethod]
    public async Task MaterializePinsResolvedCommitAndUsesDescendantRootAsArchiveRoot()
    {
        await using var repository = await GitFixture.CreateAsync();
        var provider = Provider(repository.Root);
        var configuration = Configuration(repository.Uri, "refs/heads/main", "catalog");
        var resolved = await provider.ResolveAsync(new(configuration), default);
        await repository.AdvanceBranchAsync();

        var result = await provider.MaterializeAsync(new(
            configuration,
            resolved.Revision,
            new(1024 * 1024, 10, 1024 * 1024, 10)), default);

        Assert.AreEqual(repository.FirstCommit, result.Revision);
        Assert.AreEqual("application/zip", result.Archive.MediaType);
        Assert.AreEqual(AepContentIntegrity.Sha256(result.Archive.Content), result.Archive.Integrity);
        using var archive = new ZipArchive(new MemoryStream(result.Archive.Content), ZipArchiveMode.Read);
        var entry = archive.Entries.Single();
        Assert.AreEqual("source.txt", entry.FullName);
        using var reader = new StreamReader(entry.Open());
        Assert.AreEqual("first", await reader.ReadToEndAsync());
        Assert.IsFalse(File.Exists(Path.Combine(repository.Root, "executed.txt")), "Repository scripts must never execute.");
    }

    [TestMethod]
    public async Task MaterializeEnforcesEntryAndExpandedSizeLimits()
    {
        await using var repository = await GitFixture.CreateAsync();
        var provider = Provider(repository.Root);
        var configuration = Configuration(repository.Uri, repository.FirstCommit);

        var entries = await Assert.ThrowsAsync<AepServerException>(() => provider.MaterializeAsync(new(
            configuration,
            repository.FirstCommit,
            new(1024 * 1024, 1, 1024 * 1024, 10)), default));
        var expanded = await Assert.ThrowsAsync<AepServerException>(() => provider.MaterializeAsync(new(
            configuration,
            repository.FirstCommit,
            new(1024 * 1024, 10, 1, 10)), default));

        Assert.AreEqual("git_archive_too_many_entries", entries.Code);
        Assert.AreEqual("git_archive_expanded_too_large", expanded.Code);
    }

    [TestMethod]
    public async Task ProviderEnforcesCompressedArchiveAndRepositorySizeLimits()
    {
        await using var repository = await GitFixture.CreateAsync();
        var configuration = Configuration(repository.Uri, repository.FirstCommit);
        var provider = Provider(repository.Root);
        var repositoryBoundProvider = Provider(repository.Root, maximumRepositoryBytes: 1);

        var archive = await Assert.ThrowsAsync<AepServerException>(() => provider.MaterializeAsync(new(
            configuration,
            repository.FirstCommit,
            new(1, 10, 1024 * 1024, 10)), default));
        var fetched = await Assert.ThrowsAsync<AepServerException>(() => repositoryBoundProvider.ResolveAsync(
            new(configuration), default));

        Assert.AreEqual("git_archive_too_large", archive.Code);
        Assert.AreEqual("git_repository_too_large", fetched.Code);
    }

    [TestMethod]
    public async Task ProviderRejectsImplicitRefCredentialsAndUntrustedRepositoryForms()
    {
        var provider = Provider(Path.GetTempPath(), allowLocal: false);

        var missingRef = await Assert.ThrowsAsync<AepServerException>(() => provider.ResolveAsync(
            new(Configuration("https://example.invalid/repository.git", reference: null)), default));
        var credentials = await Assert.ThrowsAsync<AepServerException>(() => provider.ResolveAsync(
            new(Configuration("https://example.invalid/repository.git", "refs/heads/main", credentialsRef: "/instance/secrets/git")), default));
        var embeddedCredential = await Assert.ThrowsAsync<AepServerException>(() => provider.ResolveAsync(
            new(Configuration("https://token@example.invalid/repository.git", "refs/heads/main")), default));
        var shortRef = await Assert.ThrowsAsync<AepServerException>(() => provider.ResolveAsync(
            new(Configuration("https://example.invalid/repository.git", "main")), default));
        var localNetwork = await Assert.ThrowsAsync<AepServerException>(() => provider.ResolveAsync(
            new(Configuration("https://127.0.0.1/repository.git", "refs/heads/main")), default));

        Assert.AreEqual("git_configuration_invalid", missingRef.Code);
        Assert.AreEqual("git_credentials_unsupported", credentials.Code);
        Assert.AreEqual("git_repository_invalid", embeddedCredential.Code);
        Assert.AreEqual("git_ref_invalid", shortRef.Code);
        Assert.AreEqual("git_repository_invalid", localNetwork.Code);
    }

    [TestMethod]
    public async Task ProviderPropagatesCallerCancellation()
    {
        await using var repository = await GitFixture.CreateAsync();
        var provider = Provider(repository.Root);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => provider.ResolveAsync(
            new(Configuration(repository.Uri, "refs/heads/main")), cancellation.Token));
    }

    private static GitSourceProvider Provider(
        string temporaryRoot,
        bool allowLocal = true,
        long maximumRepositoryBytes = 32 * 1024 * 1024) => new(Options.Create(new GitSourceProviderOptions
    {
        AllowLocalRepositories = allowLocal,
        TemporaryDirectory = Path.Combine(temporaryRoot, "provider-work"),
        MaximumRepositoryBytes = maximumRepositoryBytes,
        ResolveTimeoutSeconds = 10
    }));

    private static AepVersionedOptions Configuration(
        string repository,
        string? reference,
        string? rootPath = null,
        string? credentialsRef = null)
    {
        var values = new Dictionary<string, string?> { ["repository"] = repository };
        if (reference is not null) values["ref"] = reference;
        if (rootPath is not null) values["rootPath"] = rootPath;
        if (credentialsRef is not null) values["credentialsRef"] = credentialsRef;
        var contract = GitSourceOptionContracts.SourceChannel.Versions.Single();
        return new(GitSourceOptionContracts.SourceChannelOptionSet, contract.Version, contract.SchemaDigest,
            JsonSerializer.SerializeToElement(values));
    }

    private sealed class GitFixture(string root, string firstCommit) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public string Uri => new System.Uri(Root + Path.DirectorySeparatorChar).AbsoluteUri;
        public string FirstCommit { get; } = firstCommit;

        public static async Task<GitFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "agentstration-git-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "catalog"));
            await RunAsync(root, "init", "--initial-branch=main");
            await RunAsync(root, "config", "user.name", "Agentstration tests");
            await RunAsync(root, "config", "user.email", "tests@agentstration.local");
            await File.WriteAllTextAsync(Path.Combine(root, "catalog", "source.txt"), "first");
            await File.WriteAllTextAsync(Path.Combine(root, "outside.txt"), "outside");
            await File.WriteAllTextAsync(Path.Combine(root, "build.sh"), "echo unsafe > executed.txt");
            await RunAsync(root, "add", ".");
            await RunAsync(root, "commit", "-m", "first");
            var commit = (await RunAsync(root, "rev-parse", "HEAD")).Trim().ToLowerInvariant();
            await RunAsync(root, "tag", "-a", "v1", "-m", "version one");
            return new(root, commit);
        }

        public async Task AdvanceBranchAsync()
        {
            await File.WriteAllTextAsync(Path.Combine(Root, "catalog", "source.txt"), "second");
            await RunAsync(Root, "add", ".");
            await RunAsync(Root, "commit", "-m", "second");
        }

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }

        private static async Task<string> RunAsync(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git test fixture could not start Git.");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Git fixture command failed: {error}");
            return output;
        }
    }
}
