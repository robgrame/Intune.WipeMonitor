namespace Intune.WipeMonitor.Shared;

/// <summary>
/// Nomi degli eventi custom emessi su Application Insights.
/// Ogni evento rappresenta un'azione chiara e tracciabile nel ciclo di vita del cleanup.
/// 
/// Querying in KQL:
///   customEvents | where name startswith "DeviceCleanup"
///   customEvents | where name == "DeviceCleanup.ADDeletion" and customDimensions.Result == "Failed"
/// </summary>
public static class TelemetryEvents
{
    /// <summary>Operatore ha approvato il cleanup di un device.</summary>
    public const string CleanupApproved = "DeviceCleanup.Approved";

    /// <summary>Operatore ha saltato il cleanup di un device.</summary>
    public const string CleanupSkipped = "DeviceCleanup.Skipped";

    /// <summary>Tentativo di cancellazione da Windows Autopilot.</summary>
    public const string AutopilotDeletion = "DeviceCleanup.AutopilotDeletion";

    /// <summary>Tentativo di cancellazione da Active Directory.</summary>
    public const string ADDeletion = "DeviceCleanup.ADDeletion";

    /// <summary>Tentativo di cancellazione da SCCM.</summary>
    public const string SCCMDeletion = "DeviceCleanup.SCCMDeletion";

    /// <summary>Tentativo di cancellazione da Intune.</summary>
    public const string IntuneDeletion = "DeviceCleanup.IntuneDeletion";

    /// <summary>Cleanup completato con successo su tutti i target.</summary>
    public const string CleanupCompleted = "DeviceCleanup.Completed";

    /// <summary>Cleanup fallito su almeno un target.</summary>
    public const string CleanupFailed = "DeviceCleanup.Failed";

    /// <summary>Agent on-prem connesso al Hub.</summary>
    public const string AgentConnected = "Agent.Connected";

    /// <summary>Agent on-prem disconnesso dal Hub.</summary>
    public const string AgentDisconnected = "Agent.Disconnected";

    /// <summary>Polling Graph API completato.</summary>
    public const string WipePollCompleted = "WipePoll.Completed";

    /// <summary>Nuovo device con wipe completato rilevato.</summary>
    public const string WipeDetected = "WipePoll.WipeDetected";
}

/// <summary>
/// Chiavi standard per le custom dimensions degli eventi.
/// Garantiscono consistenza nelle query KQL.
/// </summary>
public static class TelemetryProps
{
    public const string DeviceName = "DeviceName";
    public const string ManagedDeviceId = "ManagedDeviceId";
    public const string WipeActionId = "WipeActionId";
    public const string DeviceOwner = "DeviceOwner";
    public const string InitiatedBy = "InitiatedBy";
    public const string ApprovedBy = "ApprovedBy";
    public const string Result = "Result";
    public const string ErrorMessage = "ErrorMessage";
    public const string Target = "Target";
    public const string AgentId = "AgentId";
    public const string AgentMachine = "AgentMachine";
    public const string DevicesFound = "DevicesFound";
    public const string NewDevices = "NewDevices";
    public const string StatusChanged = "StatusChanged";
}
