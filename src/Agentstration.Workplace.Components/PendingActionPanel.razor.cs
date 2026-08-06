using System.Text.Json;
using Agentstration.Work;

namespace Agentstration.Workplace.Components;

public sealed record PendingActionAnswer(PendingActionId PendingActionId, string ResumeToken, IReadOnlyDictionary<string, JsonElement> Values);
