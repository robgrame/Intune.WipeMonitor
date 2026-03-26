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
                DeviceName = action.DeviceName,
                ManagedDeviceId = action.ManagedDeviceId,
                ActionState = action.ActionState,
                WipeRequestedAt = action.RequestDateTime,
                WipeCompletedAt = action.LastUpdatedDateTime,
                InitiatedBy = action.InitiatedByUserPrincipalName ?? "system"
            };

            // Cross-reference con Entra ID
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

    private async Task EnrichWithEntraDataAsync(WipeReportEntry entry, CancellationToken ct)
    {
        try
        {
            var filter = Uri.EscapeDataString($"displayName eq '{entry.DeviceName}'");
            var url = $"https://graph.microsoft.com/v1.0/devices?$filter={filter}" +
                      "&$select=id,displayName,operatingSystem,operatingSystemVersion,trustType,accountEnabled,approximateLastSignInDateTime";

            var response = await _http.GetFromJsonAsync<GraphPagedResponse<EntraDeviceDto>>(url, JsonOptions, ct);
            var device = response?.Value.FirstOrDefault();

            if (device is not null)
            {
                entry.EntraDeviceFound = true;
                entry.EntraDeviceId = device.Id;
                entry.EntraDisplayName = device.DisplayName;
                entry.OperatingSystem = device.OperatingSystem;
                entry.OperatingSystemVersion = device.OperatingSystemVersion;
                entry.TrustType = device.TrustType;
                entry.EntraAccountEnabled = device.AccountEnabled;
                entry.EntraLastSignIn = device.ApproximateLastSignInDateTime;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore query Entra per device {Device}", entry.DeviceName);
        }
    }

    private async Task SetAuthHeaderAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), ct);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
}
