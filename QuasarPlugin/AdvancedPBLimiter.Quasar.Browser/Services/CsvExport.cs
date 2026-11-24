using System.Globalization;
using System.Text;
using Advanced_PB_Limiter.QuasarContracts;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// CSV built in the browser from the snapshots already on the page. Fields are quoted and formula-looking values are
/// neutralized so a player or grid name cannot turn into a spreadsheet formula when the file is opened.
/// </summary>
public static class CsvExport
{
    private static readonly string[] BlockHeader =
    [
        "Server", "Player", "SteamId", "Grid", "Block", "State", "Resting",
        "LastMs", "AverageMs", "PeakMs", "Offences", "Recompiles", "MemoryBytes", "MemoryUnmeasured",
    ];

    public static string ForBlocks(ReportSnapshotView snapshot, string serverName)
    {
        StringBuilder csv = new();
        Line(csv, BlockHeader);

        Dictionary<string, string> players = snapshot.Players
            .GroupBy(player => player.IdentityId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

        foreach (BlockRowView block in snapshot.Blocks.OrderBy(block => block.LastRunTimeMS > 0 ? -block.LastRunTimeMS : 0))
        {
            players.TryGetValue(block.OwnerIdentityId, out string? playerName);
            Line(csv,
                serverName,
                playerName ?? block.OwnerName,
                block.OwnerIdentityId,
                block.GridName,
                block.BlockName,
                block.Enabled ? "On" : "Off",
                block.Resting ? "Yes" : "No",
                Number(block.LastRunTimeMS),
                Number(block.RunTimeMSAvg),
                Number(block.PeekRunTimeMS),
                block.Offences.ToString(CultureInfo.InvariantCulture),
                block.Recompiles.ToString(CultureInfo.InvariantCulture),
                block.MemoryBytes.ToString(CultureInfo.InvariantCulture),
                block.MemoryUnmeasured ? "Yes" : "No");
        }

        return csv.ToString();
    }

    public static string ForServers(IReadOnlyList<ServerReport> reports)
    {
        StringBuilder csv = new();
        bool header = false;

        foreach (ServerReport report in reports)
        {
            if (report.Snapshot is null) continue;
            string text = ForBlocks(report.Snapshot, report.ServerName);
            string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;

            if (!header)
            {
                csv.AppendLine(lines[0]);
                header = true;
            }

            for (int index = 1; index < lines.Length; index++)
                csv.AppendLine(lines[index]);
        }

        return csv.ToString();
    }

    private static void Line(StringBuilder csv, params string[] fields)
    {
        for (int index = 0; index < fields.Length; index++)
        {
            if (index > 0) csv.Append(',');
            csv.Append(Field(fields[index]));
        }

        csv.Append("\r\n");
    }

    private static string Field(string? value)
    {
        value ??= string.Empty;

        // Excel and LibreOffice treat a leading =, +, -, or @ as a formula. A leading apostrophe is shown as text.
        if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            value = "'" + value;

        bool quote = value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n');
        if (quote)
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
