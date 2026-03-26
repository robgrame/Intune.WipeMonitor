using Intune.WipeMonitor.Report.Models;
using Intune.WipeMonitor.Report.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Report.Functions;

/// <summary>
/// Timer trigger settimanale: recupera le wipe actions, genera il report Excel,
/// lo carica su SharePoint e invia una notifica Teams.
/// </summary>
public class WipeReportFunction
{
    private readonly GraphReportService _graphService;
    private readonly ExcelReportBuilder _excelBuilder;
    private readonly SharePointUploadService _sharePointService;
    private readonly TeamsReportNotifier _teamsNotifier;
    private readonly ReportSettings _settings;
    private readonly ILogger<WipeReportFunction> _logger;

    public WipeReportFunction(
        GraphReportService graphService,
        ExcelReportBuilder excelBuilder,
        SharePointUploadService sharePointService,
        TeamsReportNotifier teamsNotifier,
        IOptions<ReportSettings> settings,
        ILogger<WipeReportFunction> logger)
    {
        _graphService = graphService;
        _excelBuilder = excelBuilder;
        _sharePointService = sharePointService;
        _teamsNotifier = teamsNotifier;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Ogni lunedì alle 08:00 UTC. CRON: sec min hour day month dayOfWeek
    /// </summary>
    [Function("WipeReport")]
    public async Task Run(
        [TimerTrigger("0 0 8 * * 1")] TimerInfo timer,
        CancellationToken ct)
    {
        _logger.LogInformation("=== Wipe Report — Inizio generazione (ultimi {Days} giorni) ===",
            _settings.ReportDays);

        // 1. Recupera dati: wipe actions + cross-reference Entra ID
        List<WipeReportEntry> entries;
        try
        {
            entries = await _graphService.BuildReportDataAsync(ct);
            _logger.LogInformation("[STEP 1/4] Dati recuperati: {Count} wipe actions, {Pending} pending Entra",
                entries.Count, entries.Count(e => e.IsEntraPending));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STEP 1/4 FAILED] Errore recupero dati da Graph API");
            throw;
        }

        if (entries.Count == 0)
        {
            _logger.LogInformation("Nessuna wipe action trovata. Report non generato.");
            return;
        }

        // 2. Genera Excel
        byte[] excelBytes;
        var fileName = $"WipeReport_{DateTime.UtcNow:yyyy-MM-dd}.xlsx";
        try
        {
            excelBytes = _excelBuilder.Build(entries, _settings.ReportDays);
            _logger.LogInformation("[STEP 2/4] Excel generato: {FileName} ({Size} KB)",
                fileName, excelBytes.Length / 1024);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STEP 2/4 FAILED] Errore generazione Excel");
            throw;
        }

        // 3. Upload su SharePoint
        string reportUrl;
        try
        {
            reportUrl = await _sharePointService.UploadReportAsync(excelBytes, fileName, ct);
            _logger.LogInformation("[STEP 3/4] Upload SharePoint completato: {Url}", reportUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STEP 3/4 FAILED] Errore upload su SharePoint (Site: {SiteId}, Drive: {DriveId})",
                _settings.SharePointSiteId, _settings.SharePointDriveId);
            throw;
        }

        // 4. Notifica Teams
        try
        {
            await _teamsNotifier.NotifyAsync(entries, reportUrl, ct);
            _logger.LogInformation("[STEP 4/4] Notifica Teams inviata");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STEP 4/4 FAILED] Errore invio notifica Teams");
            throw;
        }

        _logger.LogInformation("=== Wipe Report completato: {Count} device, {Pending} pending Entra, report: {Url} ===",
            entries.Count, entries.Count(e => e.IsEntraPending), reportUrl);
    }
}
