﻿using System;
using System.Collections.Generic;
using System.Linq;
using Advanced_PB_Limiter.Utils;
using Torch;

namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig : ViewModel
    {
        private bool _Enabled = true;
        public bool Enabled { get => _Enabled; set => SetValue(ref _Enabled, value); }
        
        private bool _AllowStaffCommands = true;
        public bool AllowStaffCommands { get => _AllowStaffCommands; set => SetValue(ref _AllowStaffCommands, value); }
        
        private int _RemoveInactivePBsAfterSeconds = 30;
        public int RemoveInactivePBsAfterSeconds { get => _RemoveInactivePBsAfterSeconds; set => SetValue(ref _RemoveInactivePBsAfterSeconds, value); }
        
        private int _RemovePlayersWithNoPBFrequencyInMinutes = 60;
        public int RemovePlayersWithNoPBFrequencyInMinutes { get => _RemovePlayersWithNoPBFrequencyInMinutes; set => SetValue(ref _RemovePlayersWithNoPBFrequencyInMinutes, value); }
        
        private int _MaxRunsToTrack = 50;
        public int MaxRunsToTrack { get => _MaxRunsToTrack; set => SetValue(ref _MaxRunsToTrack, value); }
        
        private bool _ClearHistoryOnRecompile = true;
        public bool ClearHistoryOnRecompile { get => _ClearHistoryOnRecompile; set => SetValue(ref _ClearHistoryOnRecompile, value); }
        
        private int _GracefulShutDownRequestDelay = 10;
        public int GracefulShutDownRequestDelay { get => _GracefulShutDownRequestDelay; set => SetValue(ref _GracefulShutDownRequestDelay, value); }
        
        private MtObservableSortedSerializableDictionary<ulong, PrivilegedPlayer> _PrivilegedPlayers = new ();
        public MtObservableSortedSerializableDictionary<ulong, PrivilegedPlayer> PrivilegedPlayers { get => _PrivilegedPlayers; set => SetValue(ref _PrivilegedPlayers, value); }
        
        private double _InstantKillThreshold = 10.0; // threshold that there are no warnings or offence limits before instant kill
        public double InstantKillThreshold { get => _InstantKillThreshold; set => SetValue(ref _InstantKillThreshold, value); }
        
        private int _PunishDamageAmount = 100;
        public int PunishDamageAmount { get => _PunishDamageAmount; set => SetValue(ref _PunishDamageAmount, value); }
        
        private int _GraceAfterOffence = 15; // In Seconds, this prevents the player occuring an offence every tick or every 10 ticks and getting punished in 0.3 seconds from first offence.
        public int GraceAfterOffence { get => _GraceAfterOffence; set => SetValue(ref _GraceAfterOffence, value); }
        
        private int _MaxOffencesBeforePunishment = 3;
        public int MaxOffencesBeforePunishment { get => _MaxOffencesBeforePunishment; set => SetValue(ref _MaxOffencesBeforePunishment, value); }

        private int _OffenseDurationBeforeDeletion = 1; // In minutes
        public int OffenseDurationBeforeDeletion { get => _OffenseDurationBeforeDeletion; set => SetValue(ref _OffenseDurationBeforeDeletion, value); }
        
        private double _MaxRunTimeMS = 0.8;
        public double MaxRunTimeMS { get => _MaxRunTimeMS; set => SetValue(ref _MaxRunTimeMS, value); }
        
        private double _MaxRunTimeMSAvg = 0.4;
        public double MaxRunTimeMSAvg { get => _MaxRunTimeMSAvg; set => SetValue(ref _MaxRunTimeMSAvg, value); }
        
        private Enums.Punishment _Punishment = Enums.Punishment.TurnOff;
        public Enums.Punishment Punishment { get => _Punishment; set => SetValue(ref _Punishment, value); }
        
        private bool _CheckAllUserBlocksCombined = true;
        public bool CheckAllUserBlocksCombined { get => _CheckAllUserBlocksCombined; set => SetValue(ref _CheckAllUserBlocksCombined, value); }
        
        private bool _PunishAllUserBlocksCombinedOnExcessLimits = true;
        public bool PunishAllUserBlocksCombinedOnExcessLimits { get => _PunishAllUserBlocksCombinedOnExcessLimits; set => SetValue(ref _PunishAllUserBlocksCombinedOnExcessLimits, value); }
        
        private double _MaxAllBlocksCombinedRunTimeMS = 3.0;
        public double MaxAllBlocksCombinedRunTimeMS { get => _MaxAllBlocksCombinedRunTimeMS; set => SetValue(ref _MaxAllBlocksCombinedRunTimeMS, value); }
        
        private double _MaxAllBlocksCombinedRunTimeMSAvg = 2.0;
        public double MaxAllBlocksCombinedRunTimeMSAvg { get => _MaxAllBlocksCombinedRunTimeMSAvg; set => SetValue(ref _MaxAllBlocksCombinedRunTimeMSAvg, value); }
        
        private bool _IgnoreNPCs = true;
        public bool IgnoreNPCs { get => _IgnoreNPCs; set => SetValue(ref _IgnoreNPCs, value); }
        
        private bool _AllowSelfTurnOnExploit = false;
        public bool AllowSelfTurnOnExploit { get => _AllowSelfTurnOnExploit; set => SetValue(ref _AllowSelfTurnOnExploit, value); }
        
        private bool _AllowNPCToAutoTurnOn = true;
        public bool AllowNPCToAutoTurnOn { get => _AllowNPCToAutoTurnOn; set => SetValue(ref _AllowNPCToAutoTurnOn, value); }
        
        private bool _WarnUserOnOffense = true;
        public bool WarnUserOnOffense { get => _WarnUserOnOffense; set => SetValue(ref _WarnUserOnOffense, value); }
        
        private bool _TurnOffUnownedBlocks = true;
        public bool TurnOffUnownedBlocks { get => _TurnOffUnownedBlocks; set => SetValue(ref _TurnOffUnownedBlocks, value); }
        
        private bool _useSimTime = true;
        public bool UseSimTime { get => _useSimTime; set => SetValue(ref _useSimTime, value); }
        
        // Future update to have players in different tiers.
        // Each tier has a different run time adjustment.
        // Example: Tier 1 has Update1 run each tick, Tier 2 runs every 3rd tick, Tier 3 runs every 5 ticks.
        // The amount of adjustment will be configurable.
        private List<TierData> _TierPlans = new();
        public List<TierData> TierPlans { get => _TierPlans; set => SetValue(ref _TierPlans, value); }

        private bool _debugReporting;
        public bool DebugReporting { get => _debugReporting; set => SetValue(ref _debugReporting, value); }

        private long _PBMemoryThreshold = 10240000; // 10MB
        public long PBMemoryThreshold { get => _PBMemoryThreshold; set => SetValue(ref _PBMemoryThreshold, value); }

        private bool _killBlockProtectedInSafeZone;
        public bool KillBlockProtectedInSafeZone { get => _killBlockProtectedInSafeZone; set => SetValue(ref _killBlockProtectedInSafeZone, value); }
    }
}
