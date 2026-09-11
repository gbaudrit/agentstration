using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Extensions.Git;

public static class GitSourceOptionContracts
{
    public const string SourceChannelOptionSet = "io.agentstration.git/source-channel";
    public const string Version = "1.0.0";

    public static AepOptionSetDescriptor SourceChannel { get; } = Create();

    private static AepOptionSetDescriptor Create()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "repository": {
                  "type": "string",
                  "minLength": 1,
                  "description": "Absolute HTTPS repository URL. Local file repositories may be enabled explicitly for development and tests."
                },
                "ref": {
                  "type": "string",
                  "minLength": 1,
                  "description": "Explicit refs/heads/*, refs/tags/*, or full 40-character commit SHA."
                },
                "rootPath": {
                  "type": "string",
                  "minLength": 1,
                  "description": "Optional descendant directory to expose as the materialized content root."
                },
                "credentialsRef": {
                  "type": "string",
                  "minLength": 1,
                  "description": "Reserved reference to locally managed credentials; private repositories are not supported yet."
                }
              },
              "required": ["repository", "ref"],
              "additionalProperties": false
            }
            """);
        var version = AepOptionSetVersionDescriptor.Create(Version, document.RootElement);
        return new(
            SourceChannelOptionSet,
            AepContributionKinds.SourceProvider,
            GitSourceProvider.ContributionId,
            AepOptionScopes.SourceChannel,
            Version,
            [version]);
    }
}
