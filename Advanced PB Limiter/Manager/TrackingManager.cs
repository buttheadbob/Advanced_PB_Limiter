using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using VRage.Game.VisualScripting.Utils;
using VRage.ModAPI;
using Timer = System.Timers.Timer;

namespace Advanced_PB_Limiter.Manager
{
    public static class TrackingManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();
        private static readonly Timer _cleanupTimer = new ();
        private static readonly int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        private static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter => Tracking Manager");
        private static readonly ConcurrentDictionary<long, ProgramInfo> ProgrammablePrograms = new();
        private static readonly Timer _memoryCheckTimer = new (5000);
        private static readonly Timer _checkCombinedLimits = new (10000);
        private static bool _checkCombinedLimitsRunning;
        private static ConcurrentQueue<TrackingDataUpdateRequest> _trackingDataUpdateQueue = new();
        private static readonly Timer _processUpdateRequests = new(16);
        private static bool _processingRunning = false;
        private static bool _memoryCheckRunning = false;
        private static Dictionary<long, int> _pbDeferTracker = new();

        public static void Start()
        {
            _memoryCheckTimer.Elapsed += (sender, args) => CheckInstanceMemoryUsages();
            _memoryCheckTimer.Start();
            
            _checkCombinedLimits.Elapsed += (sender, args) => CheckAllUserBlocksForCombinedLimits();
            _checkCombinedLimits.Start();

            _processUpdateRequests.Elapsed += (sender, args) => ProcessUpdateTrackingData();
            _processUpdateRequests.Start();
            
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPlayers();
            if (Config.RemovePlayersWithNoPBFrequencyInMinutes <= 0) return;
            _cleanupTimer.Interval = TimeSpan.FromMinutes(1).TotalMilliseconds;
            _cleanupTimer.Start();
        }

        public static void Stop()
        {
            _memoryCheckTimer.Stop();
            _checkCombinedLimits.Stop();
            _processUpdateRequests.Stop();
            _cleanupTimer.Stop();
            PlayersTracked.Clear();
            ProgrammablePrograms.Clear();
            while (_trackingDataUpdateQueue.TryDequeue(out _)) { }
        }
        
        public static List<TrackedPlayer> GetTrackedPlayerData()
        {
            return PlayersTracked.Values.ToList();
        }
        
        public static TrackedPlayer? GetTrackedPlayerDataById(long playerId)
        {
            return PlayersTracked.TryGetValue(playerId, out TrackedPlayer? player) 
                ? player 
                : null;
        }

        private static void CleanUpOldPlayers()
        {
            if (!Config.Enabled) return;
            // Get players
            long[] trackedPlayers = PlayersTracked.Keys.ToArray();
            if (trackedPlayers.Length <= 0) return;
            
            for (int index = trackedPlayers.Length - 1; index >= 0; index--)
            {
                if (PlayersTracked[trackedPlayers[index]].PBBlockCount > 0) continue;
                if ((Stopwatch.GetTimestamp() - PlayersTracked[trackedPlayers[index]].LastDataUpdateTick) / Stopwatch.Frequency / 60 < Config.RemovePlayersWithNoPBFrequencyInMinutes) continue;
                RemovePlayer(PlayersTracked[trackedPlayers[index]].PlayerId);
            }
        }

        public static void UpdateTrackingData(MyProgrammableBlock? pb, double runTime)
        {
            _trackingDataUpdateQueue.Enqueue(new TrackingDataUpdateRequest{ProgBlock = pb, RunTime = runTime});
        }
        
        private static void ProcessUpdateTrackingData()
        {
            if (!Config.Enabled) return;
            if (_processingRunning) return;
            if (_trackingDataUpdateQueue.IsEmpty) return;
            
            _processingRunning = true;

            try
            {
                while (_trackingDataUpdateQueue.TryDequeue(out TrackingDataUpdateRequest data))
                {
                    long _trackTime = Stopwatch.GetTimestamp();
                
                    if (data.ProgBlock == null) continue;
            
                    if (data.ProgBlock.OwnerId == 0)
                    {
                        if (Config.TurnOffUnownedBlocks)
                        {
                            MySandboxGame.Static.Invoke(()=>
                            {
                                data.ProgBlock.Enabled = false;
                            }, "Advanced PB Limiter");
                            data.ProgBlock.Enabled = false;
                        }
                        return;
                    } // Track un-owned blocks? something to think about...
        
                    if (Config.IgnoreNPCs)
                        if (MySession.Static.Players.IdentityIsNpc(data.ProgBlock.OwnerId)) return;

                    if (data.RunTime < 0)
                        data.RunTime = 0;
            
                    if (!PlayersTracked.TryGetValue(data.ProgBlock.OwnerId, out TrackedPlayer? player) || player == null)
                    {
                        player = new TrackedPlayer(data.ProgBlock.OwnerId);
                        PlayersTracked.TryAdd(data.ProgBlock.OwnerId, player);
                    }
            
                    player.UpdatePBBlockData(data);

                    if (Config.ShowTimingsBurp)
                        Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Processed PB Tracking Data[{data.ProgBlock.CustomName}] in: {_trackTime.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
                }
            }
            finally
            {
                _processingRunning = false;
            }
        }
        
        private static void RemovePlayer(long Id)
        {
            for (int index = PlayersTracked.Count - 1; index >= 0; index--)
            {
                if (PlayersTracked[index].PlayerId != Id) continue;
                PlayersTracked.Remove(Id);
                break;
            }
        }
        
        public static void PBRecompiled(MyProgrammableBlock? pb)
        {
            if (!Config.Enabled) return;
            if (pb is null) return;

            if (!PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player)) return;
            
            if (Config.ClearHistoryOnRecompile)
                player.ResetPBBlockData(pb);
        }
         
