using System.Collections.Concurrent;
using Advanced_PB_Limiter.Utils;
using Torch;

namespace Advanced_PB_Limiter.Settings
{
    public class Advanced_PB_LimiterConfig : ViewModel
    {
        private bool _AllowStartGracePeriod = true;
        public bool AllowStartGracePeriod { get => _AllowStartGracePeriod; set => SetValue(ref _AllowStartGracePeriod, value); }
        
        private int _RemoveInactivePBsAfterSeconds = 60;
        public int RemoveInactivePBsAfterSeconds { get => _RemoveInactivePBsAfterSeconds; set => SetValue(ref _RemoveInactivePBsAfterSeconds, value); }
        
        private int _RemovePlayersWithNoPBFrequencyInMinutes = 60;
        public int RemovePlayersWithNoPBFrequencyInMinutes { get => _RemovePlayersWithNoPBFrequencyInMinutes; set => SetValue(ref _RemovePlayersWithNoPBFrequencyInMinutes, value); }
        
        private int _MaxRunsToTrack = 10;
        public int MaxRunsToTrack { get => _MaxRunsToTrack; set => SetValue(ref _MaxRunsToTrack, value); }
        
        private bool _ClearHistoryOnRecompile = true;
        public bool ClearHistoryOnRecompile { get => _ClearHistoryOnRecompile; set => SetValue(ref _ClearHistoryOnRecompile, value); }
        
        private int _GracefulShutDownRequestDelay = 10;
        public int GracefulShutDownRequestDelay { get => _GracefulShutDownRequestDelay; set => SetValue(ref _GracefulShutDownRequestDelay, value); }
        
        private ConcurrentDictionary<ulong, PrivilegedPlayer> _PrivilegedPlayers = new ();
        public ConcurrentDictionary<ulong, PrivilegedPlayer> PrivilegedPlayers { get => _PrivilegedPlayers; set => SetValue(ref _PrivilegedPlayers, value); }
        
        private int _PunishDamageAmount = 100;
        public int PunishDamageAmount { get => _PunishDamageAmount; set => SetValue(ref _PunishDamageAmount, value); }
        
        private int _MaxOffencesBeforePunishment = 3;
        public int MaxOffencesBeforePunishment { get => _MaxOffencesBeforePunishment; set => SetValue(ref _MaxOffencesBeforePunishment, value); }
        
        private double _MaxRunTimeMS = 0.4;
        public double MaxRunTimeMS { get => _MaxRunTimeMS; set => SetValue(ref _MaxRunTimeMS, value); }
        
        private double _MaxRunTimeMSAvg = 0.3;
        public double MaxRunTimeMSAvg { get => _MaxRunTimeMSAvg; set => SetValue(ref _MaxRunTimeMSAvg, value); }
        
        private int _MaxPreviousRunsToCalculateMaxAvg = 100;
        public int MaxPreviousRunsToCalculateMaxAvg { get => _MaxPreviousRunsToCalculateMaxAvg; set => SetValue(ref _MaxPreviousRunsToCalculateMaxAvg, value); }
        
        private Enums.Punishment _Punishment = Enums.Punishment.TurnOff;
        public Enums.Punishment Punishment { get => _Punishment; set => SetValue(ref _Punishment, value); }
        
        private bool _CheckAllUserBlocksCombined = true;
        public bool CheckAllUserBlocksCombined { get => _CheckAllUserBlocksCombined; set => SetValue(ref _CheckAllUserBlocksCombined, value); }
        
        private bool _PunishAllUserBlocksCombinedOnExcessLimits = true;
        public bool PunishAllUserBlocksCombinedOnExcessLimits { get => _PunishAllUserBlocksCombinedOnExcessLimits; set => SetValue(ref _PunishAllUserBlocksCombinedOnExcessLimits, value); }
        
        private double _MaxAllBlocksCombinedRunTimeMS = 3.0;
        public double MaxAllBlocksCombinedRunTimeMS { get => _MaxAllBlocksCombinedRunTimeMS; set => SetValue(ref _MaxAllBlocksCombinedRunTimeMS, value); }
        
        private double _MaxAllBlocksCombinedRunTimeMSAvg = 1.0;
        public double MaxAllBlocksCombinedRunTimeMSAvg { get => _MaxAllBlocksCombinedRunTimeMSAvg; set => SetValue(ref _MaxAllBlocksCombinedRunTimeMSAvg, value); }
    }
}
