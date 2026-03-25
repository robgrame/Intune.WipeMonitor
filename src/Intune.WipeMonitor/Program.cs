using Azure.Identity;
using Intune.WipeMonitor.Components;
using Intune.WipeMonitor.Data;
using Intune.WipeMonitor.Hubs;
using Intune.WipeMonitor.Models;
using Intune.WipeMonitor.Services;
using Intune.WipeMonitor.Shared.Logging;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// Bootstrap logger(prima che DI sia disponibile)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog con CMTrace formatter per file + console + App Insights
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                formatter: new CMTraceFormatter("WipeMonitor.Web"),
                path: "logs/wipemonitor-web-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .WriteTo.ApplicationInsights(
                services.GetRequiredService<Microsoft.ApplicationInsights.TelemetryClient>(),
                new Serilog.Sinks.ApplicationInsights.TelemetryConverters.TraceTelemetryConverter());
    });

    // Azure App Configuration + Key Vault references
    var appConfigEndpoint = builder.Configuration["AppConfig:Endpoint"];
    if (!string.IsNullOrEmpty(appConfigEndpoint))
    {
        var credential = new DefaultAzureCredential();
        builder.Configuration.AddAzureAppConfiguration(options =>
        {
            options.Connect(new Uri(appConfigEndpoint), credential)
                .ConfigureKeyVault(kv => kv.SetCredential(credential))
                .ConfigureRefresh(refresh =>
                {
                    refresh.Register("WipeMonitor:Sentinel", refreshAll: true)
                           .SetRefreshInterval(TimeSpan.FromMinutes(5));
                });
        });
        builder.Services.AddAzureAppConfiguration();
    }

    // Application Insights
    builder.Services.AddApplicationInsightsTelemetry();

    // Configuration binding
    builder.Services.Configure<WipeMonitorSettings>(
        builder.Configuration.GetSection(WipeMonitorSettings.SectionName));
    builder.Services.Configure<GraphSettings>(
        builder.Configuration.GetSection(GraphSettings.SectionName));
    builder.Services.Configure<ActiveDirectorySettings>(
        builder.Configuration.GetSection(ActiveDirectorySettings.SectionName));
    builder.Services.Configure<SccmSettings>(
        builder.Configuration.GetSection(SccmSettings.SectionName));

    // Database (SQLite)
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=wipemonitor.db";
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(connectionString));

    // HTTP clients
    builder.Services.AddHttpClient<GraphWipeMonitorService>();

    // Services
    builder.Services.AddScoped<GraphWipeMonitorService>();
    builder.Services.AddScoped<CleanupOrchestrator>();
    builder.Services.AddSingleton<CleanupTelemetryService>();

    // SignalR (gateway verso agent on-prem)
    builder.Services.AddSignalR();

    // Background service (singleton)
    builder.Services.AddSingleton<WipeMonitorBackgroundService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WipeMonitorBackgroundService>());

    // Blazor
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var app = builder.Build();

    // Ensure DB is created
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    // Serilog request logging
    app.UseSerilogRequestLogging();

    // Azure App Configuration middleware (dynamic refresh)
    if (!string.IsNullOrEmpty(appConfigEndpoint))
    {
        app.UseAzureAppConfiguration();
    }

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();

    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    // SignalR Hub endpoint per l'agent on-prem
    app.MapHub<CleanupHub>("/hub/cleanup");

    Log.Information("Intune Wipe Monitor avviato");

    // Startup banner con versione e configurazione
    StartupBanner.PrintWebBanner(
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"),
        app.Configuration);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Applicazione terminata in modo anomalo");
}
finally
{
    Log.CloseAndFlush();
}
