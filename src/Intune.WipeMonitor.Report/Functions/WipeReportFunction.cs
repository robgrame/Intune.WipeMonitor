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
        _logger.LogInformation("=== Wipe Report — Inizio generazione ===");

        // 1. Recupera dati: wipe actions + cross-reference Entra ID
        var entries = await _graphService.BuildReportDataAsync(ct);

        if (entries.Count == 0)
        {
            _logger.LogInformation("Nessuna wipe action trovata negli ultimi {Days} giorni. Report non generato.",
                _settings.ReportDays);
            return;
        }

        // 2. Genera Excel
        var excelBytes = _excelBuilder.Build(entries, _settings.ReportDays);
        var fileName = $"WipeReport_{DateTime.UtcNow:yyyy-MM-dd}.xlsx";

        // 3. Upload su SharePoint
        var reportUrl = await _sharePointService.UploadReportAsync(excelBytes, fileName, ct);

        // 4. Notifica Teams
        await _teamsNotifier.NotifyAsync(entries, reportUrl, ct);

        _logger.LogInformation("=== Wipe Report completato: {Count} device, report: {Url} ===",
            entries.Count, reportUrl);
    }
}
