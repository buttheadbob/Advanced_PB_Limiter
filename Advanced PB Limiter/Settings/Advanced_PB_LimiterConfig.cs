using System;
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
        
        public IEnumerable<Enums.Punishment> MyPunishments => Enum.GetValues(typeof(Enums.Punishment)).Cast<Enums.Punishment>();

    }
}
