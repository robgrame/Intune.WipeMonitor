using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Identity;
using Intune.WipeMonitor.Report.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Report.Services;

/// <summary>
/// Carica il report Excel su SharePoint Document Library via Graph API.
/// </summary>
public class SharePointUploadService
{
    private readonly HttpClient _http;
    private readonly GraphSettings _graphSettings;
    private readonly ReportSettings _reportSettings;
    private readonly ILogger<SharePointUploadService> _logger;
    private readonly ClientSecretCredential _credential;

    public SharePointUploadService(
        HttpClient http,
        IOptions<GraphSettings> graphSettings,
        IOptions<ReportSettings> reportSettings,
        ILogger<SharePointUploadService> logger)
    {
        _http = http;
        _graphSettings = graphSettings.Value;
        _reportSettings = reportSettings.Value;
        _logger = logger;
        _credential = new ClientSecretCredential(
            _graphSettings.TenantId, _graphSettings.ClientId, _graphSettings.ClientSecret);
    }

    /// <summary>
    /// Carica il file su SharePoint e restituisce il webUrl del file.
    /// </summary>
    public async Task<string> UploadReportAsync(byte[] fileContent, string fileName, CancellationToken ct = default)
    {
        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), ct);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var folderPath = _reportSettings.SharePointFolderPath.TrimStart('/');
        var url = $"https://graph.microsoft.com/v1.0/sites/{_reportSettings.SharePointSiteId}" +
                  $"/drives/{_reportSettings.SharePointDriveId}" +
                  $"/root:/{folderPath}/{fileName}:/content";

        _logger.LogInformation("Upload SharePoint: PUT {Url} ({Size} KB)", url, fileContent.Length / 1024);

        using var content = new ByteArrayContent(fileContent);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        var response = await _http.PutAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("SharePoint upload fallito: HTTP {Status} — {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        // Estrai il webUrl dalla risposta
        var json = await response.Content.ReadFromJsonAsync<SharePointFileResponse>(cancellationToken: ct);
        var webUrl = json?.WebUrl ?? "";

        _logger.LogInformation("Report caricato su SharePoint: {Url}", webUrl);
        return webUrl;
    }

    private class SharePointFileResponse
    {
        public string WebUrl { get; set; } = string.Empty;
    }
}
