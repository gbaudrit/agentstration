using System.Text.Json;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public static class CodingExecutionValidation
{
    public static bool Configuration(CodingExecutionConfiguration? configuration, bool requirePins) => configuration is
    { RuntimeProfile: { } runtime, Harness: { } harness, Repository: { Length: > 0 and <= 512 } repository, TimeoutSeconds: >= 1 and <= 600, Checks: { Count: <= 8 } checks }
        && !repository.Any(char.IsControl) && WorkProjectDefinition.ValidName(runtime.Name) && WorkProjectDefinition.ValidName(harness.Name)
        && harness.Version is { Length: > 0 and <= 32 } && WorkSharingFormat.Digest(harness.Digest)
        && (!requirePins || runtime.Uid is { } runtimeUid && runtimeUid != Guid.Empty && runtime.Generation is >= 1
            && harness.Uid is { } harnessUid && harnessUid != Guid.Empty && harness.Generation is >= 1)
        && checks.All(check => check is { Name: { } name, Command: { Length: > 0 and <= 64 } command, Arguments: { Count: <= 16 } arguments }
            && WorkProjectDefinition.ValidName(name) && command.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            && arguments.All(argument => argument is { Length: <= 256 } && !argument.Any(char.IsControl)))
        && checks.Select(check => check.Name).Distinct(StringComparer.Ordinal).Count() == checks.Count;

    public static bool Input(CodingTaskInput? input) => input is { BaseRevision: { } revision, Bindings: { Count: <= 32 } bindings }
        && Commit(revision) && bindings.All(pair => WorkProjectDefinition.ValidName(pair.Key) && pair.Value is { Length: <= 8_000 })
        && JsonSerializer.SerializeToUtf8Bytes(bindings).Length <= 16 * 1024;

    public static bool Material(CodingExecutionMaterial? material) => material is
    { Version: CodingExecutionProtocol.MaterialVersion, Coordinates: { } coordinates, Harness: { } harness, Repository: { } repository, Instruction: { Length: > 0 and <= 8_000 } }
        && Configuration(material.Configuration, true) && Input(material.Input)
        && coordinates.WorkspaceId.Value != Guid.Empty && coordinates.PrincipalId != Guid.Empty && coordinates.ProjectId.Value != Guid.Empty
        && coordinates.PlanId.Value != Guid.Empty && coordinates.PlanRevision >= 1 && WorkSharingFormat.Digest(coordinates.PlanDigest)
        && coordinates.TaskId.Value != Guid.Empty && coordinates.TaskVersion >= 1 && WorkSharingFormat.Digest(coordinates.TaskDigest)
        && coordinates.TaskAttemptId != Guid.Empty && coordinates.WorkItemId.Value != Guid.Empty && coordinates.FlowRunId is { Length: > 0 and <= 200 }
        && coordinates.AssignmentId != Guid.Empty && coordinates.AssignmentAttemptId != Guid.Empty && coordinates.InvocationId == coordinates.AssignmentAttemptId
        && harness.ScopeRef == ResourceScopeRef.Workspace(coordinates.WorkspaceId.Value) && harness.ProfileName == material.Configuration.Harness.Name
        && harness.Namespace == material.Configuration.Harness.Namespace && harness.Version == material.Configuration.Harness.Version
        && harness.Uid == material.Configuration.Harness.Uid && harness.Generation == material.Configuration.Harness.Generation
        && harness.ContentDigest == material.Configuration.Harness.Digest && harness.ContentDigest == CodexHarnessMaterializer.ProfileDigest(harness.Definition, harness.NativeConfiguration)
        && CodexHarnessMaterializer.ValidNative(harness.NativeConfiguration.Definition)
        && repository.Provider is { Length: > 0 and <= 64 } && repository.NativeId is { Length: > 0 and <= 256 }
        && repository.Repository == material.Configuration.Repository && repository.CloneUrl is { IsAbsoluteUri: true, Scheme: "https" or "http", UserInfo.Length: 0 }
        && repository.CloneUrl.Fragment.Length == 0 && repository.CloneUrl.Query.Length == 0;

    public static bool Commit(string? value) => value is { Length: 40 or 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string AdmissionDigest(CodingExecutionConfiguration configuration, CodingTaskInput input, CodingRepositoryIdentity repository) =>
        WorkPlanDefinition.Digest(new { Configuration = configuration, Input = input, Repository = RepositoryDigest(repository) });
    public static string RepositoryDigest(CodingRepositoryIdentity identity) => WorkPlanDefinition.Digest(new
    { identity.Provider, identity.Repository, identity.NativeId, identity.CloneUrl });
}
