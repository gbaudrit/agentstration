using Agentstration.Asp.Abstractions;
using Agentstration.Asp.OData;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>The versioned ASP document profile owns its schema subset, independent of product Schema resource formats.</summary>
public sealed class AspDocumentSchemaDefinitionValidator : IStorageDefinitionValidator
{
    public bool ValidatesDocumentSchemas(StorageTypeVersionDescriptor version) => version.Documents is not null && version.Asp?.Profile == AspDocuments.Profile;

    public void Validate(StorageTypeResource type, StorageTypeVersionDescriptor version, StorageProperties definition)
    {
        if (!ValidatesDocumentSchemas(version)) return;
        for (var index = 0; index < definition.Collections.Count; index++)
        {
            try
            {
                var collection = definition.Collections[index];
                DocumentModel.ValidateSchema(collection.Schema);
                AspValidation.Require(DocumentModel.Type(DocumentSchema.Normalize(collection.Schema)) is null or "object",
                    "unsupported_schema", "Collection documents must be JSON objects.");
                if (collection.Policy.QueryProfiles?.Contains(ODataDocumentProfile.Id, StringComparer.Ordinal) == true)
                    _ = new ODataDocumentProfile(collection.Schema);
            }
            catch (AspException exception)
            {
                throw new StorageException("storage_collection_schema_invalid", "The collection schema is incompatible with the ASP document profile.")
                {
                    ValidationIssues = [new($"/definition/collections/{index}/schema{exception.SchemaPath}", exception.Code, exception.SchemaKeyword)]
                };
            }
        }
    }
}
