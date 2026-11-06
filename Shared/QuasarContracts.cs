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

        /// <summary><see cref="EmptyPayload"/> in, <see cref="ReportSnapshot"/> out.</summary>
        public const string Snapshot = "report.snapshot";

        /// <summary><see cref="TurnOffBlockRequest"/> in, <see cref="TurnOffResult"/> out.</summary>
        public const string TurnOffBlock = "block.turnoff";

        /// <summary><see cref="TurnOffPlayerRequest"/> in, <see cref="TurnOffResult"/> out.</summary>
        public const string TurnOffPlayer = "player.turnoff";

        /// <summary><see cref="EmptyPayload"/> in, <see cref="ProfileData"/> out.</summary>
        public const string ProfileRead = "profile.read";

        /// <summary><see cref="ProfileApplyRequest"/> in, <see cref="ProfileData"/> out, describing what was applied.</summary>
        public const string ProfileApply = "profile.apply";
    }

    public class EmptyPayload
    {
    }

    public class TurnOffBlockRequest
    {
        public long EntityId { get; set; }
    }

    public class TurnOffPlayerRequest
    {
        public long IdentityId { get; set; }
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
    }

    public class ProfileApplyRequest
    {
        public string SettingsJson { get; set; } = "";
    }

    public class ReportSnapshot
    {
        /// <summary>False when the Advanced PB Limiter Magnetar plugin is not loaded on the server.</summary>
        public bool Installed { get; set; }

        /// <summary>False until the world has loaded.</summary>
        public bool Online { get; set; }

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
        public long MemoryBytes { get; set; }
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
    }
}
