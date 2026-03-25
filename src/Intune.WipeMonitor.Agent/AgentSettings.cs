namespace Intune.WipeMonitor.Agent;

/// <summary>
/// Configurazione dell'agent on-prem.
/// </summary>
public class AgentSettings
{
    public const string SectionName = "Agent";

    /// <summary>ID univoco dell'agent.</summary>
    public string AgentId { get; set; } = Environment.MachineName;

    /// <summary>URL del SignalR Hub (es. https://intune-wipemonitor-app.azurewebsites.net/hub/cleanup).</summary>
    public string HubUrl { get; set; } = string.Empty;

    /// <summary>Intervallo heartbeat in secondi.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>Configurazione Active Directory.</summary>
    public ADSettings ActiveDirectory { get; set; } = new();

    /// <summary>Configurazione SCCM.</summary>
    public SccmConfig Sccm { get; set; } = new();
}

public class ADSettings
{
    public string Server { get; set; } = string.Empty;
    public string SearchBase { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; }
    public int Port { get; set; } = 389;
}

public class SccmConfig
{
    public string AdminServiceUrl { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IgnoreSslErrors { get; set; }
}
