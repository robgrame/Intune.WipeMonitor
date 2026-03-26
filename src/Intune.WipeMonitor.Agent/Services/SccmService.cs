using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Intune.WipeMonitor.Shared;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Agent.Services;

/// <summary>
/// Servizio per la rimozione di device da SCCM tramite AdminService REST API.
/// Eseguito sull'agent on-prem con accesso alla rete interna del Site Server.
/// Include validazione SID per evitare cancellazioni accidentali.
/// </summary>
public class SccmService
{
    private readonly HttpClient _httpClient;
    private readonly AgentSettings _settings;
    private readonly ILogger<SccmService> _logger;

    public SccmService(HttpClient httpClient, IOptions<AgentSettings> settings, ILogger<SccmService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Rimuove un device da SCCM. Se expectedSid è valorizzato, il SID del device in SCCM
    /// deve corrispondere prima di procedere alla cancellazione.
    /// </summary>
    public async Task<CleanupStepResult> RemoveDeviceAsync(string deviceName, string? expectedSid = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var deviceInfo = await FindDeviceInfoAsync(deviceName, cancellationToken);
            if (deviceInfo is null)
            {
                _logger.LogWarning("Device {DeviceName} non trovato in SCCM", deviceName);
                return CleanupStepResult.NotFound($"Device '{deviceName}' non trovato in SCCM");
            }

            // Validazione SID: confronta con il SID atteso (tipicamente proveniente da AD)
            if (!string.IsNullOrEmpty(expectedSid) && !string.IsNullOrEmpty(deviceInfo.Sid))
            {
                if (!string.Equals(expectedSid, deviceInfo.Sid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "SID mismatch per {DeviceName} in SCCM: atteso {ExpectedSid}, trovato {SccmSid} (ResourceId: {ResourceId})",
                        deviceName, expectedSid, deviceInfo.Sid, deviceInfo.ResourceId);
                    return CleanupStepResult.SidMismatch(
                        $"SID mismatch SCCM per '{deviceName}': atteso {expectedSid}, trovato {deviceInfo.Sid}",
                        deviceInfo.Sid);
                }

                _logger.LogInformation(
                    "SID validato per {DeviceName} in SCCM: {Sid} (ResourceId: {ResourceId})",
                    deviceName, deviceInfo.Sid, deviceInfo.ResourceId);
            }
            else if (!string.IsNullOrEmpty(expectedSid) && string.IsNullOrEmpty(deviceInfo.Sid))
            {
                _logger.LogWarning(
                    "SCCM non ha un SID per {DeviceName} (ResourceId: {ResourceId}) — procedo senza validazione SID",
                    deviceName, deviceInfo.ResourceId);
            }

            var deleteUrl = $"{_settings.Sccm.AdminServiceUrl}/wmi/SMS_R_System({deviceInfo.ResourceId})";
            using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
            ConfigureAuth(request);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Device {DeviceName} (ResourceId: {ResourceId}, SID: {SID}) rimosso da SCCM",
                    deviceName, deviceInfo.ResourceId, deviceInfo.Sid ?? "N/A");
                return CleanupStepResult.Success(deviceInfo.Sid);
            }

            var errorMsg = $"Errore SCCM: {response.StatusCode} - {await response.Content.ReadAsStringAsync(cancellationToken)}";
            _logger.LogError("Errore rimozione {DeviceName} da SCCM: {Error}", deviceName, errorMsg);
            return CleanupStepResult.Failed(errorMsg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eccezione durante rimozione {DeviceName} da SCCM", deviceName);
            return CleanupStepResult.Failed($"Eccezione: {ex.Message}");
        }
    }

    public bool CanConnect()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_settings.Sccm.AdminServiceUrl}/wmi/SMS_Site?$select=SiteCode&$top=1");
            ConfigureAuth(request);
            var response = _httpClient.Send(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SccmDeviceInfo?> FindDeviceInfoAsync(string deviceName, CancellationToken cancellationToken)
    {
        var escapedName = Uri.EscapeDataString(deviceName).Replace("'", "''");
        var url = $"{_settings.Sccm.AdminServiceUrl}/wmi/SMS_R_System?$filter=Name eq '{escapedName}'&$select=ResourceId,Name,SID";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureAuth(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);

        var values = doc.RootElement.GetProperty("value");
        if (values.GetArrayLength() == 0)
            return null;

        var device = values[0];
        var resourceId = device.GetProperty("ResourceId").GetInt32();
        string? sid = null;
        if (device.TryGetProperty("SID", out var sidProp) && sidProp.ValueKind == JsonValueKind.String)
            sid = sidProp.GetString();

        return new SccmDeviceInfo { ResourceId = resourceId, Sid = sid };
    }

    private void ConfigureAuth(HttpRequestMessage request)
    {
        // Le credenziali vengono gestite dal HttpClientHandler configurato nel DI.
        // Se serve un override per singola request, aggiungere qui.
    }
}

/// <summary>Informazioni di un device in SCCM.</summary>
internal class SccmDeviceInfo
{
    public int ResourceId { get; set; }
    public string? Sid { get; set; }
}
