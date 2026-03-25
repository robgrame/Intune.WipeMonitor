using Azure.Identity;
using Intune.WipeMonitor.Components;
using Intune.WipeMonitor.Data;
using Intune.WipeMonitor.Hubs;
using Intune.WipeMonitor.Models;
using Intune.WipeMonitor.Services;
using Intune.WipeMonitor.Shared.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Serilog;
using Serilog.Events;

Console.WriteLine("[STARTUP] Intune Wipe Monitor starting...");

var builder = WebApplication.CreateBuilder(args);

// Serilog — Console + File CMTrace (nessuna dipendenza da DI)
Console.WriteLine("[STARTUP] Configuring Serilog...");
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            formatter: new CMTraceFormatter("WipeMonitor.Web"),
            path: "logs/wipemonitor-web-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30);
});
Console.WriteLine("[STARTUP] Serilog OK");

// Azure App Configuration + Key Vault references (con timeout e fallback)
var appConfigEndpoint = builder.Configuration["AppConfig:Endpoint"];
var appConfigLoaded = false;
if (!string.IsNullOrEmpty(appConfigEndpoint))
{
    Console.WriteLine($"[STARTUP] Connecting to App Configuration: {appConfigEndpoint}");
    try
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
                })
                .ConfigureStartupOptions(startup =>
                {
                    startup.Timeout = TimeSpan.FromSeconds(20);
                });
        });
        builder.Services.AddAzureAppConfiguration();
        appConfigLoaded = true;
        Console.WriteLine("[STARTUP] App Configuration connected OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STARTUP] WARN: App Configuration unreachable: {ex.Message}");
        Console.WriteLine("[STARTUP] Falling back to local/AppSettings configuration");
    }
}

// Application Insights
builder.Services.AddApplicationInsightsTelemetry();

// Entra ID Authentication
builder.Services.AddMicrosoftIdentityWebAppAuthentication(builder.Configuration, "AzureAd");
builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WipeMonitorAdmin", policy =>
        policy.RequireRole("WipeMonitor-Admin"));
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddCascadingAuthenticationState();

// Configuration binding(solo config pertinente al web app — AD e SCCM sono gestiti dall'agent on-prem)
builder.Services.Configure<WipeMonitorSettings>(
    builder.Configuration.GetSection(WipeMonitorSettings.SectionName));
builder.Services.Configure<GraphSettings>(
    builder.Configuration.GetSection(GraphSettings.SectionName));

// Database (SQLite — portabile, può vivere su Azure File Share o share di rete)
var dbPath = builder.Configuration["Database:Path"] ?? "wipemonitor.db";
var dbDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
    Directory.CreateDirectory(dbDir);
var connectionString = $"Data Source={dbPath}";
Console.WriteLine($"[STARTUP] Database: {dbPath}");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

// HTTP clients
builder.Services.AddHttpClient<GraphWipeMonitorService>();

// Services
builder.Services.AddScoped<GraphWipeMonitorService>();
builder.Services.AddScoped<CleanupOrchestrator>();
builder.Services.AddSingleton<CleanupTelemetryService>();

// SignalR
builder.Services.AddSignalR();

// Background service
builder.Services.AddSingleton<WipeMonitorBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WipeMonitorBackgroundService>());

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

Console.WriteLine("[STARTUP] Building app...");
var app = builder.Build();

// Startup banner (Serilog è pronto dopo Build, DI è disponibile)
StartupBanner.PrintWebBanner(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"),
    app.Configuration);

// Ensure DB schema (non-blocking - first SQL connection via MI token is slow)
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        Log.Information("[STARTUP] Database schema verified");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "[STARTUP] DB init deferred, will retry on first request");
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Serilog request logging
app.UseSerilogRequestLogging();

if (appConfigLoaded)
    app.UseAzureAppConfiguration();

app.UseStatusCodePagesWithRedirects("/not-found");
app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapControllers(); // For Microsoft Identity UI sign-in/sign-out

app.MapHub<CleanupHub>("/hub/cleanup");

Log.Information("[STARTUP] Pipeline ready, starting server...");
app.Run();
