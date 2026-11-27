#nullable enable
using System;
using System.Collections.Generic;

namespace Advanced_PB_Limiter.QuasarContracts
{
    // Companion channel contract between the Quasar plugin and its Magnetar companion, and the shape of the report the
    // limiter builds. All of them compile this file, so the operation names and payloads always match.
    // It must build for net48, netstandard2.0 (companion) and net10.0 (Quasar plugin), and holds no game types.

    public static class ReportOperations
    {
        /// <summary>Id of the Quasar plugin and of its companion on the servers.</summary>
        public const string CompanionId = "advancedpblimiter.quasar";

        /// <summary><see cref="SnapshotRequest"/> in, <see cref="ReportSnapshot"/> out. Older Quasar plugins send an empty object.</summary>
        public const string Snapshot = "report.snapshot";

        /// <summary><see cref="TurnOffBlockRequest"/> in, <see cref="TurnOffResult"/> out.</summary>
        public const string TurnOffBlock = "block.turnoff";

        /// <summary><see cref="TurnOffPlayerRequest"/> in, <see cref="TurnOffResult"/> out.</summary>
        public const string TurnOffPlayer = "player.turnoff";

        /// <summary>An empty object in, <see cref="ProfileData"/> out.</summary>
        public const string ProfileRead = "profile.read";

        /// <summary><see cref="ProfileApplyRequest"/> in, <see cref="ProfileData"/> out, describing what was applied.</summary>
        public const string ProfileApply = "profile.apply";

        /// <summary><see cref="BlockHistoryRequest"/> in, <see cref="BlockHistoryResult"/> out.</summary>
        public const string BlockHistory = "block.history";
    }

    /// <summary>What became of one run, decided as it came. Sent with the history as numbers, so the values never change.</summary>
    public enum RunOutcome : byte
    {
        /// <summary>Under the run-time limit, or with no limit and not a spike.</summary>
        Under = 0,

        /// <summary>Over the run-time limit and forgiven as a spike: no offence.</summary>
        Forgiven = 1,

        /// <summary>No run-time limit, and the run stood out as a spike: the combined run time counts it as the block's average.</summary>
        StoodOut = 2,

        /// <summary>Over the run-time limit and not forgiven, but not checked: in the grace after an offence, with a punishment pending, above the sim-speed threshold, with the limiter off, or with the block instant-killed (by memory, or for an earlier run in the same tick).</summary>
        NotChecked = 3,

        /// <summary>Over the run-time limit and counted: it made an offence.</summary>
        Counted = 4,

        /// <summary>The run-time instant kill, whatever the run-time limit.</summary>
        InstantKilled = 5
    }

    public class BlockHistoryRequest
    {
        /// <summary>The blocks wanted. The server answers for the first <see cref="BlockHistoryResult.MaxBlocks"/> of them.</summary>
        public List<long> EntityIds { get; set; } = new();

        /// <summary>
        /// Each block's owner as the report gave it, at the same index, so the server looks the block up there instead of
        /// under every player. A block that changed owner since is still found, by a search of the other players.
        /// </summary>
        public List<long> OwnerIds { get; set; } = new();
    }

    public class BlockHistoryResult
    {
        /// <summary>The most blocks one answer carries: a page of the Reports table.</summary>
        public const int MaxBlocks = 100;

        /// <summary>The tracked blocks among those asked for. One no longer tracked is left out.</summary>
        public List<BlockHistory> Blocks { get; set; } = new();
    }

    /// <summary>A block's last runs, and the limits they are held to.</summary>
    public class BlockHistory
    {
        public long EntityId { get; set; }

        /// <summary>Up to the last 50 run times, oldest first.</summary>
        public double[] RunTimesMS { get; set; } = Array.Empty<double>();

        /// <summary>
        /// Which of <see cref="RunTimesMS"/> the average leaves out: its 2 slowest once there are 10, and below that the 1 or
        /// 2 runs that were spikes, as decided when each was added.
        /// </summary>
        public int[] LeftOutOfAverage { get; set; } = Array.Empty<int>();

        /// <summary>What became of each of <see cref="RunTimesMS"/>, at the same index.</summary>
        public RunOutcome[] Outcomes { get; set; } = Array.Empty<RunOutcome>();

        public double AverageMS { get; set; }

        /// <summary>The owner's run-time limit, privileged or not; 0 when there is none.</summary>
        public double RunTimeLimitMS { get; set; }

        /// <summary>The owner's average limit, privileged or not; 0 when there is none.</summary>
        public double AverageLimitMS { get; set; }

        /// <summary>The instant kill run time; 0 when it is off.</summary>
        public double InstantKillMS { get; set; }

        /// <summary>How many runs over <see cref="InstantKillMS"/> within <see cref="InstantKillStrikeWindow"/> seconds kill.</summary>
        public int InstantKillStrikes { get; set; }

        public int InstantKillStrikeWindow { get; set; }
    }

    public class SnapshotRequest
    {
        /// <inheritdoc cref="ProfileApplyRequest.TimeoutMs"/>
        public int TimeoutMs { get; set; }

        /// <inheritdoc cref="ProfileApplyRequest.ExpiresUnixMs"/>
        public long ExpiresUnixMs { get; set; }
    }

    public class TurnOffBlockRequest
    {
        public long EntityId { get; set; }

