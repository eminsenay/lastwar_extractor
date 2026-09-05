using ClosedXML.Excel;
using lawar4.Models;

namespace lawar4.Services;

/// <summary>Generates the multi-sheet weekly workbook. Ported from excel_export.py.</summary>
public static class ExcelExporter
{
    private const string HeaderFill = "#1F4E78";
    private const string HeaderFont = "#FFFFFF";
    private const string ImportedFont = "#008000";
    private const string StaticFont = "#666666";
    private const string ReviewFill = "#FCE4D6";

    public static void ExportWeeklyWorkbook(
        string path,
        List<Member> members,
        WeeklyData weekly,
        AliasStore aliasStore,
        string memberSource)
    {
        using var wb = new XLWorkbook();

        BuildWeeklyScores(wb, members, weekly);
        BuildObservations(wb, weekly);
        BuildIssues(wb, weekly);
        BuildAliases(wb, aliasStore);
        BuildRunInfo(wb, members, weekly, memberSource);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        wb.SaveAs(path);
    }

    private static void StyleHeader(IXLWorksheet ws, int row, int maxCol)
    {
        for (int col = 1; col <= maxCol; col++)
        {
            var cell = ws.Cell(row, col);
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderFill);
            cell.Style.Font.FontColor = XLColor.FromHtml(HeaderFont);
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }

    private static void AutoWidth(IXLWorksheet ws, double minWidth = 10, double maxWidth = 44)
        => ws.Columns().AdjustToContents(minWidth, maxWidth);

    private static void BuildWeeklyScores(XLWorkbook wb, List<Member> members, WeeklyData weekly)
    {
        var ws = wb.AddWorksheet("Weekly Scores");
        ws.ShowGridLines = false;

        var headers = new List<string> { "ID", "Player" };
        headers.AddRange(TextUtil.DayOrder.Select(TitleCase));
        for (int i = 0; i < headers.Count; i++)
            ws.Cell(1, i + 1).Value = headers[i];
        StyleHeader(ws, 1, headers.Count);
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(2);

        int row = 2;
        foreach (var member in members.OrderBy(m => m.MemberId))
        {
            ws.Cell(row, 1).Value = member.MemberId;
            ws.Cell(row, 2).Value = member.Name;
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml(StaticFont);
            ws.Cell(row, 2).Style.Font.FontColor = XLColor.FromHtml(StaticFont);
            for (int d = 0; d < TextUtil.DayOrder.Length; d++)
            {
                if (weekly.Scores.TryGetValue((member.MemberId, TextUtil.DayOrder[d]), out var points))
                {
                    var cell = ws.Cell(row, 3 + d);
                    cell.Value = points;
                    cell.Style.NumberFormat.Format = "#,##0";
                    cell.Style.Font.FontColor = XLColor.FromHtml(ImportedFont);
                }
            }
            row++;
        }
        AutoWidth(ws);
    }

    private static void BuildObservations(XLWorkbook wb, WeeklyData weekly)
    {
        var ws = wb.AddWorksheet("Observations");
        ws.ShowGridLines = false;
        var headers = new[]
        {
            "Source file", "Day", "Rank", "Visible player ID", "Raw name", "Points",
            "Extraction confidence", "Pinned row", "Matched member ID", "Matched member",
            "Match method", "Match confidence", "Issue",
        };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];
        StyleHeader(ws, 1, headers.Length);
        ws.SheetView.FreezeRows(1);

        int row = 2;
        foreach (var obs in weekly.Observations)
        {
            ws.Cell(row, 1).Value = obs.SourceFile;
            ws.Cell(row, 2).Value = obs.Day;
            ws.Cell(row, 3).Value = obs.Rank;
            if (obs.RawPlayerId is int pid)
                ws.Cell(row, 4).Value = pid;
            ws.Cell(row, 5).Value = obs.RawName;
            ws.Cell(row, 6).Value = obs.Points;
            ws.Cell(row, 7).Value = obs.ExtractionConfidence;
            ws.Cell(row, 8).Value = obs.IsPinnedRow;
            if (obs.MatchedMemberId is int mid)
                ws.Cell(row, 9).Value = mid;
            ws.Cell(row, 10).Value = obs.MatchedMemberName ?? "";
            ws.Cell(row, 11).Value = obs.MatchMethod;
            ws.Cell(row, 12).Value = obs.MatchConfidence;
            ws.Cell(row, 13).Value = obs.Issue ?? "";

            ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 7).Style.NumberFormat.Format = "0%";
            ws.Cell(row, 12).Style.NumberFormat.Format = "0%";

            if (!string.IsNullOrEmpty(obs.Issue))
            {
                for (int col = 1; col <= headers.Length; col++)
                    ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.FromHtml(ReviewFill);
            }
            row++;
        }
        AutoWidth(ws);
    }

    private static void BuildIssues(XLWorkbook wb, WeeklyData weekly)
    {
        var ws = wb.AddWorksheet("Issues");
        ws.ShowGridLines = false;
        ws.Cell(1, 1).Value = "Type";
        ws.Cell(1, 2).Value = "Details";
        StyleHeader(ws, 1, 2);

        int row = 2;
        foreach (var issue in weekly.Issues)
        {
            ws.Cell(row, 1).Value = "Review";
            ws.Cell(row, 2).Value = issue;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml(ReviewFill);
            ws.Cell(row, 2).Style.Fill.BackgroundColor = XLColor.FromHtml(ReviewFill);
            row++;
        }
        foreach (var (day, missing) in weekly.MissingByDay)
        {
            foreach (var member in missing)
            {
                ws.Cell(row, 1).Value = "Missing player";
                ws.Cell(row, 2).Value = $"{TitleCase(day)}: {member.MemberId} - {member.Name}";
                row++;
            }
        }
        AutoWidth(ws, maxWidth: 90);
    }

    private static void BuildAliases(XLWorkbook wb, AliasStore aliasStore)
    {
        var ws = wb.AddWorksheet("Aliases");
        ws.ShowGridLines = false;
        ws.Cell(1, 1).Value = "Observed alias";
        ws.Cell(1, 2).Value = "Member ID";
        StyleHeader(ws, 1, 2);

        int row = 2;
        foreach (var (alias, memberId) in aliasStore.ListAliases())
        {
            ws.Cell(row, 1).Value = alias;
            ws.Cell(row, 2).Value = memberId;
            row++;
        }
        AutoWidth(ws);
    }

    private static void BuildRunInfo(XLWorkbook wb, List<Member> members, WeeklyData weekly, string memberSource)
    {
        var ws = wb.AddWorksheet("Run Info");
        ws.ShowGridLines = false;
        ws.Cell(1, 1).Value = "Setting";
        ws.Cell(1, 2).Value = "Value";
        StyleHeader(ws, 1, 2);
        ws.Cell(2, 1).Value = "Members source";
        ws.Cell(2, 2).Value = memberSource;
        ws.Cell(3, 1).Value = "Active members";
        ws.Cell(3, 2).Value = members.Count;
        ws.Cell(4, 1).Value = "Observations";
        ws.Cell(4, 2).Value = weekly.Observations.Count;
        ws.Cell(5, 1).Value = "Issues";
        ws.Cell(5, 2).Value = weekly.Issues.Count;
        AutoWidth(ws, maxWidth: 100);
    }

    private static string TitleCase(string day) =>
        day.Length == 0 ? day : char.ToUpperInvariant(day[0]) + day[1..];
}
