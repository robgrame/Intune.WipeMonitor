using System.Text;
using System.Text.Json;
using Intune.WipeMonitor.Models;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Services;

/// <summary>
/// Invia notifiche a Microsoft Teams tramite Incoming Webhook.
/// Usa Adaptive Card per mostrare dettagli del device e bottone
/// per aprire la pagina di approvazione nella dashboard.
/// </summary>
public class TeamsNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly WipeMonitorSettings _settings;
    private readonly ILogger<TeamsNotificationService> _logger;

    public TeamsNotificationService(
        HttpClient httpClient,
        IOptions<WipeMonitorSettings> settings,
        ILogger<TeamsNotificationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Invia una notifica Teams quando un device è pronto per l'approvazione del cleanup.
    /// </summary>
    public async Task NotifyWipeCompletedAsync(DeviceCleanupRecord record)
    {
        if (string.IsNullOrEmpty(_settings.TeamsWebhookUrl))
        {
            _logger.LogDebug("Teams webhook non configurato, notifica saltata");
            return;
        }

        try
        {
            var card = BuildAdaptiveCard(record);
            var payload = JsonSerializer.Serialize(card);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_settings.TeamsWebhookUrl, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Notifica Teams inviata per device {DeviceName}", record.DeviceDisplayName);
            }
            else
            {
                _logger.LogWarning("Notifica Teams fallita per {DeviceName}: {StatusCode}",
                    record.DeviceDisplayName, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore invio notifica Teams per {DeviceName}", record.DeviceDisplayName);
        }
    }

    /// <summary>
    /// Invia una notifica Teams con il riepilogo del cleanup completato.
    /// </summary>
    public async Task NotifyCleanupCompletedAsync(DeviceCleanupRecord record, bool success)
    {
        if (string.IsNullOrEmpty(_settings.TeamsWebhookUrl)) return;

        try
        {
            var card = BuildCleanupResultCard(record, success);
            var payload = JsonSerializer.Serialize(card);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(_settings.TeamsWebhookUrl, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore invio notifica cleanup Teams per {DeviceName}", record.DeviceDisplayName);
        }
    }

    private object BuildAdaptiveCard(DeviceCleanupRecord record)
    {
        var dashboardUrl = _settings.DashboardUrl?.TrimEnd('/') ?? "https://intune-wipemonitor-app.azurewebsites.net";

        return new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    contentUrl = (string?)null,
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        msteams = new { width = "Full" },
                        body = new object[]
                        {
                            new
                            {
                                type = "ColumnSet",
                                columns = new object[]
                                {
                                    new
                                    {
                                        type = "Column",
                                        width = "auto",
                                        items = new object[]
                                        {
                                            new { type = "TextBlock", text = "🛡️", size = "Large" }
                                        }
                                    },
                                    new
                                    {
                                        type = "Column",
                                        width = "stretch",
                                        items = new object[]
                                        {
                                            new { type = "TextBlock", text = "Wipe Monitor — Approvazione Richiesta", weight = "Bolder", size = "Medium" },
                                            new { type = "TextBlock", text = "Un device con wipe completato richiede approvazione per il cleanup.", isSubtle = true, wrap = true }
                                        }
                                    }
                                }
                            },
                            new
                            {
                                type = "FactSet",
                                facts = new object[]
                                {
                                    new { title = "Device", value = record.DeviceDisplayName },
                                    new { title = "Owner", value = record.DeviceOwner },
                                    new { title = "Wipe richiesto da", value = record.InitiatedBy },
                                    new { title = "Data wipe", value = record.WipeRequestedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm") },
                                    new { title = "Managed Device ID", value = record.ManagedDeviceId }
                                }
                            }
                        },
                        actions = new object[]
                        {
                            new
                            {
                                type = "Action.OpenUrl",
                                title = "✅ Apri Approvazioni",
                                url = $"{dashboardUrl}/approvals",
                                style = "positive"
                            },
                            new
                            {
                                type = "Action.OpenUrl",
                                title = "📊 Dashboard",
                                url = dashboardUrl
                            }
                        }
                    }
                }
            }
        };
    }

    private object BuildCleanupResultCard(DeviceCleanupRecord record, bool success)
    {
        var emoji = success ? "✅" : "❌";
        var color = success ? "Good" : "Attention";
        var status = success ? "Cleanup completato con successo" : "Cleanup fallito — richiede attenzione";

        return new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    contentUrl = (string?)null,
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new { type = "TextBlock", text = $"{emoji} Wipe Monitor — {status}", weight = "Bolder", size = "Medium", color },
                            new
                            {
                                type = "FactSet",
                                facts = new object[]
                                {
                                    new { title = "Device", value = record.DeviceDisplayName },
                                    new { title = "Approvato da", value = record.ApprovedBy ?? "N/D" },
                                    new { title = "Completato", value = record.CleanupCompletedAt?.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss") ?? "N/D" }
                                }
                            }
                        }
                    }
                }
            }
        };
    }
}
