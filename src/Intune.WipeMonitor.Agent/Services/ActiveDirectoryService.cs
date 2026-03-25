using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Principal;
using Intune.WipeMonitor.Shared;
using Microsoft.Extensions.Options;

namespace Intune.WipeMonitor.Agent.Services;

/// <summary>
/// Servizio per la rimozione di oggetti computer da Active Directory via LDAP.
/// Eseguito sull'agent on-prem con accesso diretto al domain controller.
/// Include validazione SID per evitare cancellazioni accidentali.
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

    /// <summary>
    /// Recupera DN e SID di un computer object in AD.
    /// </summary>
    public async Task<AdComputerInfo?> GetComputerInfoAsync(string computerName)
    {
        return await Task.Run(() => GetComputerInfo(computerName));
    }

    /// <summary>
    /// Rimuove un computer object da AD dopo validazione SID.
    /// Se expectedSid è valorizzato, il SID del computer deve corrispondere.
    /// </summary>
    public async Task<CleanupStepResult> RemoveComputerAsync(string computerName, string? expectedSid = null)
    {
        return await Task.Run(() => RemoveComputer(computerName, expectedSid));
    }

    private CleanupStepResult RemoveComputer(string computerName, string? expectedSid)
    {
        try
        {
            using var connection = CreateConnection();

            var computerInfo = FindComputerInfo(connection, computerName);
            if (computerInfo is null)
            {
                _logger.LogWarning("Computer {ComputerName} non trovato in AD", computerName);
                return CleanupStepResult.NotFound($"Computer '{computerName}' non trovato in AD");
            }

            // Validazione SID se richiesta
            if (!string.IsNullOrEmpty(expectedSid) && computerInfo.Sid != expectedSid)
            {
                _logger.LogError(
                    "SID mismatch per {ComputerName}: atteso {ExpectedSid}, trovato {ActualSid}",
                    computerName, expectedSid, computerInfo.Sid);
                return CleanupStepResult.SidMismatch(
                    $"SID mismatch per '{computerName}': atteso {expectedSid}, trovato {computerInfo.Sid}",
                    computerInfo.Sid);
            }

            var deleteRequest = new DeleteRequest(computerInfo.DistinguishedName);
            deleteRequest.Controls.Add(new TreeDeleteControl());
            var response = (DeleteResponse)connection.SendRequest(deleteRequest);

            if (response.ResultCode == ResultCode.Success)
            {
                _logger.LogInformation(
                    "Computer {ComputerName} rimosso da AD (DN: {DN}, SID: {SID})",
                    computerName, computerInfo.DistinguishedName, computerInfo.Sid);
                return CleanupStepResult.Success(computerInfo.Sid);
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

    private AdComputerInfo? GetComputerInfo(string computerName)
    {
        try
        {
            using var connection = CreateConnection();
            return FindComputerInfo(connection, computerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore recupero info computer {ComputerName} da AD", computerName);
            return null;
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
            // Supporta formato DOMAIN\user e user@domain
            var username = _settings.ActiveDirectory.Username;
            string? domain = null;
            if (username.Contains('\\'))
            {
                var parts = username.Split('\\', 2);
                domain = parts[0];
                username = parts[1];
            }
            var credential = new NetworkCredential(username, _settings.ActiveDirectory.Password, domain ?? "");
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

    private AdComputerInfo? FindComputerInfo(LdapConnection connection, string computerName)
    {
        var searchRequest = new SearchRequest(
            _settings.ActiveDirectory.SearchBase,
            $"(&(objectClass=computer)(cn={EscapeLdapFilter(computerName)}))",
            SearchScope.Subtree,
            "distinguishedName", "objectSid");
        searchRequest.SizeLimit = 1;

        var response = (SearchResponse)connection.SendRequest(searchRequest);
        if (response.Entries.Count == 0)
            return null;

        var entry = response.Entries[0];
        var dn = entry.DistinguishedName;

        string? sid = null;
        if (entry.Attributes.Contains("objectSid") && entry.Attributes["objectSid"].Count > 0)
        {
            var sidBytes = (byte[])entry.Attributes["objectSid"][0];
#pragma warning disable CA1416 // Agent runs only on Windows
            sid = new SecurityIdentifier(sidBytes, 0).Value;
#pragma warning restore CA1416
        }

        return new AdComputerInfo { DistinguishedName = dn, Sid = sid };
    }

    private static string EscapeLdapFilter(string input) => input
        .Replace("\\", "\\5c").Replace("*", "\\2a")
        .Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
}

/// <summary>Informazioni di un computer object in AD.</summary>
public class AdComputerInfo
{
    public string DistinguishedName { get; set; } = string.Empty;
    public string? Sid { get; set; }
}
