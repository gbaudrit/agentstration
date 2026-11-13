using Agentstration.Asp.Abstractions;
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
            try { DocumentModel.ValidateSchema(definition.Collections[index].Schema); }
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
