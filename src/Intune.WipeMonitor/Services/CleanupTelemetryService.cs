using Intune.WipeMonitor.Hubs;
using Intune.WipeMonitor.Models;
using Intune.WipeMonitor.Shared;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

namespace Intune.WipeMonitor.Services;

/// <summary>
/// Servizio dedicato all'emissione di custom events su Application Insights
/// per tracciare in modo chiaro e auditabile tutte le azioni di cleanup.
/// </summary>
public class CleanupTelemetryService
{
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<CleanupTelemetryService> _logger;

    public CleanupTelemetryService(TelemetryClient telemetry, ILogger<CleanupTelemetryService> logger)
    {
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>Traccia l'approvazione di un cleanup da parte di un operatore.</summary>
    public void TrackCleanupApproved(DeviceCleanupRecord record)
    {
        var evt = new EventTelemetry(TelemetryEvents.CleanupApproved);
        AddDeviceProperties(evt, record);
        evt.Properties[TelemetryProps.ApprovedBy] = record.ApprovedBy ?? "unknown";
        _telemetry.TrackEvent(evt);

        _logger.LogInformation(
            "[TELEMETRY] {Event}: {Device} approvato da {ApprovedBy}",
            TelemetryEvents.CleanupApproved, record.DeviceDisplayName, record.ApprovedBy);
    }

    /// <summary>Traccia un cleanup saltato (skipped).</summary>
    public void TrackCleanupSkipped(DeviceCleanupRecord record)
    {
        var evt = new EventTelemetry(TelemetryEvents.CleanupSkipped);
        AddDeviceProperties(evt, record);
        evt.Properties[TelemetryProps.ApprovedBy] = record.ApprovedBy ?? "unknown";
        if (!string.IsNullOrEmpty(record.Notes))
            evt.Properties["Reason"] = record.Notes;
        _telemetry.TrackEvent(evt);
    }

    /// <summary>Traccia il tentativo di cancellazione da AD con esito.</summary>
    public void TrackADDeletion(DeviceCleanupRecord record, CleanupStepResult result)
    {
        TrackDeletionStep(TelemetryEvents.ADDeletion, CleanupTarget.ActiveDirectory, record, result);
    }

    /// <summary>Traccia il tentativo di cancellazione da SCCM con esito.</summary>
    public void TrackSCCMDeletion(DeviceCleanupRecord record, CleanupStepResult result)
    {
        TrackDeletionStep(TelemetryEvents.SCCMDeletion, CleanupTarget.SCCM, record, result);
    }

    /// <summary>Traccia il tentativo di cancellazione da Intune con esito.</summary>
    public void TrackIntuneDeletion(DeviceCleanupRecord record, CleanupStepResult result)
    {
        TrackDeletionStep(TelemetryEvents.IntuneDeletion, CleanupTarget.Intune, record, result);
    }

    /// <summary>Traccia il completamento con successo del cleanup completo.</summary>
    public void TrackCleanupCompleted(DeviceCleanupRecord record)
    {
        var evt = new EventTelemetry(TelemetryEvents.CleanupCompleted);
        AddDeviceProperties(evt, record);
        evt.Properties[TelemetryProps.Result] = "Success";
        _telemetry.TrackEvent(evt);

        _logger.LogInformation(
            "[TELEMETRY] {Event}: cleanup completato per {Device}",
            TelemetryEvents.CleanupCompleted, record.DeviceDisplayName);
    }

    /// <summary>Traccia il fallimento del cleanup.</summary>
    public void TrackCleanupFailed(DeviceCleanupRecord record, string? reason = null)
    {
        var evt = new EventTelemetry(TelemetryEvents.CleanupFailed);
        AddDeviceProperties(evt, record);
        evt.Properties[TelemetryProps.Result] = "Failed";
        if (!string.IsNullOrEmpty(reason))
            evt.Properties[TelemetryProps.ErrorMessage] = reason;
        _telemetry.TrackEvent(evt);

        _logger.LogWarning(
            "[TELEMETRY] {Event}: cleanup fallito per {Device}: {Reason}",
            TelemetryEvents.CleanupFailed, record.DeviceDisplayName, reason ?? "unknown");
    }

    /// <summary>Traccia la connessione di un agent on-prem.</summary>
    public void TrackAgentConnected(ConnectedAgent agent)
    {
        var evt = new EventTelemetry(TelemetryEvents.AgentConnected);
        evt.Properties[TelemetryProps.AgentId] = agent.AgentId;
        evt.Properties[TelemetryProps.AgentMachine] = agent.MachineName;
        _telemetry.TrackEvent(evt);
    }

    /// <summary>Traccia la disconnessione di un agent on-prem.</summary>
    public void TrackAgentDisconnected(string agentId)
    {
        var evt = new EventTelemetry(TelemetryEvents.AgentDisconnected);
        evt.Properties[TelemetryProps.AgentId] = agentId;
        _telemetry.TrackEvent(evt);
    }

    /// <summary>Traccia il completamento di un ciclo di polling Graph API.</summary>
    public void TrackWipePollCompleted(int devicesFound, int newDevices, int statusChanged)
    {
        var evt = new EventTelemetry(TelemetryEvents.WipePollCompleted);
        evt.Properties[TelemetryProps.DevicesFound] = devicesFound.ToString();
        evt.Properties[TelemetryProps.NewDevices] = newDevices.ToString();
        evt.Properties[TelemetryProps.StatusChanged] = statusChanged.ToString();
        _telemetry.TrackEvent(evt);
        _telemetry.GetMetric("WipePoll.DevicesFound").TrackValue(devicesFound);
        _telemetry.GetMetric("WipePoll.NewDevices").TrackValue(newDevices);
        _telemetry.GetMetric("WipePoll.StatusChanged").TrackValue(statusChanged);
    }

    /// <summary>Traccia un nuovo device con wipe completato.</summary>
    public void TrackWipeDetected(DeviceCleanupRecord record)
    {
        var evt = new EventTelemetry(TelemetryEvents.WipeDetected);
        AddDeviceProperties(evt, record);
        evt.Properties["WipeState"] = record.WipeActionState;
        _telemetry.TrackEvent(evt);
    }

    private void TrackDeletionStep(string eventName, CleanupTarget target, DeviceCleanupRecord record, CleanupStepResult result)
    {
        var evt = new EventTelemetry(eventName);
        AddDeviceProperties(evt, record);
        evt.Properties[TelemetryProps.Target] = target.ToString();
        evt.Properties[TelemetryProps.Result] = result.Result.ToString();
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            evt.Properties[TelemetryProps.ErrorMessage] = result.ErrorMessage;

        _telemetry.TrackEvent(evt);

        _logger.LogInformation(
            "[TELEMETRY] {Event}: {Device} → {Result} {Error}",
            eventName, record.DeviceDisplayName, result.Result,
            result.ErrorMessage ?? "");
    }

    private static void AddDeviceProperties(EventTelemetry evt, DeviceCleanupRecord record)
    {
        evt.Properties[TelemetryProps.DeviceName] = record.DeviceDisplayName;
        evt.Properties[TelemetryProps.ManagedDeviceId] = record.ManagedDeviceId;
        evt.Properties[TelemetryProps.WipeActionId] = record.WipeActionId;
        evt.Properties[TelemetryProps.DeviceOwner] = record.DeviceOwner;
        evt.Properties[TelemetryProps.InitiatedBy] = record.InitiatedBy;
    }
}
