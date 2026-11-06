using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Advanced_PB_Limiter.Manager;
using ExtensionMethods;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>A player and their programmable blocks. Updated on the game thread, the memory check and reports read it from others.</summary>
    public sealed class TrackedPlayer
    {
        private static IPbLimiterConfig Config => LimiterCore.Config;
        private static readonly LimiterLog Log = new("Tracked Player");

        private readonly ConcurrentDictionary<long, TrackedPBBlock> PBBlocks = new();

        public string? PlayerName { get; }
        public long PlayerId { get; }
        public ulong SteamId { get; }
        public bool IsNpc { get; }
        public long LastDataUpdateTick { get; private set; }
        public List<long> CombinedOffences { get; } = new();

        /// <summary>Looked up once in a while rather than on every run, see <see cref="RefreshPrivilegedPlayer"/>.</summary>
        public PrivilegedPlayer? Privileged { get; private set; }

        public TrackedPlayer(long playerId)
        {
            PlayerId = playerId;
            PlayerName = MySession.Static.Players.TryGetIdentity(playerId)?.DisplayName;
            SteamId = MySession.Static.Players.TryGetSteamId(playerId);
            IsNpc = MySession.Static.Players.IdentityIsNpc(playerId);
            LastDataUpdateTick = Stopwatch.GetTimestamp();
            RefreshPrivilegedPlayer();
        }

        public void RefreshPrivilegedPlayer()
        {
            Privileged = Config.TryGetPrivilegedPlayer(SteamId, out PrivilegedPlayer? privilegedPlayer) ? privilegedPlayer : null;
        }

        public int PBBlockCount => PBBlocks.Count;

        public TrackedPBBlock? GetTrackedPB(long entityId)
        {
            return PBBlocks.TryGetValue(entityId, out TrackedPBBlock? trackedPBBlock) ? trackedPBBlock : null;
        }

        public void UpdatePBBlockData(in TrackingDataUpdateRequest data)
        {
            long now = Stopwatch.GetTimestamp();
            LastDataUpdateTick = now;

            long entityId = data.ProgBlock.EntityId;
            if (!PBBlocks.TryGetValue(entityId, out TrackedPBBlock? trackedPBBlock))
            {
                PBBlocks.TryAdd(entityId, new TrackedPBBlock(data.ProgBlock.CubeGrid.DisplayName, data.ProgBlock));
                return;
            }

            trackedPBBlock.LastUpdateTick = now;
            if (trackedPBBlock.IsUnderGracePeriod(Privileged)) return;

            trackedPBBlock.AddRuntimeData(data.RunTime);
            PunishmentManager.CheckForPunishment(this, trackedPBBlock, data.RunTime);
        }

        public List<TrackedPBBlock> GetAllPBBlocks => new(PBBlocks.Values);

        public void GetCombinedRuntimes(out double lastRunTimeMS, out double runTimeMSAvg)
        {
            lastRunTimeMS = 0;
            runTimeMSAvg = 0;
            foreach (KeyValuePair<long, TrackedPBBlock> kvp in PBBlocks)
            {
                lastRunTimeMS += kvp.Value.LastRunTimeMS;
                runTimeMSAvg += kvp.Value.RunTimeMSAvg;
            }
        }

        public void CleanUpOldPBBlocks()
        {
            foreach (KeyValuePair<long, TrackedPBBlock> kvp in PBBlocks)
            {
                MyProgrammableBlock block = kvp.Value.ProgrammableBlock;
                if (block.Closed || block.MarkedForClose || kvp.Value.LastUpdateTick.TicksTillNow_TimeSpan().TotalSeconds > Config.RemoveInactivePBsAfterSeconds)
                    PBBlocks.TryRemove(kvp.Key, out _);
            }
        }

        public void ResetPBBlockData(MyProgrammableBlock pbBlock)
        {
            // Not finding it is normal: the "Check Code" button in the pb interface compiles in a block that isn't tracked.
            if (!PBBlocks.TryGetValue(pbBlock.EntityId, out TrackedPBBlock? trackedPBBlock)) return;

            trackedPBBlock.ClearRunTimes();
            trackedPBBlock.ResetMemoryUsage();
            if (Config.DebugReporting)
                Log.Info($"Cleared the history of [{pbBlock.CustomName}] after a recompile.");
        }
    }
}
