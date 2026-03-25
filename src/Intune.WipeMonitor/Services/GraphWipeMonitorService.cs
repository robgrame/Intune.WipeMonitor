using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Identity;
using Intune.WipeMonitor.Data;
using Intune.WipeMonitor.Models;
using Intune.WipeMonitor.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Services;

/// <summary>
/// Servizio per il polling delle azioni di wipe da Microsoft Graph API.
/// Endpoint: GET /beta/deviceManagement/remoteActionAudits?$filter=action eq 'factoryReset'
/// </summary>
public class GraphWipeMonitorService
{
    private readonly HttpClient _httpClient;
    private readonly GraphSettings _settings;
    private readonly CleanupTelemetryService _telemetry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GraphWipeMonitorService> _logger;

    private const string BaseUrl = "https://graph.microsoft.com/beta/deviceManagement/remoteActionAudits";
    private const string Filter = "?$filter=action eq 'factoryReset'&$orderby=requestDateTime desc";

    public GraphWipeMonitorService(
        HttpClient httpClient,
        IOptions<GraphSettings> settings,
        CleanupTelemetryService telemetry,
        IServiceScopeFactory scopeFactory,
        ILogger<GraphWipeMonitorService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _telemetry = telemetry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Recupera le azioni di wipe da Graph API e aggiorna il database.
    /// </summary>
    public async Task PollWipeActionsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Inizio polling azioni di wipe da Graph API...");

        try
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var wipeActions = await FetchAllWipeActionsAsync(token, cancellationToken);

            _logger.LogInformation("Recuperate {Count} azioni di wipe da Graph API", wipeActions.Count);

            await SyncWithDatabaseAsync(wipeActions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore durante il polling delle azioni di wipe");
            throw;
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var credential = new ClientSecretCredential(
            _settings.TenantId,
            _settings.ClientId,
            _settings.ClientSecret);

        var tokenRequest = new Azure.Core.TokenRequestContext([_settings.Scope]);
        var token = await credential.GetTokenAsync(tokenRequest, cancellationToken);
        return token.Token;
    }

    private async Task<List<WipeAction>> FetchAllWipeActionsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var allActions = new List<WipeAction>();
        var url = $"{BaseUrl}{Filter}";

        while (!string.IsNullOrEmpty(url))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<GraphApiResponse<WipeAction>>(content);

            if (result?.Value is not null)
            {
                allActions.AddRange(result.Value);
            }

            // Gestione paginazione
            url = result?.ODataNextLink;
        }

        return allActions;
    }

    /// <summary>
    /// Sincronizza le azioni di wipe con il database locale.
    /// Crea nuovi record o aggiorna quelli esistenti.
    /// </summary>
    private async Task SyncWithDatabaseAsync(List<WipeAction> wipeActions, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int newDevices = 0, statusChanged = 0;

        foreach (var action in wipeActions)
        {
            var existing = await db.DeviceCleanupRecords
                .FirstOrDefaultAsync(r => r.WipeActionId == action.Id, cancellationToken);

            if (existing is null)
            {
                // Nuovo record
                var record = new DeviceCleanupRecord
                {
                    WipeActionId = action.Id,
                    DeviceDisplayName = action.DeviceDisplayName,
                    ManagedDeviceId = action.ManagedDeviceId,
                    InitiatedBy = action.InitiatedByUserPrincipalName,
                    DeviceOwner = action.DeviceOwnerUserPrincipalName,
                    WipeActionState = action.ActionState,
                    WipeRequestedAt = action.RequestDateTime,
                    WipeCompletedAt = action.IsCompleted ? action.RequestDateTime : null,
                    Status = action.IsCompleted ? CleanupStatus.PendingApproval : CleanupStatus.WipePending,
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastUpdatedAt = DateTimeOffset.UtcNow
                };
                db.DeviceCleanupRecords.Add(record);
                newDevices++;

                if (action.IsCompleted)
                    _telemetry.TrackWipeDetected(record);

                _logger.LogInformation(
                    "Nuovo device rilevato: {DeviceName} (Wipe: {State})",
                    action.DeviceDisplayName, action.ActionState);
            }
            else if (existing.WipeActionState != action.ActionState)
            {
                // Aggiornamento stato wipe
                existing.WipeActionState = action.ActionState;
                existing.LastUpdatedAt = DateTimeOffset.UtcNow;
                statusChanged++;

                if (action.IsCompleted && existing.Status == CleanupStatus.WipePending)
                {
                    existing.Status = CleanupStatus.PendingApproval;
                    existing.WipeCompletedAt = DateTimeOffset.UtcNow;

                    _telemetry.TrackWipeDetected(existing);

                    _logger.LogInformation(
                        "Wipe completato per {DeviceName} - pronto per approvazione cleanup",
                        action.DeviceDisplayName);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        _telemetry.TrackWipePollCompleted(wipeActions.Count, newDevices, statusChanged);
    }

    /// <summary>
    /// Elimina il device da Intune tramite Graph API.
    /// DELETE /beta/deviceManagement/managedDevices/{managedDeviceId}
    /// </summary>
    public async Task<bool> DeleteIntuneDeviceAsync(string managedDeviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var url = $"https://graph.microsoft.com/beta/deviceManagement/managedDevices/{managedDeviceId}";

            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Device {DeviceId} rimosso da Intune", managedDeviceId);
                return true;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Device {DeviceId} non trovato in Intune (già rimosso?)", managedDeviceId);
                return true; // Consideriamo OK se già rimosso
            }

            _logger.LogError("Errore rimozione device {DeviceId} da Intune: {StatusCode}",
                managedDeviceId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eccezione durante rimozione device {DeviceId} da Intune", managedDeviceId);
            return false;
        }
    }
}
