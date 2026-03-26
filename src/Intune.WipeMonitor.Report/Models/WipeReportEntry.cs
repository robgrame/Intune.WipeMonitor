namespace Intune.WipeMonitor.Report.Models;

/// <summary>
/// Rappresenta una riga del report: un device wipato con lo stato Entra ID.
/// </summary>
public class WipeReportEntry
{
    // Dati dal wipe action (Graph remoteActionAudits)
    public string DeviceName { get; set; } = string.Empty;
    public string ManagedDeviceId { get; set; } = string.Empty;
    public string ActionState { get; set; } = string.Empty;
    public DateTimeOffset? WipeRequestedAt { get; set; }
    public DateTimeOffset? WipeCompletedAt { get; set; }
    public string InitiatedBy { get; set; } = string.Empty;

    // Dati da Intune managed device (arricchimento)
    public bool IntuneDeviceFound { get; set; }
    public string? AzureADDeviceId { get; set; }
    public string? IntuneUser { get; set; }
    public string? IntuneSerialNumber { get; set; }

    // Dati da Entra ID (Graph /devices)
    public bool EntraDeviceFound { get; set; }
    public string? EntraDeviceId { get; set; }
    public string? EntraDisplayName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OperatingSystemVersion { get; set; }
    public string? TrustType { get; set; }
    public DateTimeOffset? EntraLastSignIn { get; set; }
    public bool? EntraAccountEnabled { get; set; }
    public string? EntraDeviceOwner { get; set; }

    /// <summary>Device wipato con oggetto Entra ancora presente → pronto per cleanup.</summary>
    public bool IsEntraPending => EntraDeviceFound && ActionState == "done";
}

/// <summary>Risposta paginata Graph API.</summary>
public class GraphPagedResponse<T>
{
    public List<T> Value { get; set; } = new();
    public string? ODataNextLink { get; set; }
}

/// <summary>Wipe action da Graph remoteActionAudits.</summary>
public class WipeActionDto
{
    public string Id { get; set; } = string.Empty;
    public string DeviceDisplayName { get; set; } = string.Empty;
    public string ManagedDeviceId { get; set; } = string.Empty;
    public string ActionState { get; set; } = string.Empty;
    public DateTimeOffset? RequestDateTime { get; set; }
    public DateTimeOffset? LastUpdatedDateTime { get; set; }
    public string? InitiatedByUserPrincipalName { get; set; }
}

/// <summary>Device da Graph /devices endpoint.</summary>
public class EntraDeviceDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OperatingSystemVersion { get; set; }
    public string? TrustType { get; set; }
    public bool AccountEnabled { get; set; }
    public DateTimeOffset? ApproximateLastSignInDateTime { get; set; }
}

/// <summary>Managed device da Intune.</summary>
public class ManagedDeviceDto
{
    public string Id { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? AzureADDeviceId { get; set; }
    public string? SerialNumber { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? UserPrincipalName { get; set; }
}
