using System.Text;
using System.Text.Json;
using Intune.WipeMonitor.Report.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Report.Services;

/// <summary>
/// Invia una Adaptive Card di riepilogo su Teams tramite Power Automate webhook,
/// con link al report su SharePoint.
/// </summary>
public class TeamsReportNotifier
{
    private readonly HttpClient _http;
    private readonly ReportSettings _settings;
    private readonly ILogger<TeamsReportNotifier> _logger;

    public TeamsReportNotifier(
        HttpClient http,
        IOptions<ReportSettings> settings,
        ILogger<TeamsReportNotifier> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(
        List<WipeReportEntry> entries,
        string reportUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.TeamsWebhookUrl))
        {
            _logger.LogWarning("TeamsWebhookUrl non configurato, notifica saltata");
            return;
        }

        var totalWipes = entries.Count;
        var completed = entries.Count(e => e.ActionState == "done");
        var entraPresent = entries.Count(e => e.EntraDeviceFound);
        var entraPending = entries.Count(e => e.IsEntraPending);

        var card = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = "📊 Wipe Monitor — Report Settimanale",
                                weight = "Bolder",
                                size = "Large",
                                style = "heading"
                            },
                            new
                            {
                                type = "TextBlock",
                                text = $"Report generato il {DateTime.Now:dd/MM/yyyy HH:mm} — ultimi {_settings.ReportDays} giorni",
                                isSubtle = true,
                                spacing = "None"
                            },
                            new
                            {
                                type = "FactSet",
                                facts = new object[]
                                {
                                    new { title = "Totale wipe actions", value = totalWipes.ToString() },
                                    new { title = "Wipe completati", value = completed.ToString() },
                                    new { title = "Device in Entra ID", value = entraPresent.ToString() },
                                    new { title = "⚠ Pending Entra cleanup", value = entraPending.ToString() }
                                }
                            },
                            new
                            {
                                type = "TextBlock",
                                text = entraPending > 0
                                    ? $"**{entraPending} device** con wipe completato hanno ancora un oggetto Entra ID attivo."
                                    : "✅ Tutti i device wipati sono stati rimossi da Entra ID.",
                                wrap = true,
                                color = entraPending > 0 ? "Attention" : "Good"
                            }
                        },
                        actions = new object[]
                        {
                            new
                            {
                                type = "Action.OpenUrl",
                                title = "📂 Apri Report su SharePoint",
                                url = reportUrl
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(card);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(_settings.TeamsWebhookUrl, content, ct);
        if (response.IsSuccessStatusCode)
            _logger.LogInformation("Adaptive Card inviata su Teams");
        else
            _logger.LogWarning("Errore invio Teams card: {Status}", response.StatusCode);
    }
}
