using System.Security.Claims;
using System.Text.Encodings.Web;
using Intune.WipeMonitor.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Auth;

/// <summary>
/// Autenticazione API Key per l'agent on-prem che si connette al SignalR Hub.
/// L'agent invia la chiave come query string: /hub/cleanup?api_key=xxx
/// La chiave è configurata in WipeMonitor:AgentApiKey.
/// </summary>
public class AgentApiKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly WipeMonitorSettings _settings;

    public AgentApiKeyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<WipeMonitorSettings> settings)
        : base(options, logger, encoder)
    {
        _settings = settings.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Questo handler si attiva solo per il SignalR hub
        // Le pagine Blazor usano OpenID Connect (Entra ID)

        var apiKey = Context.Request.Query["api_key"].FirstOrDefault()
            ?? Context.Request.Headers["X-Api-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(apiKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (string.IsNullOrEmpty(_settings.AgentApiKey))
        {
            Logger.LogWarning("AgentApiKey non configurato — connessioni agent rifiutate");
            return Task.FromResult(AuthenticateResult.Fail("AgentApiKey not configured"));
        }

        if (!string.Equals(apiKey, _settings.AgentApiKey, StringComparison.Ordinal))
        {
            Logger.LogWarning("API key non valida ricevuta per SignalR hub");
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "WipeMonitor-Agent"),
            new Claim(ClaimTypes.Role, "Agent"),
            new Claim("AuthMethod", "ApiKey")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
