using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Intune.WipeMonitor.Shared.Logging;

/// <summary>
/// Stampa un banner ASCII e i valori di configurazione all'avvio dell'applicazione.
/// I valori che contengono secret vengono mascherati con ****.
/// </summary>
public static class StartupBanner
{
    private static readonly string[] SecretKeys =
        ["secret", "password", "connectionstring", "key", "token"];

    /// <summary>
    /// Stampa il banner di avvio per il Web App.
    /// </summary>
    public static void PrintWebBanner(ILogger logger, IConfiguration configuration)
    {
        var version = GetAssemblyVersion("Intune.WipeMonitor");
        var sharedVersion = GetAssemblyVersion("Intune.WipeMonitor.Shared");

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║                                                              ║");
        sb.AppendLine("║   ██╗    ██╗██╗██████╗ ███████╗                              ║");
        sb.AppendLine("║   ██║    ██║██║██╔══██╗██╔════╝                              ║");
        sb.AppendLine("║   ██║ █╗ ██║██║██████╔╝█████╗                                ║");
        sb.AppendLine("║   ██║███╗██║██║██╔═══╝ ██╔══╝                                ║");
        sb.AppendLine("║   ╚███╔███╔╝██║██║     ███████╗                              ║");
        sb.AppendLine("║    ╚══╝╚══╝ ╚═╝╚═╝    ╚══════╝                              ║");
        sb.AppendLine("║                                                              ║");
        sb.AppendLine("║   Intune Wipe Monitor — Web Dashboard                        ║");
        sb.AppendLine("║                                                              ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"  Version:        {version}");
        sb.AppendLine($"  Shared:         {sharedVersion}");
        sb.AppendLine($"  Environment:    {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}");
        sb.AppendLine($"  Machine:        {Environment.MachineName}");
        sb.AppendLine($"  OS:             {Environment.OSVersion}");
        sb.AppendLine($"  Runtime:        {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"  Started:        {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("  ── Configuration ──────────────────────────────────────────");
        sb.AppendLine();

        AppendSection(sb, configuration, "WipeMonitor");
        AppendSection(sb, configuration, "Graph");
        AppendSection(sb, configuration, "ActiveDirectory");
        AppendSection(sb, configuration, "SCCM");
        AppendConfigValue(sb, configuration, "AppConfig:Endpoint");
        AppendConfigValue(sb, configuration, "ConnectionStrings:DefaultConnection");
        AppendConfigValue(sb, configuration, "ApplicationInsights:ConnectionString");

        sb.AppendLine();
        sb.AppendLine("  ─────────────────────────────────────────────────────────");

        logger.LogInformation("{Banner}", sb.ToString());
    }

    /// <summary>
    /// Stampa il banner di avvio per l'Agent on-prem.
    /// </summary>
    public static void PrintAgentBanner(ILogger logger, IConfiguration configuration)
    {
        var version = GetAssemblyVersion("Intune.WipeMonitor.Agent");
        var sharedVersion = GetAssemblyVersion("Intune.WipeMonitor.Shared");

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║                                                              ║");
        sb.AppendLine("║   ██╗    ██╗██╗██████╗ ███████╗                              ║");
        sb.AppendLine("║   ██║    ██║██║██╔══██╗██╔════╝                              ║");
        sb.AppendLine("║   ██║ █╗ ██║██║██████╔╝█████╗                                ║");
        sb.AppendLine("║   ██║███╗██║██║██╔═══╝ ██╔══╝                                ║");
        sb.AppendLine("║   ╚███╔███╔╝██║██║     ███████╗                              ║");
        sb.AppendLine("║    ╚══╝╚══╝ ╚═╝╚═╝    ╚══════╝                              ║");
        sb.AppendLine("║                                                              ║");
        sb.AppendLine("║   Intune Wipe Monitor — On-Prem Agent                        ║");
        sb.AppendLine("║                                                              ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"  Version:        {version}");
        sb.AppendLine($"  Shared:         {sharedVersion}");
        sb.AppendLine($"  Machine:        {Environment.MachineName}");
        sb.AppendLine($"  OS:             {Environment.OSVersion}");
        sb.AppendLine($"  Runtime:        {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"  User:           {Environment.UserDomainName}\\{Environment.UserName}");
        sb.AppendLine($"  Started:        {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("  ── Configuration ──────────────────────────────────────────");
        sb.AppendLine();

        AppendSection(sb, configuration, "Agent");
        AppendConfigValue(sb, configuration, "ApplicationInsights:ConnectionString");

        sb.AppendLine();
        sb.AppendLine("  ─────────────────────────────────────────────────────────");

        logger.LogInformation("{Banner}", sb.ToString());
    }

    private static void AppendSection(StringBuilder sb, IConfiguration configuration, string sectionName)
    {
        var section = configuration.GetSection(sectionName);
        if (!section.Exists()) return;

        sb.AppendLine($"  [{sectionName}]");
        foreach (var child in section.GetChildren())
        {
            AppendConfigEntry(sb, child, "    ");
        }
        sb.AppendLine();
    }

    private static void AppendConfigEntry(StringBuilder sb, IConfigurationSection section, string indent)
    {
        if (section.GetChildren().Any())
        {
            sb.AppendLine($"{indent}[{section.Key}]");
            foreach (var child in section.GetChildren())
            {
                AppendConfigEntry(sb, child, indent + "  ");
            }
        }
        else
        {
            var value = MaskIfSecret(section.Path, section.Value);
            sb.AppendLine($"{indent}{section.Key} = {value}");
        }
    }

    private static void AppendConfigValue(StringBuilder sb, IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (value is null) return;
        sb.AppendLine($"  {key} = {MaskIfSecret(key, value)}");
    }

    private static string MaskIfSecret(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";

        var keyLower = key.ToLowerInvariant();
        foreach (var secret in SecretKeys)
        {
            if (keyLower.Contains(secret))
                return "****";
        }

        return value;
    }

    private static string GetAssemblyVersion(string assemblyName)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName);

            if (asm is not null)
            {
                var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (!string.IsNullOrEmpty(infoVersion))
                    return infoVersion;

                return asm.GetName().Version?.ToString() ?? "unknown";
            }

            return "not loaded";
        }
        catch
        {
            return "unknown";
        }
    }
}
