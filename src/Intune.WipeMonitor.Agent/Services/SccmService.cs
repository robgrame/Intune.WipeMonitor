using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Intune.WipeMonitor.Shared;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Agent.Services;

/// <summary>
/// Servizio per la rimozione di device da SCCM tramite AdminService REST API.
/// Eseguito sull'agent on-prem con accesso alla rete interna del Site Server.
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

    public async Task<CleanupStepResult> RemoveDeviceAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        try
        {
            var resourceId = await FindDeviceResourceIdAsync(deviceName, cancellationToken);
            if (resourceId is null)
            {
                _logger.LogWarning("Device {DeviceName} non trovato in SCCM", deviceName);
                return CleanupStepResult.NotFound($"Device '{deviceName}' non trovato in SCCM");
            }

            var deleteUrl = $"{_settings.Sccm.AdminServiceUrl}/wmi/SMS_R_System({resourceId})";
            using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
            ConfigureAuth(request);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Device {DeviceName} (ResourceId: {ResourceId}) rimosso da SCCM",
                    deviceName, resourceId);
                return CleanupStepResult.Success();
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

    private async Task<int?> FindDeviceResourceIdAsync(string deviceName, CancellationToken cancellationToken)
    {
        var url = $"{_settings.Sccm.AdminServiceUrl}/wmi/SMS_R_System?$filter=Name eq '{deviceName}'&$select=ResourceId,Name";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureAuth(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);

        var values = doc.RootElement.GetProperty("value");
        return values.GetArrayLength() > 0 ? values[0].GetProperty("ResourceId").GetInt32() : null;
    }

    private void ConfigureAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_settings.Sccm.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_settings.Sccm.Username}:{_settings.Sccm.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }
}
