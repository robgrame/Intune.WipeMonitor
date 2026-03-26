using Intune.WipeMonitor.Report.Models;
using Intune.WipeMonitor.Report.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var host = new HostBuilder()
        .UseSerilog()
        .ConfigureFunctionsWorkerDefaults()
        .ConfigureServices((context, services) =>
        {
            services.AddApplicationInsightsTelemetryWorkerService();

            // Settings
            services.Configure<GraphSettings>(
                context.Configuration.GetSection(GraphSettings.SectionName));
            services.Configure<ReportSettings>(
                context.Configuration.GetSection(ReportSettings.SectionName));

            // Services
            services.AddHttpClient<GraphReportService>();
            services.AddHttpClient<SharePointUploadService>();
            services.AddHttpClient<TeamsReportNotifier>();
            services.AddSingleton<ExcelReportBuilder>();
        })
        .Build();

    Log.Information("Intune Wipe Monitor Report Function starting...");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
