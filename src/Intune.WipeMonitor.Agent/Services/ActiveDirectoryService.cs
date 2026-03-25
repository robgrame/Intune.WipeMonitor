using System.DirectoryServices.Protocols;
using System.Net;
using Intune.WipeMonitor.Shared;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Agent.Services;

/// <summary>
/// Servizio per la rimozione di oggetti computer da Active Directory via LDAP.
/// Eseguito sull'agent on-prem con accesso diretto al domain controller.
/// </summary>
public class ActiveDirectoryService
{
    private readonly AgentSettings _settings;
    private readonly ILogger<ActiveDirectoryService> _logger;

    public ActiveDirectoryService(IOptions<AgentSettings> settings, ILogger<ActiveDirectoryService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<CleanupStepResult> RemoveComputerAsync(string computerName)
    {
        return await Task.Run(() => RemoveComputer(computerName));
    }

    private CleanupStepResult RemoveComputer(string computerName)
    {
        try
        {
            using var connection = CreateConnection();

            var dn = FindComputerDn(connection, computerName);
            if (dn is null)
            {
                _logger.LogWarning("Computer {ComputerName} non trovato in AD", computerName);
                return CleanupStepResult.NotFound($"Computer '{computerName}' non trovato in AD");
            }

            var deleteRequest = new DeleteRequest(dn);
            deleteRequest.Controls.Add(new TreeDeleteControl());
            var response = (DeleteResponse)connection.SendRequest(deleteRequest);

            if (response.ResultCode == ResultCode.Success)
            {
                _logger.LogInformation("Computer {ComputerName} rimosso da AD (DN: {DN})", computerName, dn);
                return CleanupStepResult.Success();
            }

            var errorMsg = $"Errore AD: {response.ResultCode} - {response.ErrorMessage}";
            _logger.LogError("Errore rimozione {ComputerName} da AD: {Error}", computerName, errorMsg);
            return CleanupStepResult.Failed(errorMsg);
        }
        catch (LdapException ex)
        {
            _logger.LogError(ex, "Eccezione LDAP durante rimozione {ComputerName}", computerName);
            return CleanupStepResult.Failed($"Eccezione LDAP: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eccezione durante rimozione {ComputerName} da AD", computerName);
            return CleanupStepResult.Failed($"Eccezione: {ex.Message}");
        }
    }

    public bool CanConnect()
    {
        try
        {
            using var connection = CreateConnection();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private LdapConnection CreateConnection()
    {
        var identifier = new LdapDirectoryIdentifier(
            _settings.ActiveDirectory.Server, _settings.ActiveDirectory.Port);

        LdapConnection connection;

        if (!string.IsNullOrEmpty(_settings.ActiveDirectory.Username))
        {
            var credential = new NetworkCredential(
                _settings.ActiveDirectory.Username, _settings.ActiveDirectory.Password);
            connection = new LdapConnection(identifier, credential);
        }
        else
        {
            connection = new LdapConnection(identifier);
        }

        connection.SessionOptions.ProtocolVersion = 3;
        if (_settings.ActiveDirectory.UseSsl)
            connection.SessionOptions.SecureSocketLayer = true;

        connection.Bind();
        return connection;
    }

    private string? FindComputerDn(LdapConnection connection, string computerName)
    {
        var searchRequest = new SearchRequest(
            _settings.ActiveDirectory.SearchBase,
            $"(&(objectClass=computer)(cn={EscapeLdapFilter(computerName)}))",
            SearchScope.Subtree,
            "distinguishedName");
        searchRequest.SizeLimit = 1;

        var response = (SearchResponse)connection.SendRequest(searchRequest);
        return response.Entries.Count > 0 ? response.Entries[0].DistinguishedName : null;
    }

    private static string EscapeLdapFilter(string input) => input
        .Replace("\\", "\\5c").Replace("*", "\\2a")
        .Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
}
