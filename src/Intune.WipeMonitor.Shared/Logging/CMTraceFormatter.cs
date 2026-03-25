using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;

namespace Intune.WipeMonitor.Shared.Logging;

/// <summary>
/// Serilog ITextFormatter che produce output nel formato CMTrace (SCCM/OneTrace).
///
/// Formato CMTrace:
///   &lt;![LOG[messaggio]LOG]!&gt;&lt;time="HH:mm:ss.fff+000" date="MM-DD-YYYY" component="Component" context="" type="1" thread="0" file=""&gt;
///
/// Tipi CMTrace:
///   1 = Informational
///   2 = Warning
///   3 = Error
///
/// Apri i file .log generati con CMTrace.exe o OneTrace per ottenere
/// colorazione automatica, filtri e ricerca.
/// </summary>
public class CMTraceFormatter : ITextFormatter
{
    private readonly string _componentName;

    /// <param name="componentName">Nome del componente visualizzato nella colonna Component di CMTrace.</param>
    public CMTraceFormatter(string componentName = "WipeMonitor")
    {
        _componentName = componentName;
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var message = logEvent.RenderMessage().Replace("\r", "").Replace("\n", " ");

        // Accoda eventuale eccezione al messaggio
        if (logEvent.Exception is not null)
        {
            message += $" | Exception: {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";
        }

        var type = logEvent.Level switch
        {
            LogEventLevel.Verbose => 1,
            LogEventLevel.Debug => 1,
            LogEventLevel.Information => 1,
            LogEventLevel.Warning => 2,
            LogEventLevel.Error => 3,
            LogEventLevel.Fatal => 3,
            _ => 1
        };

        var timestamp = logEvent.Timestamp.LocalDateTime;
        var time = timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var date = timestamp.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        var offset = logEvent.Timestamp.Offset.TotalMinutes >= 0
            ? $"+{(int)logEvent.Timestamp.Offset.TotalMinutes}"
            : $"{(int)logEvent.Timestamp.Offset.TotalMinutes}";

        // Estrai il nome del source context (il logger originale) come component
        var component = _componentName;
        if (logEvent.Properties.TryGetValue("SourceContext", out var sourceContext))
        {
            var ctx = sourceContext.ToString().Trim('"');
            // Prendi solo il nome della classe (ultima parte del namespace)
            var lastDot = ctx.LastIndexOf('.');
            component = lastDot >= 0 ? ctx[(lastDot + 1)..] : ctx;
        }

        var thread = Environment.CurrentManagedThreadId;

        // Formato CMTrace
        output.Write($"<![LOG[{message}]LOG]!>");
        output.Write($"<time=\"{time}{offset}\" ");
        output.Write($"date=\"{date}\" ");
        output.Write($"component=\"{component}\" ");
        output.Write($"context=\"\" ");
        output.Write($"type=\"{type}\" ");
        output.Write($"thread=\"{thread}\" ");
        output.Write($"file=\"\">");
        output.WriteLine();
    }
}
