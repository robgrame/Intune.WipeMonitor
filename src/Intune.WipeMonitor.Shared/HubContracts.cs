namespace Intune.WipeMonitor.Shared;

/// <summary>
/// Contratto SignalR: metodi invocabili sull'agent on-prem dal Hub (server → client).
/// </summary>
public interface ICleanupAgentClient
{
    /// <summary>Richiede la rimozione di un device da Active Directory.</summary>
    Task<CleanupStepResult> RemoveFromActiveDirectory(CleanupCommand command);

    /// <summary>Richiede la rimozione di un device da SCCM.</summary>
    Task<CleanupStepResult> RemoveFromSccm(CleanupCommand command);

    /// <summary>Ping per verificare che l'agent sia connesso e operativo.</summary>
    Task<AgentStatus> Ping();
}

/// <summary>
/// Contratto SignalR: metodi invocabili sul Hub dall'agent (client → server).
/// </summary>
public interface ICleanupHub
{
    /// <summary>L'agent si registra al Hub con le proprie informazioni.</summary>
    Task RegisterAgent(AgentRegistration registration);

    /// <summary>L'agent riporta il completamento di uno step di cleanup.</summary>
    Task ReportStepCompleted(string wipeActionId, CleanupTarget target, CleanupStepResult result);

    /// <summary>L'agent invia un heartbeat periodico.</summary>
    Task Heartbeat(string agentId);
}

/// <summary>Comando di cleanup inviato all'agent.</summary>
public class CleanupCommand
{
    public string WipeActionId { get; set; } = string.Empty;
    public string DeviceDisplayName { get; set; } = string.Empty;
    public string ManagedDeviceId { get; set; } = string.Empty;
    /// <summary>Entra Device ID (objectId) per cross-validazione SID.</summary>
    public string EntraDeviceId { get; set; } = string.Empty;
}

/// <summary>Risultato di uno step di cleanup dall'agent.</summary>
public class CleanupStepResult
{
    public StepResult Result { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>SID del computer object validato durante il cleanup (per audit trail).</summary>
    public string? MatchedSid { get; set; }
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;

    public static CleanupStepResult Success(string? sid = null) => new() { Result = StepResult.Success, MatchedSid = sid };
    public static CleanupStepResult NotFound(string msg) => new() { Result = StepResult.NotFound, ErrorMessage = msg };
    public static CleanupStepResult Failed(string msg) => new() { Result = StepResult.Failed, ErrorMessage = msg };
    public static CleanupStepResult SidMismatch(string msg, string? sid = null) => new() { Result = StepResult.SidMismatch, ErrorMessage = msg, MatchedSid = sid };
}

/// <summary>Informazioni di registrazione dell'agent.</summary>
public class AgentRegistration
{
    public string AgentId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>L'agent riesce a raggiungere il Domain Controller via LDAP.</summary>
    public bool CanReachAD { get; set; }
    /// <summary>L'agent riesce a raggiungere SCCM AdminService via HTTPS.</summary>
    public bool CanReachSccm { get; set; }
}

/// <summary>Stato dell'agent.</summary>
public class AgentStatus
{
    public string AgentId { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public bool CanReachAD { get; set; }
    public bool CanReachSccm { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