        private static void CheckAllUserBlocksForCombinedLimits()
        {
            if (!Config.Enabled) return;
            
            if (!Config.CheckAllUserBlocksCombined) return;
            
            if (_checkCombinedLimitsRunning) return;

            try
            {
                _checkCombinedLimitsRunning = true;
                
                foreach(KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
                {
                   long _trackStart = Stopwatch.GetTimestamp();
                    
                    // clear old combined warnings
                    for (int index = trackedPlayer.Value.CombinedOffences.Count - 1; index >= 0; index--)
                    {
                        if (trackedPlayer.Value.CombinedOffences[index].TicksTillNow_TimeSpan().TotalMinutes >= Config.OffenseDurationBeforeDeletion)
                        {
                            trackedPlayer.Value.CombinedOffences.Remove(trackedPlayer.Value.CombinedOffences[index]);
                        }
                    }

                    if (Config.PrivilegedPlayers.TryGetValue(trackedPlayer.Value.SteamId, out PrivilegedPlayer? privilegedPlayer))
                        if (!privilegedPlayer.NoCombinedLimits) continue;

                    double totalMS = 0;
                    double totalMSAvg = 0;
                    double CombinedRuntimeAllowance = privilegedPlayer?.CombinedRuntimeAllowance ?? Config.MaxAllBlocksCombinedRunTimeMS;
                    double CombinedRuntimeAverageAllowance = privilegedPlayer?.CombinedRuntimeAverageAllowance ?? Config.MaxAllBlocksCombinedRunTimeMSAvg;
                    
                    Span<double> AllRuntimes = stackalloc double[trackedPlayer.Value.PBBlockCount];
                    trackedPlayer.Value.GetAllPBBlocksLastRunTimeMS(AllRuntimes);
                    for (int index = AllRuntimes.Length - 1; index >= 0; index--)
                    {
                        totalMS += AllRuntimes[index];
                    }
                    
                    Span<double> AllRuntimesAverage = stackalloc double[trackedPlayer.Value.PBBlockCount];
                    trackedPlayer.Value.GetAllPBBlocksMSAvg(AllRuntimesAverage);
                    for (int index = AllRuntimesAverage.Length - 1; index >= 0; index--)
                    {
                        totalMSAvg += AllRuntimesAverage[index];
                    }
                    
                    if (totalMS > CombinedRuntimeAllowance)
                    {
                        trackedPlayer.Value.CombinedOffences.Add(Stopwatch.GetTimestamp());
                        
                        if (trackedPlayer.Value.CombinedOffences.Count <= Config.MaxCombinedOffencesBeforePunishment)
                        {
                            foreach (TrackedPBBlock? pbblocks in trackedPlayer.Value.GetAllPBBlocks)
                            {
                                MySandboxGame.Static.Invoke(() =>
                                {
                                    pbblocks.ProgrammableBlock?.Run("GracefulShutDown::-3", UpdateType.Script);
                                }, "Advanced_PB_Limiter");
                            }
                            continue;
                        }
                        
                        if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
                        {
                            foreach (TrackedPBBlock pbBlock in trackedPlayer.Value.GetAllPBBlocks)
                                PunishmentManager.PunishPB(trackedPlayer.Value, pbBlock, PunishmentManager.PunishReason.CombinedRuntimeOverLimit);
                        }
                        else
                        {
                            Random random = new(DateTime.Now.Millisecond);
                            PunishmentManager.PunishPB(trackedPlayer.Value, trackedPlayer.Value.GetAllPBBlocks[random.Next(0,trackedPlayer.Value.GetAllPBBlocks.Count()-1)], PunishmentManager.PunishReason.CombinedRuntimeOverLimit);
                        }
                    }
                    
                    if (totalMSAvg > CombinedRuntimeAverageAllowance)
                    {
                        trackedPlayer.Value.CombinedOffences.Add(Stopwatch.GetTimestamp());

                        if (trackedPlayer.Value.CombinedOffences.Count <= Config.MaxCombinedOffencesBeforePunishment)
                        {
                            foreach (TrackedPBBlock? pbblocks in trackedPlayer.Value.GetAllPBBlocks)
                            {
                                MySandboxGame.Static.Invoke(() =>
                                {
                                    pbblocks.ProgrammableBlock?.Run("GracefulShutDown::-3", UpdateType.Script);
                                }, "Advanced_PB_Limiter");
                            }
                            continue;
                        }
                        
                        if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
                        {
                            foreach (TrackedPBBlock pbBlock in trackedPlayer.Value.GetAllPBBlocks)
                                PunishmentManager.PunishPB(trackedPlayer.Value, pbBlock, PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit);
                        }
                        else
                        {
                            Random random = new(DateTime.Now.Millisecond);
                            PunishmentManager.PunishPB(trackedPlayer.Value, trackedPlayer.Value.GetAllPBBlocks[random.Next(0,trackedPlayer.Value.GetAllPBBlocks.Count()-1)], PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit);
                        }
                    }
                    
                    if (Config.ShowTimingsBurp)
                        Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Checked User Combined Usage[{trackedPlayer.Value.PlayerName}] in: {_trackStart.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
                }
            }
            finally
            {
                _checkCombinedLimitsRunning = false;
            }
        }

        public static bool IsPBInstanceReferenceValid(long pbEntityID)
        {
            if (!ProgrammablePrograms.TryGetValue(pbEntityID, out ProgramInfo? programmableProgram)) return false;
            if (programmableProgram == null) return false;
            if (!programmableProgram.ProgramReference.TryGetTarget(out IMyGridProgram? program)) return false;
            return program != null;
        }

        public static bool IsMemoryCheckInProgress(long entityID)
        {
            if (!ProgrammablePrograms.TryGetValue(entityID, out ProgramInfo? programmableProgram)) return false;

            return Volatile.Read(ref programmableProgram.IsChecking) == 1;
        }

        public static void AddPBInstanceReference(long entityID, ProgramInfo programInfo)
        {
            ProgrammablePrograms.AddOrUpdate(entityID, programInfo, (l, info) => info);
        }

        public static void RemovePBInstanceReference(long entityId)
        {
            ProgrammablePrograms.TryRemove(entityId, out _);
        }

        private static void CheckInstanceMemoryUsages()
        {
            if (!Config.EnableMemoryMonitoring) return;
            if (_memoryCheckRunning) return;
            _memoryCheckRunning = true;

            try
            {
                foreach (KeyValuePair<long,ProgramInfo> programInfo in ProgrammablePrograms)
                {
                    if (Interlocked.CompareExchange(ref programInfo.Value.IsChecking, 1, 0) != 0)
                        continue;

                    try
                    {
                        if (!programInfo.Value.ProgramReference.TryGetTarget(out IMyGridProgram? program))
                        {
                            RemovePBInstanceReference(programInfo.Key);
                            continue;
                        }

                        if (program == null)
                        {
                            RemovePBInstanceReference(programInfo.Key);
                            continue;
                        }
                    
                        GetSize.OfAssembly(program, out long size, out long time);
                        if (Config.ShowTimingsBurp)
                            Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Programmable Block=>[{programInfo.Value.PB.CustomName}] Size=>[{size} bytes] ScanTime=>[{TimeSpan.FromTicks(time).TotalMilliseconds}ms]");
                    
                        programInfo.Value.LastUpdate = DateTime.Now;

                        PlayersTracked.TryGetValue(programInfo.Value.OwnerID, out TrackedPlayer? trackedPlayer);
                        TrackedPBBlock? block = null;
                    
                        if (trackedPlayer == null)
                        {
                            foreach (TrackedPlayer player in PlayersTracked.Values)
                            {
                                block = player.GetTrackedPB(programInfo.Value.PB.EntityId);
                                if (block != null)
                                    break;
                            }
                        }
                        else
                        {
                            block = trackedPlayer.GetTrackedPB(programInfo.Key);
                        }
                    
                        if (block == null) continue;
                    
                        block.updateMemoryUsage(size);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref programInfo.Value.IsChecking, 0);
                    }
                }
            }
            finally
            {
                _memoryCheckRunning = false;
            }
        }
    }
}
     