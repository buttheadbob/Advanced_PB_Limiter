#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils.Reports
{
    /// <summary>
    /// A row of one of the Reports grids. It is created once and then updated in place, so WPF keeps the row's container
    /// and only the cells whose value changed are redrawn. Before this, every live refresh replaced both grids' sources
    /// and rebuilt every cell, including the buttons.
    /// </summary>
    public abstract class ReportRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>A row of the per block grid on the Reports tab.</summary>
    public sealed class CustomBlockReport : ReportRow
    {
        private ulong _steamId;
        private string _playerName = "";
        private string _gridName = "";
        private string? _blockName = "";
        private double _lastRunTimeMS;
        private double _avgMS;
        private double _peekRunTimeMS;
        private int _offences;
        private int _recompiles;
        private string _memoryUsed = "";
        private bool _resting;
        private bool _memoryInitialized;
        private long _memoryBytes;
        private bool _memoryUnmeasured;
        private MyProgrammableBlock? _pbBlock;

        public long EntityId { get; }

        public ulong SteamId
        {
            get => _steamId;
            private set => Set(ref _steamId, value);
        }

        public string PlayerName
        {
            get => _playerName;
            private set => Set(ref _playerName, value);
        }

        public string GridName
        {
            get => _gridName;
            private set => Set(ref _gridName, value);
        }

        public string? BlockName
        {
            get => _blockName;
            private set => Set(ref _blockName, value);
        }

        public double LastRunTimeMS
        {
            get => _lastRunTimeMS;
            private set => Set(ref _lastRunTimeMS, value);
        }

        public double AvgMS
        {
            get => _avgMS;
            private set => Set(ref _avgMS, value);
        }

        public double PeekRunTimeMS
        {
            get => _peekRunTimeMS;
            private set => Set(ref _peekRunTimeMS, value);
        }

        public int Offences
        {
            get => _offences;
            private set => Set(ref _offences, value);
        }

        public int Recompiles
        {
            get => _recompiles;
            private set => Set(ref _recompiles, value);
        }

        public string MemoryUsed
        {
            get => _memoryUsed;
            private set => Set(ref _memoryUsed, value);
        }

        /// <summary>The measured bytes, for the summary tiles; the grid shows <see cref="MemoryUsed"/>.</summary>
        public long MemoryBytes { get; private set; }

        /// <summary>At least one script could not be measured, as the reports show it.</summary>
        public bool MemoryUnmeasured { get; private set; }

        /// <summary>Resting between rare runs, not run lately: its run times may be an hour old and are left out of the totals.</summary>
        public bool Resting
        {
            get => _resting;
            private set => Set(ref _resting, value);
        }

        /// <summary>The Turn Off button's target. Null after the row is detached.</summary>
        public MyProgrammableBlock? pbBlock => _pbBlock;

        public CustomBlockReport(TrackedPlayer player, TrackedPBBlock block, bool resting)
        {
            EntityId = block.EntityId;
            _pbBlock = block.ProgrammableBlock;
            Update(player, block, resting);
        }

        public void Update(TrackedPlayer player, TrackedPBBlock block, bool resting)
        {
            SteamId = player.SteamId;
            PlayerName = player.PlayerName ?? "Unknown";
            GridName = block.GridName;
            BlockName = block.BlockName;
            LastRunTimeMS = Math.Round(block.LastRunTimeMS, 6, MidpointRounding.ToEven);
            AvgMS = Math.Round(block.RunTimeMSAvg, 6, MidpointRounding.ToEven);
            PeekRunTimeMS = block.PeekRunTimeMS;
            Offences = block.Offences.Count;
            Recompiles = block.Recompiles;
            Resting = resting;
            MemoryBytes = block.memoryUsage;
            MemoryUnmeasured = block.MemoryUnmeasured;

            if (!_memoryInitialized || _memoryBytes != block.memoryUsage || _memoryUnmeasured != block.MemoryUnmeasured)
            {
                _memoryInitialized = true;
                _memoryBytes = block.memoryUsage;
                _memoryUnmeasured = block.MemoryUnmeasured;
                MemoryUsed = MemoryText.Block(_memoryBytes, _memoryUnmeasured);
            }
        }

        /// <summary>Releases the game object before the row leaves both the grid and the report snapshot.</summary>
        internal void Detach()
        {
            if (_pbBlock is null) return;
            _pbBlock = null;
            Raise(nameof(pbBlock));
        }
    }

    /// <summary>A row of the per player grid on the Reports tab.</summary>
    public sealed class CustomPlayerReport : ReportRow
    {
        private ulong _steamId;
        private string _playerName = "";
        private double _combinedLastRunTimeMS;
        private double _combinedAvgMS;
        private int _offences;
        private int _pbCount;
        private int _recompiles;
        private string _memoryUsed = "";
        private bool _memoryInitialized;
        private long _memoryBytes;
        private bool _memoryUnmeasured;
        private TrackedPlayer? _playerTracked;
        private CombinedRunTimes _combined;

        public long PlayerId { get; }

        public ulong SteamId
        {
            get => _steamId;
            private set => Set(ref _steamId, value);
        }

        public string PlayerName
        {
            get => _playerName;
            private set => Set(ref _playerName, value);
        }

        public double CombinedLastRunTimeMS
        {
            get => _combinedLastRunTimeMS;
            private set => Set(ref _combinedLastRunTimeMS, value);
        }

        public double CombinedAvgMS
        {
            get => _combinedAvgMS;
            private set => Set(ref _combinedAvgMS, value);
        }

        public int Offences
        {
            get => _offences;
            private set => Set(ref _offences, value);
        }

        public int PbCount
        {
            get => _pbCount;
            private set => Set(ref _pbCount, value);
        }

        public int Recompiles
        {
            get => _recompiles;
            private set => Set(ref _recompiles, value);
        }

        public string MemoryUsed
        {
            get => _memoryUsed;
            private set => Set(ref _memoryUsed, value);
        }

        /// <summary>The measured bytes, for the summary tiles; the grid shows <see cref="MemoryUsed"/>.</summary>
        public long MemoryBytes { get; private set; }

        /// <summary>At least one of the player's scripts could not be measured.</summary>
        public bool MemoryUnmeasured { get; private set; }

        /// <summary>The Turn All Off button's target. Null after the row is detached.</summary>
        public TrackedPlayer? PlayerTracked
        {
            get => _playerTracked;
            private set => Set(ref _playerTracked, value);
        }

        /// <summary>The combined run times, unrounded, as every report counts them; the hottest player is ranked by these.</summary>
        public CombinedRunTimes Combined => _combined;

        public CustomPlayerReport(TrackedPlayer player, PlayerTotals totals)
        {
            PlayerId = player.PlayerId;
            _combined = totals.Combined;
            Update(player, totals);
        }

        public void Update(TrackedPlayer player, PlayerTotals totals)
        {
            SteamId = player.SteamId;
            PlayerName = player.PlayerName ?? "Unknown";
            _combined = totals.Combined;
            CombinedLastRunTimeMS = Math.Round(totals.Combined.LastRunTimeMS, 4, MidpointRounding.ToEven);
            CombinedAvgMS = Math.Round(totals.Combined.RunTimeMSAvg, 4, MidpointRounding.ToEven);
            Offences = totals.Offences;
            PbCount = totals.Blocks;
            Recompiles = totals.Recompiles;
            MemoryBytes = totals.MemoryBytes;
            MemoryUnmeasured = totals.MemoryUnmeasured;
            PlayerTracked = player;

            if (!_memoryInitialized || _memoryBytes != totals.MemoryBytes || _memoryUnmeasured != totals.MemoryUnmeasured)
            {
                _memoryInitialized = true;
                _memoryBytes = totals.MemoryBytes;
                _memoryUnmeasured = totals.MemoryUnmeasured;
                MemoryUsed = MemoryText.Total(_memoryBytes, _memoryUnmeasured);
            }
        }

        /// <summary>Releases the tracked player before the row leaves both the grid and the report snapshot.</summary>
        internal void Detach()
        {
            if (_playerTracked is null) return;
            _playerTracked = null;
            Raise(nameof(PlayerTracked));
        }
    }
}
