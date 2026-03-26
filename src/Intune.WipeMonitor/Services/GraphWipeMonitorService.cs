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
    private readonly TeamsNotificationService _teamsNotifier;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GraphWipeMonitorService> _logger;

    private const string BaseUrl = "https://graph.microsoft.com/beta/deviceManagement/remoteActionAudits";
    private const string Filter = "?$filter=action eq 'factoryReset'&$orderby=requestDateTime desc";

    public GraphWipeMonitorService(
        HttpClient httpClient,
        IOptions<GraphSettings> settings,
        CleanupTelemetryService telemetry,
        TeamsNotificationService teamsNotifier,
        IServiceScopeFactory scopeFactory,
        ILogger<GraphWipeMonitorService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _telemetry = telemetry;
        _teamsNotifier = teamsNotifier;
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
                {
                    _telemetry.TrackWipeDetected(record);
                    await _teamsNotifier.NotifyWipeCompletedAsync(record);
                }

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
                    await _teamsNotifier.NotifyWipeCompletedAsync(existing);

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

    /// <summary>
    /// Cerca e rimuove il device da Windows Autopilot tramite Graph API.
    /// GET  /beta/deviceManagement/windowsAutopilotDeviceIdentities?$filter=contains(displayName,'{name}')
    /// DELETE /beta/deviceManagement/windowsAutopilotDeviceIdentities/{id}
    /// </summary>
    public async Task<(bool Success, bool NotFound)> DeleteAutopilotDeviceAsync(
        string deviceName, string managedDeviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate managedDeviceId is a valid GUID to prevent OData injection
            if (!Guid.TryParse(managedDeviceId, out _))
            {
                _logger.LogWarning("managedDeviceId non è un GUID valido: {Id}", managedDeviceId);
                return (false, false);
            }

            var token = await GetAccessTokenAsync(cancellationToken);
            var headers = new AuthenticationHeaderValue("Bearer", token);

            // Cerca per managedDeviceId (più preciso) o per displayName come fallback
            var searchUrl = $"https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeviceIdentities?" +
                $"$filter=managedDeviceId eq '{managedDeviceId}'&$select=id,displayName,serialNumber,managedDeviceId";

            using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.Authorization = headers;
            using var searchResponse = await _httpClient.SendAsync(searchRequest, cancellationToken);
            searchResponse.EnsureSuccessStatusCode();

            var searchContent = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
            var searchResult = JsonSerializer.Deserialize<GraphApiResponse<JsonElement>>(searchContent);

            if (searchResult?.Value is null || searchResult.Value.Count == 0)
            {
                _logger.LogInformation("Device {DeviceName} non trovato in Autopilot", deviceName);
                return (true, true); // NotFound = successo
            }

            // Elimina ogni identità Autopilot trovata
            foreach (var device in searchResult.Value)
            {
                var autopilotId = device.GetProperty("id").GetString();
                var serial = device.TryGetProperty("serialNumber", out var sn) ? sn.GetString() : "N/A";

                var deleteUrl = $"https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeviceIdentities/{autopilotId}";
                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                deleteRequest.Headers.Authorization = headers;
                using var deleteResponse = await _httpClient.SendAsync(deleteRequest, cancellationToken);

                if (deleteResponse.IsSuccessStatusCode || deleteResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogInformation(
                        "Device {DeviceName} (SN: {Serial}) rimosso da Autopilot (ID: {AutopilotId})",
                        deviceName, serial, autopilotId);
                }
                else
                {
                    _logger.LogError(
                        "Errore rimozione {DeviceName} da Autopilot: {StatusCode}",
                        deviceName, deleteResponse.StatusCode);
                    return (false, false);
                }
            }

            return (true, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eccezione durante rimozione {DeviceName} da Autopilot", deviceName);
            return (false, false);
        }
    }
}
