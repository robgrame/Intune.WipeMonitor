namespace Intune.WipeMonitor.Report.Models;

public class ReportSettings
{
    public const string SectionName = "Report";

    /// <summary>SharePoint site ID (from Graph API).</summary>
    public string SharePointSiteId { get; set; } = string.Empty;

    /// <summary>SharePoint drive ID for the document library.</summary>
    public string SharePointDriveId { get; set; } = string.Empty;

    /// <summary>Folder path within the document library.</summary>
    public string SharePointFolderPath { get; set; } = "WipeMonitor Reports";

    /// <summary>Teams Power Automate webhook URL for Adaptive Card notifications.</summary>
    public string TeamsWebhookUrl { get; set; } = string.Empty;

    /// <summary>Number of days to include in the report (default 30).</summary>
    public int ReportDays { get; set; } = 30;
}

public class GraphSettings
{
    public const string SectionName = "Graph";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = "https://graph.microsoft.com/.default";
}
