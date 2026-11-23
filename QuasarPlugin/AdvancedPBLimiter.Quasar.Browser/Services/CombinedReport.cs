using Advanced_PB_Limiter.Utils;

using System.Globalization;
using System.Text;
using Advanced_PB_Limiter.QuasarContracts;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>What one server gave for a report: a snapshot, or the reason there is none.</summary>
public sealed record ServerReport(string ServerName, ReportSnapshotView? Snapshot, string? Problem);

/// <summary>Builds the text report, in the layout of the one Torch saves, for any number of servers.</summary>
public static class CombinedReport
{
    private const string Rule = "-----------------------";

    public static string Build(IReadOnlyList<ServerReport> servers, DateTime generatedUtc)
    {
        List<ServerReport> answered = servers.Where(s => s.Snapshot != null).ToList();
        List<BlockOnServer> blocks = answered.SelectMany(s => s.Snapshot!.Blocks.Select(b => new BlockOnServer(s.ServerName, b))).ToList();

        StringBuilder text = new();
        Line(text, "Advanced PB Limiter Report");
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Report generated at {generatedUtc:yyyy-MM-dd HH:mm:ss} UTC"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Servers: {answered.Count} of {servers.Count} selected"));
        foreach (ServerReport server in servers.Where(s => s.Snapshot == null))
            Line(text, $"Not included: {server.ServerName}: {server.Problem}");
        Line(text, "");

        AppendOverall(text, answered, blocks);
        foreach (ServerReport server in answered)
            AppendServer(text, server);

        Line(text, "");
        Line(text, Rule);
        Line(text, "End Of Report");
        Line(text, Rule);
        return text.ToString();
    }

    private static void AppendOverall(StringBuilder text, List<ServerReport> servers, List<BlockOnServer> blocks)
    {
        List<PlayerRowView> players = servers.SelectMany(s => s.Snapshot!.Players).ToList();

        Line(text, "> All Selected Servers");
        Line(text, Rule);
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Total Players Tracked: {players.Count}"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Total Programmable Blocks Tracked: {players.Sum(p => p.BlockCount)}"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Total Offences: {players.Sum(p => p.Offences)}"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Total Recompiles: {players.Sum(p => p.Recompiles)}"));
        // A resting block's last run may be an hour old.
        Line(text, $"Hottest Block: {Describe(Max(blocks.Where(b => !b.Block.Resting).ToList(), b => b.Block.LastRunTimeMS))}");
        Line(text, $"Block With Most Offences: {Describe(Max(blocks, b => b.Block.Offences))}");
        Line(text, $"Most Recompiled Block: {Describe(Max(blocks, b => b.Block.Recompiles))}");
        if (servers.Exists(s => s.Snapshot!.Truncated))
            Line(text, "Some servers have more blocks than a report carries, so the hottest, most offences and most recompiled blocks above are picked from the blocks listed below only; the totals count every block.");
        Line(text, "");
    }

    private static void AppendServer(StringBuilder text, ServerReport server)
    {
        ReportSnapshotView snapshot = server.Snapshot!;

        Line(text, "");
        Line(text, $"> Server: {server.ServerName}");
        Line(text, Rule);
        Line(text, $"Version: {snapshot.Version}");
        Line(text, $"Limiter: {(snapshot.Enabled ? "enabled" : "disabled")}");
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Players: {snapshot.Players.Count}"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Blocks Tracked: {snapshot.Players.Sum(p => p.BlockCount)}"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Offences: {snapshot.Players.Sum(p => p.Offences)}"));
        if (snapshot.Truncated)
            Line(text, string.Create(CultureInfo.InvariantCulture, $"Only the first {snapshot.Blocks.Count} blocks are listed. Player totals still count all of them."));

        Line(text, "");
        Line(text, "Per Player");
        foreach (PlayerRowView player in snapshot.Players.OrderByDescending(p => p.CombinedLastRunTimeMS))
        {
            string tags = (player.IsNpc ? " [NPC]" : "") + (player.IsPrivileged ? " [Privileged]" : "");
            Line(text, string.Create(CultureInfo.InvariantCulture, $"    {player.Name}{tags}  ||  SteamID: {(player.IsNpc ? "-" : player.SteamId)}  ||  Blocks: {player.BlockCount}")
                       + $"  ||  CombinedRunTimeMS: {Figures.Ms(player.CombinedLastRunTimeMS)}  ||  CombinedAvgMS: {Figures.Ms(player.CombinedRunTimeMSAvg)}"
                       + string.Create(CultureInfo.InvariantCulture, $"  ||  Offences: {player.Offences}  ||  Recompiles: {player.Recompiles}  ||  MemoryUsed: {Figures.MemoryTotal(player.MemoryBytes, player.MemoryUnmeasured)}"));
        }

        Line(text, "");
        Line(text, "Per Programmable Block");
        foreach (BlockRowView block in snapshot.Blocks.OrderByDescending(b => b.LastRunTimeMS))
        {
            Line(text, $"    {block.BlockName}  ||  GridName: {block.GridName}  ||  Owner: {block.OwnerName}  ||  State: {(block.Enabled ? "On" : "Off")}{(block.Resting ? " (resting, not run lately)" : "")}"
                       + $"  ||  LastRunTimeMS: {Figures.Ms(block.LastRunTimeMS)}  ||  AvgMS: {Figures.Ms(block.RunTimeMSAvg)}  ||  PeakMS: {Figures.Ms(block.PeekRunTimeMS)}"
                       + string.Create(CultureInfo.InvariantCulture, $"  ||  Offences: {block.Offences}  ||  Recompiles: {block.Recompiles}  ||  MemoryUsed: {Figures.Memory(block.MemoryBytes, block.MemoryUnmeasured)}"));
        }
    }

    /// <summary>Nothing when every block has 0, so a report never singles out a block for having no offences.</summary>
    private static BlockOnServer? Max(List<BlockOnServer> blocks, Func<BlockOnServer, double> value)
    {
        BlockOnServer? found = blocks.MaxBy(value);
        return found != null && value(found) > 0 ? found : null;
    }

    private static string Describe(BlockOnServer? found)
    {
        if (found == null) return "None";

        BlockRowView block = found.Block;
        return $"{block.BlockName} on {block.GridName} owned by {block.OwnerName}, on {found.ServerName}"
               + $" ({Figures.Ms(block.LastRunTimeMS)}ms, {InvariantText.Count(block.Offences, "offence", "offences")}, {InvariantText.Count(block.Recompiles, "recompile", "recompiles")})";
    }

    // A report is opened on any system, so it does not depend on the line ending of the one Quasar runs on.
    private static void Line(StringBuilder text, string line) => text.Append(line).Append("\r\n");

    private sealed record BlockOnServer(string ServerName, BlockRowView Block);
}
