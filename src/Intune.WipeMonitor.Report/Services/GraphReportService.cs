using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Intune.WipeMonitor.Report.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Report.Services;

/// <summary>
/// Recupera le azioni di wipe da Graph API (remoteActionAudits)
/// e verifica la presenza dei device in Entra ID.
/// </summary>
public class GraphReportService
{
    private readonly HttpClient _http;
    private readonly GraphSettings _graphSettings;
    private readonly ReportSettings _reportSettings;
    private readonly ILogger<GraphReportService> _logger;
    private readonly ClientSecretCredential _credential;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GraphReportService(
        HttpClient http,
        IOptions<GraphSettings> graphSettings,
        IOptions<ReportSettings> reportSettings,
        ILogger<GraphReportService> logger)
    {
        _http = http;
        _graphSettings = graphSettings.Value;
        _reportSettings = reportSettings.Value;
        _logger = logger;
        _credential = new ClientSecretCredential(
            _graphSettings.TenantId, _graphSettings.ClientId, _graphSettings.ClientSecret);
    }

    /// <summary>Genera il report completo: wipe actions + cross-reference Entra.</summary>
    public async Task<List<WipeReportEntry>> BuildReportDataAsync(CancellationToken ct = default)
    {
        await SetAuthHeaderAsync(ct);

        var wipeActions = await FetchWipeActionsAsync(ct);
        _logger.LogInformation("Recuperate {Count} azioni di wipe degli ultimi {Days} giorni",
            wipeActions.Count, _reportSettings.ReportDays);

        var entries = new List<WipeReportEntry>();
        foreach (var action in wipeActions)
        {
            var entry = new WipeReportEntry
            {
                DeviceName = action.DeviceDisplayName,
                ManagedDeviceId = action.ManagedDeviceId,
                ActionState = action.ActionState,
                WipeRequestedAt = action.RequestDateTime,
                WipeCompletedAt = action.LastUpdatedDateTime,
                InitiatedBy = action.InitiatedByUserPrincipalName ?? "system"
            };

            // Arricchisci con dati Intune (azureADDeviceId, user, OS)
            await EnrichFromManagedDeviceAsync(entry, ct);

            // Cross-reference con Entra ID (displayName + validazione deviceId)
            await EnrichWithEntraDataAsync(entry, ct);

            entries.Add(entry);
        }

        _logger.LogInformation("Report: {Total} device, {Pending} con Entra ancora presente",
            entries.Count, entries.Count(e => e.IsEntraPending));

        return entries;
    }

