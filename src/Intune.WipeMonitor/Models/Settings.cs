namespace Intune.WipeMonitor.Models;

/// <summary>
/// Configurazione dell'applicazione, mappata da appsettings.json.
/// </summary>
public class WipeMonitorSettings
{
    public const string SectionName = "WipeMonitor";

    /// <summary>Intervallo di polling in minuti.</summary>
    public int PollingIntervalMinutes { get; set; } = 60;

    /// <summary>Se true, richiede approvazione manuale prima del cleanup.</summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>Elimina anche l'oggetto da Intune dopo il wipe (opzionale).</summary>
    public bool CleanupIntune { get; set; } = true;

    /// <summary>Teams Incoming Webhook URL per notifiche. Se vuoto, notifiche disabilitate.</summary>
    public string? TeamsWebhookUrl { get; set; }

    /// <summary>URL base della dashboard (per i link nelle notifiche Teams).</summary>
    public string? DashboardUrl { get; set; }

    /// <summary>API Key per l'autenticazione dell'agent on-prem al SignalR Hub.</summary>
    public string? AgentApiKey { get; set; }
}

public class GraphSettings
{
    public const string SectionName = "Graph";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Scope per Graph API (default: https://graph.microsoft.com/.default).</summary>
    public string Scope { get; set; } = "https://graph.microsoft.com/.default";
}

public class ActiveDirectorySettings
{
    public const string SectionName = "ActiveDirectory";

    /// <summary>LDAP server (es. ldap://dc01.contoso.com).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Search base DN (es. DC=contoso,DC=com).</summary>
    public string SearchBase { get; set; } = string.Empty;

    /// <summary>Username per binding LDAP (opzionale, usa credenziali processo se vuoto).</summary>
    public string? Username { get; set; }

    /// <summary>Password per binding LDAP.</summary>
    public string? Password { get; set; }

    /// <summary>Usa SSL per la connessione LDAP.</summary>
    public bool UseSsl { get; set; }

    /// <summary>Porta LDAP (default 389, 636 per SSL).</summary>
    public int Port { get; set; } = 389;
}

public class SccmSettings
{
    public const string SectionName = "SCCM";

    /// <summary>URL base dell'AdminService (es. https://sccm-server.contoso.com/AdminService).</summary>
    public string AdminServiceUrl { get; set; } = string.Empty;

    /// <summary>Username per autenticazione AdminService (opzionale).</summary>
    public string? Username { get; set; }

    /// <summary>Password per autenticazione AdminService.</summary>
    public string? Password { get; set; }

    /// <summary>Se true, ignora errori di certificato SSL (solo dev/test).</summary>
    public bool IgnoreSslErrors { get; set; }
}
