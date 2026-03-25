using Intune.WipeMonitor.Models;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Services;

/// <summary>
/// Background service che esegue il polling periodico delle azioni di wipe
/// da Microsoft Graph API e gestisce il ciclo di vita del cleanup.
/// </summary>
public class WipeMonitorBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WipeMonitorBackgroundService> _logger;
    private readonly WipeMonitorSettings _settings;

    public WipeMonitorBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<WipeMonitorSettings> settings,
        ILogger<WipeMonitorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Indica se il servizio di polling è attivo.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Timestamp dell'ultimo polling eseguito.</summary>
    public DateTimeOffset? LastPollTime { get; private set; }

    /// <summary>Eventuale errore dell'ultimo polling.</summary>
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WipeMonitor Background Service avviato. Intervallo polling: {Interval} minuti",
            _settings.PollingIntervalMinutes);

        IsRunning = true;

        // Attendi un breve periodo al startup per permettere all'app di inizializzarsi
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var graphService = scope.ServiceProvider.GetRequiredService<GraphWipeMonitorService>();

                await graphService.PollWipeActionsAsync(stoppingToken);

                LastPollTime = DateTimeOffset.UtcNow;
                LastError = null;

                _logger.LogInformation("Polling completato con successo alle {Time}", LastPollTime);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogError(ex, "Errore durante il ciclo di polling");
            }

            await Task.Delay(TimeSpan.FromMinutes(_settings.PollingIntervalMinutes), stoppingToken);
        }

        IsRunning = false;
        _logger.LogInformation("WipeMonitor Background Service fermato");
    }
}
