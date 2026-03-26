using ClosedXML.Excel;
using Intune.WipeMonitor.Report.Models;
using Microsoft.Extensions.Logging;

namespace Intune.WipeMonitor.Report.Services;

/// <summary>
/// Genera il report Excel (.xlsx) con ClosedXML.
/// Sheet 1: Tutte le wipe actions del periodo
/// Sheet 2: Device con Entra ancora presente (pending cleanup)
/// Sheet 3: Summary statistiche
/// </summary>
public class ExcelReportBuilder
{
    private readonly ILogger<ExcelReportBuilder> _logger;

    public ExcelReportBuilder(ILogger<ExcelReportBuilder> logger)
    {
        _logger = logger;
    }

    public byte[] Build(List<WipeReportEntry> entries, int reportDays)
    {
        using var workbook = new XLWorkbook();

        BuildWipeActionsSheet(workbook, entries, reportDays);
        BuildEntraPendingSheet(workbook, entries);
        BuildSummarySheet(workbook, entries, reportDays);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        _logger.LogInformation("Report Excel generato: {Sheets} sheet, {Size} KB",
            workbook.Worksheets.Count, stream.Length / 1024);
        return stream.ToArray();
    }

    private static void BuildWipeActionsSheet(XLWorkbook wb, List<WipeReportEntry> entries, int days)
    {
        var ws = wb.AddWorksheet($"Wipe Actions ({days}d)");

        // Header
        var headers = new[] { "Device Name", "Wipe Requested", "Wipe Completed", "State",
            "Initiated By", "User", "Serial Number", "OS", "OS Version",
            "Entra Present", "Entra Device ID", "Trust Type", "Last Entra Sign-In", "Entra Enabled",
            "Intune Present", "Managed Device ID" };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        StyleHeader(ws, headers.Length);

        // Data
        int row = 2;
        foreach (var e in entries.OrderByDescending(e => e.WipeRequestedAt))
        {
            ws.Cell(row, 1).Value = e.DeviceName;
            ws.Cell(row, 2).Value = e.WipeRequestedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
            ws.Cell(row, 3).Value = e.WipeCompletedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
            ws.Cell(row, 4).Value = e.ActionState;
            ws.Cell(row, 5).Value = e.InitiatedBy;
            ws.Cell(row, 6).Value = e.IntuneUser ?? "";
            ws.Cell(row, 7).Value = e.IntuneSerialNumber ?? "";
            ws.Cell(row, 8).Value = e.OperatingSystem ?? "";
            ws.Cell(row, 9).Value = e.OperatingSystemVersion ?? "";
            ws.Cell(row, 10).Value = e.EntraDeviceFound ? "Yes" : "No";
            ws.Cell(row, 11).Value = e.EntraDeviceId ?? "";
            ws.Cell(row, 12).Value = e.TrustType ?? "";
            ws.Cell(row, 13).Value = e.EntraLastSignIn?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
            ws.Cell(row, 14).Value = e.EntraAccountEnabled?.ToString() ?? "";
            ws.Cell(row, 15).Value = e.IntuneDeviceFound ? "Yes" : "No";
            ws.Cell(row, 16).Value = e.ManagedDeviceId;

            if (e.IsEntraPending)
                ws.Row(row).Style.Fill.BackgroundColor = XLColor.LightYellow;

            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        if (row > 2) ws.RangeUsed()?.SetAutoFilter();
    }

    private static void BuildEntraPendingSheet(XLWorkbook wb, List<WipeReportEntry> entries)
    {
        var ws = wb.AddWorksheet("Entra Cleanup Pending");
        var pending = entries.Where(e => e.IsEntraPending).OrderBy(e => e.DeviceName).ToList();

        var headers = new[] { "Device Name", "Entra Device ID", "OS", "OS Version",
            "Trust Type", "Account Enabled", "Last Sign-In", "Wipe Date", "Initiated By" };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        StyleHeader(ws, headers.Length);

        int row = 2;
        foreach (var e in pending)
        {
            ws.Cell(row, 1).Value = e.DeviceName;
            ws.Cell(row, 2).Value = e.EntraDeviceId ?? "";
            ws.Cell(row, 3).Value = e.OperatingSystem ?? "";
            ws.Cell(row, 4).Value = e.OperatingSystemVersion ?? "";
            ws.Cell(row, 5).Value = e.TrustType ?? "";
            ws.Cell(row, 6).Value = e.EntraAccountEnabled?.ToString() ?? "";
            ws.Cell(row, 7).Value = e.EntraLastSignIn?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
            ws.Cell(row, 8).Value = e.WipeCompletedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
            ws.Cell(row, 9).Value = e.InitiatedBy;
            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        if (row > 2) ws.RangeUsed()?.SetAutoFilter();
    }

    private static void BuildSummarySheet(XLWorkbook wb, List<WipeReportEntry> entries, int days)
    {
        var ws = wb.AddWorksheet("Summary");

        ws.Cell("A1").Value = "Intune Wipe Monitor — Report";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;

        ws.Cell("A3").Value = "Generated:";
        ws.Cell("B3").Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        ws.Cell("A4").Value = "Period:";
        ws.Cell("B4").Value = $"Last {days} days";

        ws.Cell("A6").Value = "Metric";
        ws.Cell("B6").Value = "Count";
        ws.Range("A6:B6").Style.Font.Bold = true;
        ws.Range("A6:B6").Style.Fill.BackgroundColor = XLColor.DarkBlue;
        ws.Range("A6:B6").Style.Font.FontColor = XLColor.White;

        var stats = new (string Label, int Value)[]
        {
            ("Total Wipe Actions", entries.Count),
            ("Wipe Completed", entries.Count(e => e.ActionState == "done")),
            ("Wipe Pending", entries.Count(e => e.ActionState != "done")),
            ("Entra Device Present", entries.Count(e => e.EntraDeviceFound)),
            ("Entra Device Removed", entries.Count(e => !e.EntraDeviceFound)),
            ("⚠ Pending Entra Cleanup", entries.Count(e => e.IsEntraPending))
        };

        for (int i = 0; i < stats.Length; i++)
        {
            ws.Cell(7 + i, 1).Value = stats[i].Label;
            ws.Cell(7 + i, 2).Value = stats[i].Value;
        }

        // Highlight pending cleanup row
        var pendingRow = 7 + stats.Length - 1;
        ws.Range(pendingRow, 1, pendingRow, 2).Style.Fill.BackgroundColor = XLColor.LightYellow;
        ws.Range(pendingRow, 1, pendingRow, 2).Style.Font.Bold = true;

        ws.Columns().AdjustToContents();
    }

    private static void StyleHeader(IXLWorksheet ws, int colCount)
    {
        var headerRange = ws.Range(1, 1, 1, colCount);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.DarkBlue;
        headerRange.Style.Font.FontColor = XLColor.White;
    }
}
