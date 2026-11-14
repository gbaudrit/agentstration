using System.Text.Json;
using Agentstration.Storage.Contracts;

namespace Agentstration.Web.Console;

public sealed record StructuredDocumentQueryEditorRequest(StorageQueryProfilePin Profile, JsonElement Query, int PageSize);
