using System.Text.Json.Serialization;

namespace Intune.WipeMonitor.Models;

/// <summary>
/// Rappresenta un'azione di wipe (factory reset) recuperata da Graph API.
/// Mappato da: GET /beta/deviceManagement/remoteActionAudits?$filter=action eq 'factoryReset'
/// </summary>
public class WipeAction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("deviceDisplayName")]
    public string DeviceDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("initiatedByUserPrincipalName")]
    public string InitiatedByUserPrincipalName { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("requestDateTime")]
    public DateTimeOffset RequestDateTime { get; set; }

    [JsonPropertyName("deviceOwnerUserPrincipalName")]
    public string DeviceOwnerUserPrincipalName { get; set; } = string.Empty;

    [JsonPropertyName("deviceIMEI")]
    public string DeviceIMEI { get; set; } = string.Empty;

    [JsonPropertyName("actionState")]
    public string ActionState { get; set; } = string.Empty;

    [JsonPropertyName("managedDeviceId")]
    public string ManagedDeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceActionCategory")]
    public string DeviceActionCategory { get; set; } = string.Empty;

    [JsonPropertyName("bulkDeviceActionId")]
    public string BulkDeviceActionId { get; set; } = string.Empty;

    /// <summary>Indica se il wipe è stato completato con successo.</summary>
    [JsonIgnore]
    public bool IsCompleted => string.Equals(ActionState, "done", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Wrapper per la risposta paginata di Graph API.
/// </summary>
public class GraphApiResponse<T>
{
    [JsonPropertyName("@odata.context")]
    public string? ODataContext { get; set; }

    [JsonPropertyName("@odata.count")]
    public int? ODataCount { get; set; }

    [JsonPropertyName("@odata.nextLink")]
    public string? ODataNextLink { get; set; }

    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];
}
