using System.Collections.Generic;
using System.Globalization;
using Advanced_PB_Limiter.Utils;
using PluginSdk.Config;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>
/// One row of the privileged players list in Quasar. The Steam ID is text because Quasar's number boxes hold a double,
/// which rounds a 17 digit Steam ID to a different player. A struct has no defaults of its own in Quasar, so a
/// new row starts at 0, and 0 in the fields that say so means the server's setting.
/// </summary>
public struct PrivilegedPlayerEntry
{
    [StructMember("Player name, only to tell the rows apart"), StructCaption]
    public string Name { get; set; }

    [StructMember("Steam ID, the 17 digit number")]
    public string SteamId { get; set; }

    [StructMember("Max run time of one programmable block in ms, 0 for no limit")]
    public double RuntimeAllowance { get; set; }

    [StructMember("Max average run time of one programmable block in ms, 0 for no limit")]
    public double RuntimeAverageAllowance { get; set; }

    [StructMember("Seconds to ignore a block's run time when it first starts")]
    public double StartupAllowance { get; set; }

    [StructMember("Max run time of all this player's blocks combined in ms, 0 for no limit")]
    public double CombinedRuntimeAllowance { get; set; }

    [StructMember("Max average run time of all this player's blocks combined in ms, 0 for no limit")]
    public double CombinedRuntimeAverageAllowance { get; set; }

    [StructMember("Do not apply the combined limits to this player")]
    public bool NoCombinedLimits { get; set; }

    [StructMember("How this player's blocks are punished")]
    public Enums.Punishment Punishment { get; set; }

    [StructMember("Seconds the script gets to shut down before it is punished, 0 uses the server's setting")]
    public int GracefulShutDownRequestDelay { get; set; }

    [StructMember("Offences allowed before a block is punished, 0 uses the server's setting")]
    public int OffencesBeforePunishment { get; set; }

    [StructMember("Damage dealt when the punishment is Damage, 0 uses the server's setting")]
    public int DamageAmount { get; set; }

    public static PrivilegedPlayerEntry From(PrivilegedPlayer player) => new()
    {
        Name = player.Name,
        SteamId = player.SteamId.ToString(CultureInfo.InvariantCulture),
        RuntimeAllowance = player.RuntimeAllowance,
        RuntimeAverageAllowance = player.RuntimeAverageAllowance,
        StartupAllowance = player.StartupAllowance,
        CombinedRuntimeAllowance = player.CombinedRuntimeAllowance,
        CombinedRuntimeAverageAllowance = player.CombinedRuntimeAverageAllowance,
        NoCombinedLimits = player.NoCombinedLimits,
        Punishment = player.Punishment,
        GracefulShutDownRequestDelay = player.GracefulShutDownRequestDelay,
        OffencesBeforePunishment = player.OffencesBeforePunishment,
        DamageAmount = player.DamageAmount
    };

    public readonly bool TryGetSteamId(out ulong steamId)
    {
        return ulong.TryParse(SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out steamId) && steamId != 0;
    }

    public readonly PrivilegedPlayer ToPrivilegedPlayer(ulong steamId, LimiterConfig server) => new()
    {
        Name = Name ?? string.Empty,
        SteamId = steamId,
        RuntimeAllowance = RuntimeAllowance,
        RuntimeAverageAllowance = RuntimeAverageAllowance,
        StartupAllowance = StartupAllowance,
        CombinedRuntimeAllowance = CombinedRuntimeAllowance,
        CombinedRuntimeAverageAllowance = CombinedRuntimeAverageAllowance,
        NoCombinedLimits = NoCombinedLimits,
        Punishment = Punishment,
        GracefulShutDownRequestDelay = GracefulShutDownRequestDelay != 0 ? GracefulShutDownRequestDelay : server.GracefulShutDownRequestDelay,
        OffencesBeforePunishment = OffencesBeforePunishment != 0 ? OffencesBeforePunishment : server.MaxOffencesBeforePunishment,
        DamageAmount = DamageAmount != 0 ? DamageAmount : server.PunishDamageAmount
    };
}

/// <summary>Magnetar config. Quasar renders the editor from these attributes, there is no UI code.</summary>
[Tab("limits", caption: "Limits")]
[Tab("punishment", caption: "Punishment")]
[Tab("privileged", caption: "Privileged players")]
[Tab("defer", caption: "Deferred runs")]
[Tab("advanced", caption: "Advanced")]
[Section("single", parent: "limits", caption: "Per programmable block")]
[Section("combined", parent: "limits", caption: "All of a player's programmable blocks combined")]
[Section("enforcement", parent: "limits", caption: "Enforcement")]
[Section("restart", parent: "advanced", caption: "Turned off blocks and restarts")]
[Section("memory", parent: "advanced", caption: "Memory")]
[Section("housekeeping", parent: "advanced", caption: "Housekeeping")]
public class LimiterConfig : PluginConfig, IPbLimiterConfig
{
    // Replaced whole, never edited, because the punishment and tracking threads read it without a lock.
    private Dictionary<ulong, PrivilegedPlayer> _privilegedLookup = new();