    private async Task<List<WipeActionDto>> FetchWipeActionsAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-_reportSettings.ReportDays);
        var filter = $"action eq 'factoryReset' and requestDateTime ge {since:yyyy-MM-ddTHH:mm:ssZ}";
        var url = $"https://graph.microsoft.com/beta/deviceManagement/remoteActionAudits" +
                  $"?$filter={Uri.EscapeDataString(filter)}&$orderby=requestDateTime desc";

        var allActions = new List<WipeActionDto>();

        while (!string.IsNullOrEmpty(url))
        {
            var response = await _http.GetFromJsonAsync<GraphPagedResponse<WipeActionDto>>(url, JsonOptions, ct);
            if (response is null) break;

            allActions.AddRange(response.Value);
            url = response.ODataNextLink;
        }

        return allActions;
    }

    /// <summary>
    /// Arricchisce con dati dal managed device Intune (azureADDeviceId, user, OS).
    /// Se il device è stato cancellato da Intune, i dati non saranno disponibili.
    /// </summary>
    private async Task EnrichFromManagedDeviceAsync(WipeReportEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.ManagedDeviceId)) return;

        try
        {
            var url = $"https://graph.microsoft.com/beta/deviceManagement/managedDevices/{entry.ManagedDeviceId}" +
                      "?$select=id,deviceName,azureADDeviceId,serialNumber,operatingSystem,osVersion,userPrincipalName";

            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return; // device già cancellato da Intune

            var md = await response.Content.ReadFromJsonAsync<ManagedDeviceDto>(JsonOptions, ct);
            if (md is null) return;

            entry.IntuneDeviceFound = true;
            entry.AzureADDeviceId = md.AzureADDeviceId;
            entry.IntuneUser = md.UserPrincipalName;
            entry.IntuneSerialNumber = md.SerialNumber;

            // Usa i dati Intune come fallback se mancano
            if (string.IsNullOrEmpty(entry.OperatingSystem))
                entry.OperatingSystem = md.OperatingSystem;
            if (string.IsNullOrEmpty(entry.OperatingSystemVersion))
                entry.OperatingSystemVersion = md.OsVersion;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Managed device non raggiungibile: {Id}", entry.ManagedDeviceId);
        }
    }

    /// <summary>
    /// Cerca il device in Entra ID per displayName. Se abbiamo l'azureADDeviceId
    /// da Intune, valida che il match sia lo stesso device.
    /// </summary>
    private async Task EnrichWithEntraDataAsync(WipeReportEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.DeviceName))
        {
            _logger.LogDebug("Device name vuoto, skip Entra lookup");
            return;
        }

        try
        {
            var escapedName = entry.DeviceName.Replace("'", "''");
            var url = $"https://graph.microsoft.com/v1.0/devices?$filter={Uri.EscapeDataString($"displayName eq '{escapedName}'")}" +
                      $"&$select={Uri.EscapeDataString("id,displayName,deviceId,operatingSystem,operatingSystemVersion,trustType,accountEnabled,approximateLastSignInDateTime")}" +
                      "&$count=true";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("ConsistencyLevel", "eventual");
            var httpResponse = await _http.SendAsync(request, ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Entra query fallita per {Device}: HTTP {Status} — {Body}",
                    entry.DeviceName, (int)httpResponse.StatusCode, errorBody);
                return;
            }

            var response = await httpResponse.Content.ReadFromJsonAsync<GraphPagedResponse<EntraDeviceDto>>(JsonOptions, ct);
            var devices = response?.Value ?? new();

            // Se abbiamo l'azureADDeviceId da Intune, cerca il match esatto
            EntraDeviceDto? device = null;
            if (!string.IsNullOrEmpty(entry.AzureADDeviceId) && entry.AzureADDeviceId != "00000000-0000-0000-0000-000000000000")
            {
                device = devices.FirstOrDefault(d => string.Equals(d.DeviceId, entry.AzureADDeviceId, StringComparison.OrdinalIgnoreCase));
                if (device is null && devices.Count > 0)
                {
                    _logger.LogWarning("Entra device {Device}: trovato per nome ma deviceId non corrisponde (atteso: {Expected})",
                        entry.DeviceName, entry.AzureADDeviceId);
                }
            }

            // Fallback: usa il primo risultato per nome
            device ??= devices.FirstOrDefault();

            if (device is not null)
            {
                entry.EntraDeviceFound = true;
                entry.EntraDeviceId = device.Id;
                entry.EntraDisplayName = device.DisplayName;
                entry.OperatingSystem ??= device.OperatingSystem;
                entry.OperatingSystemVersion ??= device.OperatingSystemVersion;
                entry.TrustType = device.TrustType;
                entry.EntraAccountEnabled = device.AccountEnabled;
                entry.EntraLastSignIn = device.ApproximateLastSignInDateTime;
                _logger.LogInformation("Entra device trovato: {Device} (ID: {Id}, deviceId: {DeviceId})",
                    entry.DeviceName, device.Id, device.DeviceId);
            }
            else
            {
                _logger.LogInformation("Entra device non trovato: {Device}", entry.DeviceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Eccezione query Entra per device {Device}", entry.DeviceName);
        }
    }

    private async Task SetAuthHeaderAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), ct);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
}
