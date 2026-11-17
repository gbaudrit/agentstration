using System.Net;

namespace Agentstration.Web.Console;

public static class StorageErrorKey
{
    public static string For(Exception error) => error is AgentstrationApiException api
        ? ForCode(api.ProblemTitle, api.StatusCode) : "Error.Generic";

    public static string ForCode(string? code, HttpStatusCode? statusCode = null) => code switch
    {
        "storage_documents_backend_connection_refused" => "Error.BackendConnectionRefused",
        "storage_documents_backend_timeout" or "storage_documents_timeout" => "Error.BackendTimeout",
        "storage_documents_backend_unavailable" or "storage_documents_provider_unavailable" or "storage_documents_unavailable" => "Error.BackendUnavailable",
        "storage_metadata_invalid" => "Error.Metadata",
        "storage_collection_schema_invalid" => "Error.CollectionSchema",
        "storage_documents_invalid_query" or "storage_documents_unsupported_query" => "Documents.Error.Query",
        "storage_documents_unsupported_capability" => "Documents.Error.Profile",
        "storage_documents_schema_validation" => "Documents.Error.Schema",
        "storage_documents_invalid_reference" or "storage_documents_request_expired" => "Documents.Error.Expired",
        "storage_collection_unavailable" or "storage_revision_stale" or "storage_documents_version_unavailable" => "Documents.Error.Exact",
        "storage_documents_limit_exceeded" or "storage_documents_capacity_exceeded" => "Documents.Error.Limit",
        "storage_documents_conflict" or "storage_documents_idempotency_conflict" => "Documents.Error.Conflict",
        "storage_type_in_use" => "Error.TypeInUse",
        "storage_type_version_exists" or "storage_type_version_immutable" => "Error.TypeImmutable",
        "resource_managed" or "resource_governance_authority_required" => "Error.TypeManaged",
        "storage_version_conflict" or "storage_etag_required" => "Error.Conflict",
        "storage_mapping_incomplete" or "storage_export_incompatible" => "Error.Mapping",
        "storage_export_stale" or "storage_exports_stale" => "Error.Export",
        "storage_service_stale" => "Error.ServiceStale",
        "storage_service_unavailable" => "Error.Service",
        "storage_type_stale" or "storage_reconciliation_required" => "Error.Drift",
        "storage_type_unavailable" or "storage_provider_unavailable" => "Error.Provider",
        "storage_configuration_invalid" or "storage_schema_invalid" => "Error.Configuration",
        "storage_in_use" => "Error.InUse",
        "storage_access_denied" => "Error.Forbidden",
        _ => statusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Error.Forbidden",
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.PreconditionRequired => "Error.Conflict",
            HttpStatusCode.NotFound => "Error.NotFound",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "Error.Configuration",
            _ => "Error.Generic"
        }
    };
}
