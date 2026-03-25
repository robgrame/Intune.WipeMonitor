using Intune.WipeMonitor.Agent.Services;
using Intune.WipeMonitor.Shared;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Agent;

/// <summary>
/// Worker service che si connette al SignalR Hub in Azure, riceve i comandi
/// di cleanup e li esegue contro AD e SCCM on-prem.
/// Ogni operazione viene tracciata con custom events su Application Insights.
/// </summary>
public class CleanupAgentWorker : BackgroundService
{
    private readonly AgentSettings _settings;
    private readonly ActiveDirectoryService _adService;
    private readonly SccmService _sccmService;
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<CleanupAgentWorker> _logger;
    private HubConnection? _connection;

    public CleanupAgentWorker(
        IOptions<AgentSettings> settings,
        ActiveDirectoryService adService,
        SccmService sccmService,
        TelemetryClient telemetry,
        ILogger<CleanupAgentWorker> logger)
    {
        _settings = settings.Value;
        _adService = adService;
        _sccmService = sccmService;
        _telemetry = telemetry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent {AgentId} in avvio su {Machine}...",
            _settings.AgentId, Environment.MachineName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connessione al Hub persa. Riconnessione tra 10 secondi...");
                TrackAgentEvent(TelemetryEvents.AgentDisconnected);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        if (_connection is not null)
            await _connection.DisposeAsync();

        _logger.LogInformation("Agent {AgentId} arrestato", _settings.AgentId);
    }

    private async Task ConnectAndRunAsync(CancellationToken stoppingToken)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_settings.HubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Registra gli handler per i comandi dal Hub
        _connection.On<CleanupCommand, CleanupStepResult>(
            nameof(ICleanupAgentClient.RemoveFromActiveDirectory),
            HandleRemoveFromADAsync);

        _connection.On<CleanupCommand, CleanupStepResult>(
            nameof(ICleanupAgentClient.RemoveFromSccm),
            HandleRemoveFromSccmAsync);

        _connection.On<AgentStatus>(
            nameof(ICleanupAgentClient.Ping),
            () => HandlePing());

        _connection.Reconnecting += error =>
        {
            _logger.LogWarning("Riconnessione al Hub in corso... {Error}", error?.Message);
            return Task.CompletedTask;
        };

        _connection.Reconnected += async connectionId =>
        {
            _logger.LogInformation("Riconnesso al Hub con ConnectionId: {ConnectionId}", connectionId);
            await RegisterAsync();
        };

        await _connection.StartAsync(stoppingToken);
        _logger.LogInformation("Connesso al Hub: {Url}", _settings.HubUrl);

        await RegisterAsync();
        TrackAgentEvent(TelemetryEvents.AgentConnected);

        // Heartbeat loop
        while (!stoppingToken.IsCancellationRequested && _connection.State == HubConnectionState.Connected)
        {
            await _connection.InvokeAsync("Heartbeat", _settings.AgentId, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds), stoppingToken);
        }
    }

    private async Task RegisterAsync()
    {
        if (_connection is null) return;

        var canReachAD = _adService.CanConnect();
        var canReachSccm = _sccmService.CanConnect();

        _logger.LogInformation(
            "Verifica connettività: AD {ADStatus}, SCCM {SCCMStatus}",
            canReachAD ? "✅ raggiungibile" : "❌ non raggiungibile",
            canReachSccm ? "✅ raggiungibile" : "❌ non raggiungibile");

        var registration = new AgentRegistration
        {
            AgentId = _settings.AgentId,
            MachineName = Environment.MachineName,
            Version = typeof(CleanupAgentWorker).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            StartedAt = DateTimeOffset.UtcNow,
            CanReachAD = canReachAD,
            CanReachSccm = canReachSccm
        };

        await _connection.InvokeAsync("RegisterAgent", registration);
        _logger.LogInformation("Agent registrato al Hub come {AgentId} (AD: {AD}, SCCM: {SCCM})",
            _settings.AgentId, canReachAD ? "OK" : "FAIL", canReachSccm ? "OK" : "FAIL");
    }

    private async Task<CleanupStepResult> HandleRemoveFromADAsync(CleanupCommand command)
    {
        _logger.LogInformation("[AD] Richiesta rimozione device: {DeviceName} (WipeAction: {WipeActionId})",
            command.DeviceDisplayName, command.WipeActionId);

        var result = await _adService.RemoveComputerAsync(command.DeviceDisplayName);

        // Custom event su App Insights dall'agent
        TrackDeletionEvent(TelemetryEvents.ADDeletion, command, result);

        return result;
    }

    private async Task<CleanupStepResult> HandleRemoveFromSccmAsync(CleanupCommand command)
    {
        _logger.LogInformation("[SCCM] Richiesta rimozione device: {DeviceName} (WipeAction: {WipeActionId})",
            command.DeviceDisplayName, command.WipeActionId);

        var result = await _sccmService.RemoveDeviceAsync(command.DeviceDisplayName);

        // Custom event su App Insights dall'agent
        TrackDeletionEvent(TelemetryEvents.SCCMDeletion, command, result);

        return result;
    }

    private AgentStatus HandlePing()
    {
        return new AgentStatus
        {
            AgentId = _settings.AgentId,
            IsHealthy = true,
            CanReachAD = _adService.CanConnect(),
            CanReachSccm = _sccmService.CanConnect()
        };
    }

    private void TrackDeletionEvent(string eventName, CleanupCommand command, CleanupStepResult result)
    {
        var evt = new EventTelemetry(eventName);
        evt.Properties[TelemetryProps.DeviceName] = command.DeviceDisplayName;
        evt.Properties[TelemetryProps.ManagedDeviceId] = command.ManagedDeviceId;
        evt.Properties[TelemetryProps.WipeActionId] = command.WipeActionId;
        evt.Properties[TelemetryProps.AgentId] = _settings.AgentId;
        evt.Properties[TelemetryProps.AgentMachine] = Environment.MachineName;
        evt.Properties[TelemetryProps.Result] = result.Result.ToString();
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            evt.Properties[TelemetryProps.ErrorMessage] = result.ErrorMessage;

        _telemetry.TrackEvent(evt);

        _logger.LogInformation("[TELEMETRY] {Event}: {Device} → {Result} (agent: {Agent})",
            eventName, command.DeviceDisplayName, result.Result, _settings.AgentId);
    }

    private void TrackAgentEvent(string eventName)
    {
        var evt = new EventTelemetry(eventName);
        evt.Properties[TelemetryProps.AgentId] = _settings.AgentId;
        evt.Properties[TelemetryProps.AgentMachine] = Environment.MachineName;
        _telemetry.TrackEvent(evt);
    }
}
