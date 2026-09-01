using System.Text.RegularExpressions;
using ClosedXML.Excel;
using LastWarExtractor.Models;

namespace LastWarExtractor.Services;

public static class MembersLoader
{
    private static readonly HttpClient Http = new();

    public static MemberLoadResult LoadFromXlsx(string path, string sheetName = "Members")
    {
        using var wb = new XLWorkbook(path);
        return LoadFromWorkbook(wb, sheetName, path);
    }

    public static async Task<MemberLoadResult> LoadFromGoogleSheetAsync(string url, string sheetName = "Members", CancellationToken cancellationToken = default)
    {
        var exportUrl = GoogleSheetExportUrl(url);
        byte[] data;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, exportUrl);
            request.Headers.Add("User-Agent", "LastWarWeeklyApp/1.0");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not download the Google Sheet anonymously. If your organization blocks this, " +
                "use the local Excel workbook source instead.", ex);
        }

        using var stream = new MemoryStream(data);
        using var wb = new XLWorkbook(stream);
        var result = LoadFromWorkbook(wb, sheetName, url);
        result.SourceDescription = url;
        return result;
    }

    private static MemberLoadResult LoadFromWorkbook(XLWorkbook wb, string sheetName, string sourceDescription)
    {
        if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
        {
            var available = string.Join(", ", wb.Worksheets.Select(w => w.Name));
            throw new InvalidOperationException($"Worksheet '{sheetName}' not found. Available: {available}");
        }

        var headerRow = ws.FirstRowUsed();
        if (headerRow is null)
            throw new InvalidOperationException("Members worksheet is empty");

        var index = new Dictionary<string, int>();
        foreach (var cell in headerRow.CellsUsed())
        {
            var text = cell.GetString().Trim();
            if (text.Length > 0 && !index.ContainsKey(text))
                index[text] = cell.Address.ColumnNumber;
        }

        foreach (var required in new[] { "ID", "Name", "Rank" })
        {
            if (!index.ContainsKey(required))
            {
                var missing = new[] { "ID", "Name", "Rank" }.Where(c => !index.ContainsKey(c));
                throw new InvalidOperationException($"Members worksheet is missing columns: {string.Join(", ", missing)}");
            }
        }

        var allMembers = new List<Member>();
        var warnings = new List<string>();

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();
        for (int rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRow; rowNumber++)
        {
            var row = ws.Row(rowNumber);
            int? memberId = CoerceInt(row.Cell(index["ID"]));
            string name = row.Cell(index["Name"]).GetString().Trim();
            string rank = row.Cell(index["Rank"]).GetString().Trim();

            bool nameBlank = string.IsNullOrEmpty(name);
            if (memberId is null && nameBlank)
                continue;
            if (memberId is null || nameBlank)
            {
                warnings.Add($"Members row {rowNumber} has missing/invalid ID or name and was skipped.");
                continue;
            }

            DateTime? joined = index.TryGetValue("Joined on", out var joinedCol) ? CoerceDate(row.Cell(joinedCol)) : null;
            double? heroPower = index.TryGetValue("Total Hero Power", out var hpCol) ? CoerceDouble(row.Cell(hpCol)) : null;

            allMembers.Add(new Member(memberId.Value, name, rank, joined, heroPower));
        }

        var active = allMembers.Where(m => !string.Equals(m.Rank, "left", StringComparison.OrdinalIgnoreCase)).ToList();

        var activeById = active.GroupBy(m => m.MemberId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (memberId, group) in activeById)
        {
            if (group.Count > 1)
                warnings.Add($"Duplicate active member ID {memberId}: {string.Join(", ", group.Select(m => m.Name))}");
        }

        var duplicateActiveIds = activeById.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToHashSet();
        var historicalById = allMembers.GroupBy(m => m.MemberId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (memberId, group) in historicalById)
        {
            if (group.Count > 1 && !duplicateActiveIds.Contains(memberId))
                warnings.Add(
                    $"Member ID {memberId} occurs multiple times historically; active filtering resolves it: " +
                    string.Join(", ", group.Select(m => $"{m.Name} ({m.Rank})")));
        }

        return new MemberLoadResult(active, allMembers, warnings, sourceDescription);
    }

    private static string GoogleSheetExportUrl(string url)
    {
        var match = Regex.Match(url, "/spreadsheets/d/([a-zA-Z0-9_-]+)");
        if (!match.Success)
            throw new InvalidOperationException("Could not find a Google spreadsheet ID in the URL");
        return $"https://docs.google.com/spreadsheets/d/{match.Groups[1].Value}/export?format=xlsx";
    }

    private static int? CoerceInt(IXLCell cell)
    {
        if (cell.IsEmpty())
            return null;
        if (cell.Value.IsNumber)
            return (int)cell.Value.GetNumber();
        var text = cell.GetString().Trim();
        if (text.Length == 0)
            return null;
        return double.TryParse(text, out var d) ? (int)d : null;
    }

    private static double? CoerceDouble(IXLCell cell)
    {
        if (cell.IsEmpty())
            return null;
        if (cell.Value.IsNumber)
            return cell.Value.GetNumber();
        return double.TryParse(cell.GetString().Trim(), out var d) ? d : null;
    }

    private static DateTime? CoerceDate(IXLCell cell)
    {
        if (cell.IsEmpty())
            return null;
        if (cell.Value.IsDateTime)
            return cell.Value.GetDateTime();
        return DateTime.TryParse(cell.GetString().Trim(), out var dt) ? dt : null;
    }
}
