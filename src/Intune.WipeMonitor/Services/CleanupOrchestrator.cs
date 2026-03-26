using Intune.WipeMonitor.Data;
using Intune.WipeMonitor.Hubs;
using Intune.WipeMonitor.Models;
using Intune.WipeMonitor.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Services;

/// <summary>
/// Orchestratore del processo di cleanup. Invia i comandi di cancellazione AD/SCCM
/// all'agent on-prem via SignalR e gestisce la cancellazione Intune direttamente via Graph.
/// Ogni azione viene tracciata con custom events su Application Insights.
/// </summary>
public class CleanupOrchestrator
{
    private readonly IHubContext<CleanupHub> _hubContext;
    private readonly GraphWipeMonitorService _graphService;
    private readonly CleanupTelemetryService _telemetry;
    private readonly TeamsNotificationService _teamsNotifier;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WipeMonitorSettings _appSettings;
    private readonly ILogger<CleanupOrchestrator> _logger;

    public CleanupOrchestrator(
        IHubContext<CleanupHub> hubContext,
        GraphWipeMonitorService graphService,
        CleanupTelemetryService telemetry,
        TeamsNotificationService teamsNotifier,
        IServiceScopeFactory scopeFactory,
        IOptions<WipeMonitorSettings> appSettings,
        ILogger<CleanupOrchestrator> logger)
    {
        _hubContext = hubContext;
        _graphService = graphService;
        _telemetry = telemetry;
        _teamsNotifier = teamsNotifier;
        _scopeFactory = scopeFactory;
        _appSettings = appSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Esegue il cleanup completo per un device approvato.
    /// Ordine: AD (via agent) → SCCM (via agent) → Intune (via Graph API).
    /// Entra ID si pulisce automaticamente alla prossima sync con AD.
    /// </summary>
    public async Task ExecuteCleanupAsync(string wipeActionId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var record = await db.DeviceCleanupRecords
            .Include(r => r.CleanupSteps)
            .FirstOrDefaultAsync(r => r.WipeActionId == wipeActionId, cancellationToken);

        if (record is null)
        {
            _logger.LogError("Record di cleanup non trovato: {WipeActionId}", wipeActionId);
            return;
        }

        if (record.Status != CleanupStatus.PendingApproval && record.Status != CleanupStatus.Failed)
        {
            _logger.LogWarning("Record {WipeActionId} non è in stato approvabile (stato: {Status})",
                wipeActionId, record.Status);
            return;
        }

        // Verifica che almeno un agent sia connesso — preferisci quello con AD e SCCM raggiungibili
        var activeAgent = CleanupHub.ConnectedAgents.Values
            .Where(a => a.IsAlive)
            .OrderByDescending(a => a.CanReachAD && a.CanReachSccm)
            .FirstOrDefault();
        if (activeAgent is null)
        {
            _logger.LogError("Nessun agent on-prem connesso. Impossibile eseguire cleanup per {DeviceName}",
                record.DeviceDisplayName);
            record.Status = CleanupStatus.Failed;
            record.LastUpdatedAt = DateTimeOffset.UtcNow;
            record.Notes = (record.Notes ?? "") + " [Cleanup fallito: nessun agent on-prem connesso]";
            await db.SaveChangesAsync(cancellationToken);
            _telemetry.TrackCleanupFailed(record, "Nessun agent on-prem connesso");
            return;
        }

        record.Status = CleanupStatus.CleanupInProgress;
        record.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Inizio cleanup per device {DeviceName} via agent {AgentId}",
            record.DeviceDisplayName, activeAgent.AgentId);

        var command = new CleanupCommand
        {
            WipeActionId = record.WipeActionId,
            DeviceDisplayName = record.DeviceDisplayName,
            ManagedDeviceId = record.ManagedDeviceId
        };

        var allSuccess = true;

        // Step 1: Autopilot (direttamente via Graph API dal cloud — primo step)
        var (autopilotOk, autopilotNotFound) = await _graphService.DeleteAutopilotDeviceAsync(
            record.DeviceDisplayName, record.ManagedDeviceId, cancellationToken);
        var autopilotResult = autopilotNotFound
            ? CleanupStepResult.NotFound($"Device '{record.DeviceDisplayName}' non registrato in Autopilot")
            : autopilotOk
                ? CleanupStepResult.Success()
                : CleanupStepResult.Failed("Errore durante la rimozione da Autopilot");
        allSuccess &= await PersistStepResultAsync(db, record, CleanupTarget.Autopilot, autopilotResult, cancellationToken);
        _telemetry.TrackAutopilotDeletion(record, autopilotResult);

        // Step 2: Active Directory (via agent on-prem)
        var adResult = await InvokeAgentStepAsync(
            activeAgent.ConnectionId, CleanupTarget.ActiveDirectory,
            nameof(ICleanupAgentClient.RemoveFromActiveDirectory), command);
        allSuccess &= await PersistStepResultAsync(db, record, CleanupTarget.ActiveDirectory, adResult, cancellationToken);
        _telemetry.TrackADDeletion(record, adResult);

        // Step 3: SCCM (via agent on-prem)
        var sccmResult = await InvokeAgentStepAsync(
            activeAgent.ConnectionId, CleanupTarget.SCCM,
            nameof(ICleanupAgentClient.RemoveFromSccm), command);
        allSuccess &= await PersistStepResultAsync(db, record, CleanupTarget.SCCM, sccmResult, cancellationToken);
        _telemetry.TrackSCCMDeletion(record, sccmResult);

        // Step 4: Intune (direttamente via Graph API dal cloud — ultimo step)
        if (_appSettings.CleanupIntune)
        {
            var intuneSuccess = await _graphService.DeleteIntuneDeviceAsync(record.ManagedDeviceId, cancellationToken);
            var intuneResult = intuneSuccess
                ? CleanupStepResult.Success()
                : CleanupStepResult.Failed("Errore durante la rimozione da Intune");
            allSuccess &= await PersistStepResultAsync(db, record, CleanupTarget.Intune, intuneResult, cancellationToken);
            _telemetry.TrackIntuneDeletion(record, intuneResult);
        }

        // Aggiorna stato finale e traccia evento complessivo
        record.Status = allSuccess ? CleanupStatus.Completed : CleanupStatus.Failed;
        record.CleanupCompletedAt = DateTimeOffset.UtcNow;
        record.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (allSuccess)
            _telemetry.TrackCleanupCompleted(record);
        else
            _telemetry.TrackCleanupFailed(record);

        await _teamsNotifier.NotifyCleanupCompletedAsync(record, allSuccess);
    }