        /// <inheritdoc cref="ProfileApplyRequest.TimeoutMs"/>
        public int TimeoutMs { get; set; }

        /// <inheritdoc cref="ProfileApplyRequest.ExpiresUnixMs"/>
        public long ExpiresUnixMs { get; set; }
    }

    public class TurnOffPlayerRequest
    {
        public long IdentityId { get; set; }

        /// <inheritdoc cref="ProfileApplyRequest.TimeoutMs"/>
        public int TimeoutMs { get; set; }

        /// <inheritdoc cref="ProfileApplyRequest.ExpiresUnixMs"/>
        public long ExpiresUnixMs { get; set; }
    }

    public class TurnOffResult
    {
        public int TurnedOff { get; set; }
    }

    /// <summary>Every limiter setting of a server, its privileged players included.</summary>
    public class ProfileData
    {
        /// <summary>Opaque to Quasar, so a profile keeps working when a newer plugin adds settings.</summary>
        public string SettingsJson { get; set; } = "";

        public int Settings { get; set; }
        public int PrivilegedPlayers { get; set; }

        /// <summary>
        /// Set by a server that will never apply this profile, with the reason, as a node of a managed cluster: Quasar then
        /// stops sending it. Null when it was applied. Older servers never set it.
        /// </summary>
        public string? Refused { get; set; }
    }

    public class ProfileApplyRequest
    {
        public string SettingsJson { get; set; } = "";

        /// <summary>
        /// Quasar's queue entry, the same each time it sends that entry again; empty from a Quasar before 1.3.3. The answer
        /// can arrive after Quasar stopped waiting, so it sends the entry again: the companion answers a repeat of the last
        /// request it applied with the answer it gave, without applying it twice. Older companions ignore it.
        /// </summary>
        public string RequestId { get; set; } = "";

        /// <summary>
        /// 0 for no limit. The companion does not start the work once this long has passed since it received the request.
        /// Quasar sends less than it waits, so the refusal still reaches it. Older companions ignore it.
        /// </summary>
        public int TimeoutMs { get; set; }

        /// <summary>
        /// 0 for none. When Quasar stops waiting, less a second for the answer's way back, as Unix milliseconds on Quasar's
        /// clock. A request can wait on the agent behind another request for longer than <see cref="TimeoutMs"/> allows for,
        /// so the companion refuses one that arrives after this, and never lets one wait past it, as far as the two clocks
        /// agree: a server clock ahead shortens the wait, and one behind lengthens it, so a request delayed on the way can
        /// then still start after Quasar stopped waiting, by at most that lag less the second kept for the answer. Quasar's agent
        /// also trusts the two clocks to agree within seconds, for cluster chat. Older companions ignore it.
        /// </summary>
        public long ExpiresUnixMs { get; set; }
    }

    public class ReportSnapshot
    {
        /// <summary>False when the Advanced PB Limiter Magnetar plugin is not loaded on the server.</summary>
        public bool Installed { get; set; }

        /// <summary>False until the world has loaded.</summary>
        public bool Online { get; set; }

        /// <summary>
        /// Why the limiter is inactive on this server until it restarts, or null: a node of a Quasar cluster, where it never
        /// runs, or a game update it does not match, until the plugin is updated. Older servers never set it.
        /// </summary>
        public string? Inactive { get; set; }

        /// <summary>The limiter's own Enabled setting. Nothing new is tracked or punished while it is off.</summary>
        public bool Enabled { get; set; }

        public string Version { get; set; } = "";

        /// <summary>Set when the server has more blocks than a report carries. Player totals still count all of them.</summary>
        public bool Truncated { get; set; }

        public List<PlayerRow> Players { get; set; } = new();
        public List<BlockRow> Blocks { get; set; } = new();
    }

    public class PlayerRow
    {
        public long IdentityId { get; set; }
        public ulong SteamId { get; set; }
        public string Name { get; set; } = "";
        public bool IsNpc { get; set; }
        public bool IsPrivileged { get; set; }
        public int BlockCount { get; set; }
        public double CombinedLastRunTimeMS { get; set; }
        public double CombinedRunTimeMSAvg { get; set; }
        public int Offences { get; set; }
        public int Recompiles { get; set; }

        /// <summary>The memory of the player's blocks that could be measured.</summary>
        public long MemoryBytes { get; set; }

        /// <summary>At least one of the player's blocks could not be measured, see <see cref="BlockRow.MemoryUnmeasured"/>.</summary>
        public bool MemoryUnmeasured { get; set; }
    }

    public class BlockRow
    {
        public long EntityId { get; set; }
        public long OwnerIdentityId { get; set; }
        public string OwnerName { get; set; } = "";
        public string GridName { get; set; } = "";
        public string BlockName { get; set; } = "";
        public bool Enabled { get; set; }
        public double LastRunTimeMS { get; set; }
        public double RunTimeMSAvg { get; set; }
        public double PeekRunTimeMS { get; set; }
        public int Offences { get; set; }
        public int Recompiles { get; set; }
        public long MemoryBytes { get; set; }

        /// <summary>The memory check could not measure the block's script, so it counts as over the limit and MemoryBytes is not a size.</summary>
        public bool MemoryUnmeasured { get; set; }

        /// <summary>Resting between rare runs, not run lately: left out of the combined run times. False from servers before 1.3.3.</summary>
        public bool Resting { get; set; }
    }
}
