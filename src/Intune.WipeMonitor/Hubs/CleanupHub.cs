using System.Collections.Concurrent;
using Intune.WipeMonitor.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Intune.WipeMonitor.Hubs;

/// <summary>
/// SignalR Hub che funge da gateway tra il cloud (dashboard/orchestrator)
/// e l'agent on-premises che esegue le operazioni su AD e SCCM.
/// </summary>
public class CleanupHub : Hub<ICleanupAgentClient>, ICleanupHub
{
    private static readonly ConcurrentDictionary<string, ConnectedAgent> _connectedAgents = new();
    private readonly ILogger<CleanupHub> _logger;

    public CleanupHub(ILogger<CleanupHub> logger)
    {
        _logger = logger;
    }

    /// <summary>Agenti attualmente connessi.</summary>
    public static IReadOnlyDictionary<string, ConnectedAgent> ConnectedAgents => _connectedAgents;

    /// <summary>Registra un agent on-prem che si connette al Hub.</summary>
    public Task RegisterAgent(AgentRegistration registration)
    {
        var agent = new ConnectedAgent
        {
            ConnectionId = Context.ConnectionId,
            AgentId = registration.AgentId,
            MachineName = registration.MachineName,
            Version = registration.Version,
            ConnectedAt = DateTimeOffset.UtcNow,
            LastHeartbeat = DateTimeOffset.UtcNow,
            CanReachAD = registration.CanReachAD,
            CanReachSccm = registration.CanReachSccm
        };

        _connectedAgents.AddOrUpdate(registration.AgentId, agent, (_, _) => agent);

        _logger.LogInformation(
            "Agent registrato: {AgentId} su {MachineName} (v{Version}), AD: {AD}, SCCM: {SCCM}, ConnectionId: {ConnectionId}",
            registration.AgentId, registration.MachineName, registration.Version,
            registration.CanReachAD, registration.CanReachSccm,
            Context.ConnectionId);

        return Task.CompletedTask;
    }

    /// <summary>Riceve il risultato di uno step di cleanup dall'agent.</summary>
    public Task ReportStepCompleted(string wipeActionId, CleanupTarget target, CleanupStepResult result)
    {
        _logger.LogInformation(
            "Step completato: {WipeActionId} - {Target} → {Result} (errore: {Error})",
            wipeActionId, target, result.Result, result.ErrorMessage ?? "nessuno");

        return Task.CompletedTask;
    }

    /// <summary>Heartbeat dall'agent per segnalare che è ancora vivo.</summary>
    public Task Heartbeat(string agentId)
    {
        if (_connectedAgents.TryGetValue(agentId, out var agent))
        {
            agent.LastHeartbeat = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var agentEntry = _connectedAgents.FirstOrDefault(a => a.Value.ConnectionId == Context.ConnectionId);
        if (agentEntry.Key is not null)
        {
            _connectedAgents.TryRemove(agentEntry.Key, out _);
            _logger.LogWarning("Agent disconnesso: {AgentId} (ConnectionId: {ConnectionId})",
                agentEntry.Key, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Informazioni su un agent on-prem connesso.</summary>
public class ConnectedAgent
{
    public string ConnectionId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset LastHeartbeat { get; set; }
    public bool CanReachAD { get; set; }
    public bool CanReachSccm { get; set; }

    public bool IsAlive => (DateTimeOffset.UtcNow - LastHeartbeat).TotalMinutes < 5;
}
