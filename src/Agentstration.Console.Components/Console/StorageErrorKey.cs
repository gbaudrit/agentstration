using System.Net;

namespace Agentstration.Web.Console;

public static class StorageErrorKey
{
    public static string For(Exception error) => error is AgentstrationApiException api ? api.ProblemTitle switch
    {
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
        _ => api.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Error.Forbidden",
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.PreconditionRequired => "Error.Conflict",
            HttpStatusCode.NotFound => "Error.NotFound",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "Error.Configuration",
            _ => "Error.Generic"
        }
    } : "Error.Generic";
}
