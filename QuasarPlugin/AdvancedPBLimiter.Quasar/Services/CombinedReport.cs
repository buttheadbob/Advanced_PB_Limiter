using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Advanced_PB_Limiter.QuasarContracts;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>What one server gave for a report: a snapshot, or the reason there is none.</summary>
public sealed record ServerReport(string ServerName, ReportSnapshot? Snapshot, string? Problem);

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
        Line(text, $"Report generated at {generatedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        Line(text, $"Servers: {answered.Count} of {servers.Count} selected");
        foreach (ServerReport server in servers.Where(s => s.Snapshot == null))
            Line(text, $"Not included: {server.ServerName} ({server.Problem})");
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
        List<PlayerRow> players = servers.SelectMany(s => s.Snapshot!.Players).ToList();

        Line(text, "> All Selected Servers");
        Line(text, Rule);
        Line(text, $"Total Players Tracked: {players.Count}");
        Line(text, $"Total Programmable Blocks Tracked: {players.Sum(p => p.BlockCount)}");
        Line(text, $"Total Offences: {players.Sum(p => p.Offences)}");
        Line(text, $"Total Recompiles: {players.Sum(p => p.Recompiles)}");
        Line(text, $"Hottest Block: {Describe(Max(blocks, b => b.Block.LastRunTimeMS))}");
        Line(text, $"Block With Most Offences: {Describe(Max(blocks, b => b.Block.Offences))}");
        Line(text, $"Most Recompiled Block: {Describe(Max(blocks, b => b.Block.Recompiles))}");
        if (servers.Any(s => s.Snapshot!.Truncated))
            Line(text, "Some servers have more blocks than a report carries, so the block figures above cover only the blocks listed below.");
        Line(text, "");
    }

    private static void AppendServer(StringBuilder text, ServerReport server)
    {
        ReportSnapshot snapshot = server.Snapshot!;

        Line(text, "");
        Line(text, $"> Server: {server.ServerName}");
        Line(text, Rule);
        Line(text, $"Version: {snapshot.Version}");
        Line(text, $"Limiter: {(snapshot.Enabled ? "enabled" : "disabled")}");
        Line(text, $"Players: {snapshot.Players.Count}");
        Line(text, $"Blocks Tracked: {snapshot.Players.Sum(p => p.BlockCount)}");
        Line(text, $"Offences: {snapshot.Players.Sum(p => p.Offences)}");
        if (snapshot.Truncated)
            Line(text, $"Only the first {snapshot.Blocks.Count} blocks are listed. Player totals still count all of them.");

        Line(text, "");
        Line(text, "Per Player");
        foreach (PlayerRow player in snapshot.Players.OrderByDescending(p => p.CombinedLastRunTimeMS))
        {
            string tags = (player.IsNpc ? " [NPC]" : "") + (player.IsPrivileged ? " [Privileged]" : "");
            Line(text, $"    {player.Name}{tags}  ||  SteamID: {(player.IsNpc ? "-" : Number(player.SteamId))}  ||  Blocks: {player.BlockCount}"
                       + $"  ||  CombinedRunTimeMS: {Ms(player.CombinedLastRunTimeMS)}  ||  CombinedAvgMS: {Ms(player.CombinedRunTimeMSAvg)}"
                       + $"  ||  Offences: {player.Offences}  ||  Recompiles: {player.Recompiles}  ||  MemoryUsed: {Memory(player.MemoryBytes)}");
        }

        Line(text, "");
        Line(text, "Per Programmable Block");
        foreach (BlockRow block in snapshot.Blocks.OrderByDescending(b => b.LastRunTimeMS))
        {
            Line(text, $"    {block.BlockName}  ||  GridName: {block.GridName}  ||  Owner: {block.OwnerName}  ||  State: {(block.Enabled ? "On" : "Off")}"
                       + $"  ||  LastRunTimeMS: {Ms(block.LastRunTimeMS)}  ||  AvgMS: {Ms(block.RunTimeMSAvg)}  ||  PeakMS: {Ms(block.PeekRunTimeMS)}"
                       + $"  ||  Offences: {block.Offences}  ||  Recompiles: {block.Recompiles}  ||  MemoryUsed: {Memory(block.MemoryBytes)}");
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

        BlockRow block = found.Block;
        return $"{block.BlockName} on {block.GridName} owned by {block.OwnerName}, on {found.ServerName}"
               + $" ({Ms(block.LastRunTimeMS)}ms, {block.Offences} offences, {block.Recompiles} recompiles)";
    }

    private static string Ms(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Memory(long bytes) => bytes <= 0 ? "-" : (bytes / 1024d).ToString("N2", CultureInfo.InvariantCulture) + " KB";

    // A report is opened on any system, so it does not depend on the line ending of the one Quasar runs on.
    private static void Line(StringBuilder text, string line) => text.Append(line).Append("\r\n");

    private sealed record BlockOnServer(string ServerName, BlockRow Block);
}

/// <summary>Holds a finished report for a short while, for the download link. They are only ever kept in memory.</summary>
public sealed class ReportDownloads
{
    private const int MaxReports = 5;
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, Entry> _reports = new();

    public Guid Add(string text, string fileName)
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<Guid, Entry> old in _reports.Where(r => now - r.Value.CreatedUtc > KeepFor).ToList())
            _reports.TryRemove(old.Key, out _);

        // A report of many large servers is a few megabytes, so only the newest few stay.
        foreach (KeyValuePair<Guid, Entry> extra in _reports.OrderByDescending(r => r.Value.CreatedUtc).Skip(MaxReports - 1).ToList())
            _reports.TryRemove(extra.Key, out _);

        Guid id = Guid.NewGuid();
        _reports[id] = new Entry(Encoding.UTF8.GetBytes(text), fileName, now);
        return id;
    }

    public bool TryGet(Guid id, out byte[] content, out string fileName)
    {
        if (_reports.TryGetValue(id, out Entry? entry) && DateTime.UtcNow - entry.CreatedUtc <= KeepFor)
        {
            content = entry.Content;
            fileName = entry.FileName;
            return true;
        }

        content = Array.Empty<byte>();
        fileName = "";
        return false;
    }

    private sealed record Entry(byte[] Content, string FileName, DateTime CreatedUtc);
}