    [BoolOption("Enable or disable the plugin", Parent = "single")]
    public bool Enabled { get; set => SetField(ref field, value); } = true;

    [DoubleOption(0, 1000, "Max time in ms a script is allowed to use before being issued any punishment, 0 for no limit", Parent = "single")]
    public double MaxRunTimeMS { get; set => SetField(ref field, value); } = 0.8;

    [DoubleOption(0, 1000, "Max time in ms a script is allowed to average before being issued any punishment, 0 for no limit", Parent = "single")]
    public double MaxRunTimeMSAvg { get; set => SetField(ref field, value); } = 0.4;

    [DoubleOption(0, 100000, "Any block that reaches this run time in ms is damaged beyond function immediately, regardless of offences and with no graceful shutdown request. 0 to disable", Parent = "single")]
    public double InstantKillThreshold { get; set => SetField(ref field, value); } = 10.0;

    [IntOption(0, 3600, "Seconds to ignore a block's run time when it first starts", Parent = "single")]
    public int StartupAllowance { get; set => SetField(ref field, value); } = 60;

    [BoolOption("Track the total time of all running blocks owned by a player", Parent = "combined")]
    public bool CheckAllUserBlocksCombined { get; set => SetField(ref field, value); } = true;

    [DoubleOption(0, 100000, "Max time in ms of all of a player's blocks before punishing, 0 for no limit", Parent = "combined")]
    public double MaxAllBlocksCombinedRunTimeMS { get; set => SetField(ref field, value); } = 3.0;

    [DoubleOption(0, 100000, "Max time in ms all of a player's blocks are allowed to average before punishing, 0 for no limit", Parent = "combined")]
    public double MaxAllBlocksCombinedRunTimeMSAvg { get; set => SetField(ref field, value); } = 2.0;

    [IntOption(0, 1000, "Amount of combined offences by the player before punishment is applied", Parent = "combined")]
    public int MaxCombinedOffencesBeforePunishment { get; set => SetField(ref field, value); } = 3;

    [BoolOption("Apply the punishment to all the player's active blocks. Off punishes a random one", Parent = "combined")]
    public bool PunishAllUserBlocksCombinedOnExcessLimits { get; set => SetField(ref field, value); } = true;

    [BoolOption("Adjust run times for sim speed: when sim speed tanks, a script can take longer to run at no fault of the player", Parent = "enforcement")]
    public bool UseSimTime { get; set => SetField(ref field, value); } = true;

    [BoolOption("If sim speed is fine, is there an issue? Only enforce the limits below the sim speed set here", Parent = "enforcement")]
    public bool EnforceOnlyBelowSimThreshold { get; set => SetField(ref field, value); }

    [DoubleOption(0, 1, "How low sim speed can go before any rules are enforced, from 0.0 to 1.0", Parent = "enforcement")]
    public double EnforceOnlyBelowSimRate { get; set => SetField(ref field, value); } = 0.7;

    [BoolOption("Do not track programmable blocks owned by NPCs", Parent = "enforcement")]
    public bool IgnoreNPCs { get; set => SetField(ref field, value); } = true;

    [BoolOption("Blocks with no owner can still run... but should they? On turns them off", Parent = "enforcement")]
    public bool TurnOffUnownedBlocks { get; set => SetField(ref field, value); } = true;

    [EnumOption("TurnOff disables the block. Damage applies the set damage points, which may not happen in safe zones. Destroy damages it to 100 integrity, below functional level, and a block already at or below that is left as it is", Parent = "punishment")]
    public Enums.Punishment Punishment { get; set => SetField(ref field, value); } = Enums.Punishment.TurnOff;

    [IntOption(0, 1000, "Amount of offences before punishment is applied", Parent = "punishment")]
    public int MaxOffencesBeforePunishment { get; set => SetField(ref field, value); } = 3;

    [IntOption(0, 3600, "Seconds after an offence before another can be incurred. This prevents Update1 and Update10 from reaching the offence limit within 1 second of the first offence", Parent = "punishment")]
    public int GraceAfterOffence { get; set => SetField(ref field, value); } = 15;

    [IntOption(1, 10080, "Minutes before an offence is removed", Parent = "punishment")]
    public int OffenseDurationBeforeDeletion { get; set => SetField(ref field, value); } = 1;

    [IntOption(0, 3600, "Seconds to wait for the script to shut down before the punishment is applied, which always happens after the delay. 0 for no delay", Parent = "punishment")]
    public int GracefulShutDownRequestDelay { get; set => SetField(ref field, value); } = 10;

    [IntOption(0, 100000, "Points of damage to apply when the punishment is Damage", Parent = "punishment")]
    public int PunishDamageAmount { get; set => SetField(ref field, value); } = 100;

    [BoolOption("Blocks in a safe zone with damage disabled take no damage. On makes the Damage punishment remove all of such a block's building materials and reset it to unwelded. The Destroy punishment and instant kills always do this", Parent = "punishment")]
    public bool KillProtectedBlock { get; set => SetField(ref field, value); }

    [BoolOption("Send a warning to the owner when their block incurs an offence", Parent = "punishment")]
    public bool WarnUserOnOffense { get; set => SetField(ref field, value); } = true;

