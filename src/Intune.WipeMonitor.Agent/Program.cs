using Intune.WipeMonitor.Agent;
using Intune.WipeMonitor.Agent.Services;
using Intune.WipeMonitor.Shared.Logging;
using Serilog;
using Serilog.Events;

// Imposta la working directory alla cartella dell'eseguibile (necessario per Windows Service)
var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
Directory.SetCurrentDirectory(exeDir);
var logPath = Path.Combine(exeDir, "logs", "wipemonitor-agent-.log");

// Bootstrap logger — scrive sia su console che su file in formato CMTrace
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        formatter: new CMTraceFormatter("WipeMonitor.Agent"),
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        flushToDiskInterval: TimeSpan.FromSeconds(1))
    .CreateBootstrapLogger();

try
{
    Log.Information("Intune Wipe Monitor Agent avvio su {Machine}...", Environment.MachineName);

    var builder = Host.CreateApplicationBuilder(args);

    // Configurazione per eseguire come Windows Service
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Intune.WipeMonitor.Agent";
    });

    // Application Insights
    builder.Services.AddApplicationInsightsTelemetryWorkerService();

    // Serilog con CMTrace formatter per file + console (telemetria App Insights gestita dal Worker)
    builder.Services.AddSerilog((services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(builder.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                formatter: new CMTraceFormatter("WipeMonitor.Agent"),
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30);
    });

    // Settings
    builder.Services.Configure<AgentSettings>(
        builder.Configuration.GetSection(AgentSettings.SectionName));

    // Services
    builder.Services.AddSingleton<ActiveDirectoryService>();
    builder.Services.AddHttpClient<SccmService>()
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var settings = builder.Configuration
                .GetSection(AgentSettings.SectionName).Get<AgentSettings>();
            var handler = new HttpClientHandler();
            // Usa credenziali di dominio esplicite se configurate, altrimenti default
            if (!string.IsNullOrEmpty(settings?.Sccm.Username))
            {
                handler.Credentials = new System.Net.NetworkCredential(
                    settings.Sccm.Username, settings.Sccm.Password);
            }
            else
            {
                handler.UseDefaultCredentials = true;
            }
            if (settings?.Sccm.IgnoreSslErrors == true)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            return handler;
        });

    // Worker
    builder.Services.AddHostedService<CleanupAgentWorker>();

    var host = builder.Build();

    // Startup banner con versione e configurazione
    StartupBanner.PrintAgentBanner(
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"),
        builder.Configuration);

    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminato in modo anomalo");
}
finally
{
    Log.CloseAndFlush();
}
