using System.Security.Claims;
using System.Text.Encodings.Web;
using Intune.WipeMonitor.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Auth;

/// <summary>
/// Autenticazione API Key per l'agent on-prem che si connette al SignalR Hub.
/// Accetta la chiave tramite header X-Api-Key, query string api_key/access_token,
/// oppure Authorization: Bearer (usato da SignalR AccessTokenProvider).
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

        // Controlla tutte le fonti possibili di API key:
        // 1. Header X-Api-Key (invio diretto dall'agent)
        // 2. Query string access_token (SignalR AccessTokenProvider per WebSocket)
        // 3. Query string api_key (compatibilità)
        // 4. Authorization: Bearer (SignalR AccessTokenProvider per HTTP)
        var apiKey = Context.Request.Headers["X-Api-Key"].FirstOrDefault()
            ?? Context.Request.Query["access_token"].FirstOrDefault()
            ?? Context.Request.Query["api_key"].FirstOrDefault()
            ?? ExtractBearerToken();

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

    private string? ExtractBearerToken()
    {
        var authHeader = Context.Request.Headers["Authorization"].FirstOrDefault();
        if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..];
        return null;
    }
}