    [StructOption("Players with their own limits", Parent = "privileged")]
    public List<PrivilegedPlayerEntry> PrivilegedPlayers { get; set => SetField(ref field, value); } = new();

    [BoolOption("Always run timed scripts only after the set number of requests", Parent = "defer")]
    public bool DeferRunAlways { get; set => SetField(ref field, value); }

    [BoolOption("Run timed scripts only after the set number of requests while sim speed is below the rate set here", Parent = "defer")]
    public bool DeferRunOnSimspeedRate { get; set => SetField(ref field, value); }

    [DoubleOption(0, 1, "Below this sim speed, from 0.0 to 1.0, blocks run less often if the option above is on", Parent = "defer")]
    public double DeferRunBelowSimRate { get; set => SetField(ref field, value); } = 0.7;

    [IntOption(0, 1000, "How many times a block should request to run before it is allowed", Parent = "defer")]
    public int DeferRunCount { get; set => SetField(ref field, value); }

    [BoolOption("Attaches Update1 to Update10, so it only runs when Update10 would. No script really needs to run 60 times per second, and none should need changing", Parent = "defer")]
    public bool DisableUpdate1 { get; set => SetField(ref field, value); }

    [BoolOption("Auto enable: players use Save() or the constructor to turn on their block automatically, which runs whether the block is enabled or not. Off prevents that", Parent = "restart")]
    public bool AllowSelfTurnOnExploit { get; set => SetField(ref field, value); }

    [BoolOption("Auto enable for NPCs: allows the Save() of an NPC's block to run. Useful for custom operations on the server", Parent = "restart")]
    public bool AllowNPCToAutoTurnOn { get; set => SetField(ref field, value); } = true;

    [BoolOption("No recompile on restart: blocks that are turned off are not compiled during a restart, so the player has to click Recompile instead of Run. Best to prevent timers from enabling them, but a button will not turn them on either", Parent = "restart")]
    public bool RequireRecompileOnRestart { get; set => SetField(ref field, value); }

    [BoolOption("Enable the limiting and monitoring of memory use", Parent = "memory")]
    public bool EnableMemoryMonitoring { get; set => SetField(ref field, value); } = true;

    [LongOption(0, 1073741824, "Max memory per block in bytes. Over this it is damaged beyond function immediately, like the run time limit that does the same. 0 to ignore", Parent = "memory")]
    public long PBMemoryThreshold { get; set => SetField(ref field, value); } = 10240000;

    [BoolOption("When a player recompiles their script, reset all its tracking data", Parent = "housekeeping")]
    public bool ClearHistoryOnRecompile { get; set => SetField(ref field, value); } = true;

    [IntOption(1, 86400, "Seconds to keep the data of an inactive block, only useful for showing data. Blocks removed from the game are removed from tracking regardless", Parent = "housekeeping")]
    public int RemoveInactivePBsAfterSeconds { get; set => SetField(ref field, value); } = 30;

    [IntOption(0, 10080, "Minutes to keep a player in reports after they have no blocks in game. 0 to never remove them", Parent = "housekeeping")]
    public int RemovePlayersWithNoPBFrequencyInMinutes { get; set => SetField(ref field, value); } = 60;

    [BoolOption("Allow staff to use the CreatePrivilegedUser command", Parent = "housekeeping")]
    public bool AllowStaffCommands { get; set => SetField(ref field, value); } = true;

    [BoolOption("Shows some extra stuff that will fill your log file", Parent = "housekeeping")]
    public bool DebugReporting { get; set => SetField(ref field, value); }

    /// <summary>Runtime only: the Timings command turns this on for a few seconds, so it has no option attribute and is never saved.</summary>
    public bool ShowTimingsBurp { get; set => SetField(ref field, value); }

    public bool TryGetPrivilegedPlayer(ulong steamId, out PrivilegedPlayer? player) => _privilegedLookup.TryGetValue(steamId, out player);

    public void SetPrivilegedPlayer(PrivilegedPlayer player)
    {
        PrivilegedPlayerEntry entry = PrivilegedPlayerEntry.From(player);
        int index = PrivilegedPlayers.FindIndex(e => e.SteamId?.Trim() == entry.SteamId);
        if (index >= 0)
            PrivilegedPlayers[index] = entry;
        else
            PrivilegedPlayers.Add(entry);

        // Rebuilds the lookup and saves, through the host's PropertyChanged handler.
        NotifyChanged(nameof(PrivilegedPlayers));
    }

    /// <summary>Returns the names of the rows left out because their Steam ID is not a number.</summary>
    public List<string> RebuildPrivilegedLookup()
    {
        Dictionary<ulong, PrivilegedPlayer> lookup = new(PrivilegedPlayers.Count);
        List<string> unusable = new();
        foreach (PrivilegedPlayerEntry entry in PrivilegedPlayers)
        {
            if (entry.TryGetSteamId(out ulong steamId))
                lookup[steamId] = entry.ToPrivilegedPlayer(steamId, this);
            else
                unusable.Add(entry.Name ?? "");
        }

        _privilegedLookup = lookup;
        return unusable;
    }
}
