using Azure.Identity;
using Intune.WipeMonitor.Components;
using Intune.WipeMonitor.Data;
using Intune.WipeMonitor.Hubs;
using Intune.WipeMonitor.Models;
using Intune.WipeMonitor.Services;
using Microsoft.EntityFrameworkCore;

Console.WriteLine("[STARTUP] Intune Wipe Monitor starting...");

var builder = WebApplication.CreateBuilder(args);

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

// Configuration binding
builder.Services.Configure<WipeMonitorSettings>(
    builder.Configuration.GetSection(WipeMonitorSettings.SectionName));
builder.Services.Configure<GraphSettings>(
    builder.Configuration.GetSection(GraphSettings.SectionName));
builder.Services.Configure<ActiveDirectorySettings>(
    builder.Configuration.GetSection(ActiveDirectorySettings.SectionName));
builder.Services.Configure<SccmSettings>(
    builder.Configuration.GetSection(SccmSettings.SectionName));

// Database (Azure SQL with Managed Identity)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

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

// Ensure DB schema (non-blocking - first SQL connection via MI token is slow)
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        Console.WriteLine("[STARTUP] Database schema verified");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STARTUP] WARN: DB init deferred: {ex.Message}");
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (appConfigLoaded)
    app.UseAzureAppConfiguration();

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<CleanupHub>("/hub/cleanup");

Console.WriteLine("[STARTUP] Pipeline ready, starting server...");
app.Run();
