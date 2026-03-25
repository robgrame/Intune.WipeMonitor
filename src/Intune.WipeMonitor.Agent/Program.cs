using Intune.WipeMonitor.Agent;
using Intune.WipeMonitor.Agent.Services;
using Intune.WipeMonitor.Shared.Logging;
using Serilog;
using Serilog.Events;

// Bootstrap logger
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Serilog con CMTrace formatter per file + console + App Insights
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
                path: "logs/wipemonitor-agent-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .WriteTo.ApplicationInsights(
                services.GetRequiredService<Microsoft.ApplicationInsights.TelemetryClient>(),
                new Serilog.Sinks.ApplicationInsights.TelemetryConverters.TraceTelemetryConverter());
    });

    // Configurazione per eseguire come Windows Service
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Intune.WipeMonitor.Agent";
    });

    // Application Insights (dalla macchina on-prem)
    builder.Services.AddApplicationInsightsTelemetryWorkerService();

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
            var handler = new HttpClientHandler { UseDefaultCredentials = true };
            if (settings?.Sccm.IgnoreSslErrors == true)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            return handler;
        });
    builder.Services.AddSingleton<SccmService>();

    // Worker
    builder.Services.AddHostedService<CleanupAgentWorker>();

    Log.Information("Intune Wipe Monitor Agent avviato su {Machine}", Environment.MachineName);

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
