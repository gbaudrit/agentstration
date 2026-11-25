using Agentstration.DataSources.Contracts;
using Agentstration.Resources;

namespace Agentstration.Knowledge.Contracts;

public sealed record PrepareKnowledgePreviewRequest(ResourceNamespace Namespace, KnowledgeSourceProperties Definition,
    IReadOnlyDictionary<string, string> BindingSamples);
public sealed record KnowledgePreviewInputIssue(string BindingName, string Code, bool Required);
public sealed record PreparedKnowledgePreview(SourceSample Sample, IReadOnlyList<KnowledgePreviewInputIssue> Issues);