    /// <summary>
    /// Invoca un metodo sull'agent on-prem via SignalR con timeout.
    /// Usa InvokeAsync diretto (non il typed client) per supportare client results.
    /// </summary>
    private async Task<CleanupStepResult> InvokeAgentStepAsync(
        string connectionId,
        CleanupTarget target,
        string methodName,
        CleanupCommand command)
    {
        try
        {
            var client = _hubContext.Clients.Client(connectionId);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            _logger.LogInformation("Invocazione {Method} su agent per device {Device}",
                methodName, command.DeviceDisplayName);

            var result = await client.InvokeAsync<CleanupStepResult>(
                methodName, command, cts.Token);
            return result ?? CleanupStepResult.Failed($"Agent ha ritornato null per {target}");
        }
        catch (OperationCanceledException)
        {
            return CleanupStepResult.Failed($"Timeout: l'agent non ha risposto entro 2 minuti per {target}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore comunicazione con agent per step {Target}", target);
            return CleanupStepResult.Failed($"Errore comunicazione SignalR: {ex.Message}");
        }
    }

    private async Task<bool> PersistStepResultAsync(
        AppDbContext db,
        DeviceCleanupRecord record,
        CleanupTarget target,
        CleanupStepResult result,
        CancellationToken cancellationToken)
    {
        var stepLog = new CleanupStepLog
        {
            WipeActionId = record.WipeActionId,
            Target = target,
            Result = result.Result,
            ErrorMessage = result.ErrorMessage,
            ExecutedAt = result.ExecutedAt
        };

        db.CleanupStepLogs.Add(stepLog);
        await db.SaveChangesAsync(cancellationToken);

        return result.Result is StepResult.Success or StepResult.NotFound;
    }

    /// <summary>Approva un device per il cleanup.</summary>
    public async Task<bool> ApproveCleanupAsync(string wipeActionId, string approvedBy, string? notes = null, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var record = await db.DeviceCleanupRecords
            .FirstOrDefaultAsync(r => r.WipeActionId == wipeActionId, cancellationToken);

        if (record is null || record.Status != CleanupStatus.PendingApproval)
            return false;

        record.ApprovedBy = approvedBy;
        record.ApprovedAt = DateTimeOffset.UtcNow;
        record.Notes = notes;
        record.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _telemetry.TrackCleanupApproved(record);

        await ExecuteCleanupAsync(wipeActionId, cancellationToken);
        return true;
    }

    /// <summary>Segna un device come "Skipped".</summary>
    public async Task<bool> SkipCleanupAsync(string wipeActionId, string skippedBy, string? reason = null, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var record = await db.DeviceCleanupRecords
            .FirstOrDefaultAsync(r => r.WipeActionId == wipeActionId, cancellationToken);

        if (record is null || record.Status != CleanupStatus.PendingApproval)
            return false;

        record.Status = CleanupStatus.Skipped;
        record.ApprovedBy = skippedBy;
        record.Notes = reason;
        record.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _telemetry.TrackCleanupSkipped(record);
        return true;
    }

    /// <summary>Ritenta il cleanup per un device fallito.</summary>
    public async Task<bool> RetryCleanupAsync(string wipeActionId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var record = await db.DeviceCleanupRecords
            .FirstOrDefaultAsync(r => r.WipeActionId == wipeActionId, cancellationToken);

        if (record is null || record.Status != CleanupStatus.Failed)
            return false;

        await ExecuteCleanupAsync(wipeActionId, cancellationToken);
        return true;
    }
}
