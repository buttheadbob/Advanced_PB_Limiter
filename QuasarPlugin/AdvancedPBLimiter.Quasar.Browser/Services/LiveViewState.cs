using System.Globalization;
using Advanced_PB_Limiter.QuasarContracts;

namespace AdvancedPBLimiter.Quasar.Services;

public enum LiveTab
{
    Overview,
    Blocks,
    Players
}

public enum BlockStateFilter
{
    All,
    On,
    Off,
    Resting,
    Offences
}

public sealed record HistoryPoint(
    DateTime AtUtc,
    int Players,
    int Blocks,
    double CombinedLast,
    double CombinedAverage,
    int Offences,
    int Recompiles,
    long Memory);

/// <summary>
/// The live page's browser-side view model. The Blocks and Players tables each keep their own filter and sort; the
/// Overview always shows the whole server.
/// The page renders far more often than snapshots arrive, so everything a render reads is worked out once per snapshot
/// or choice and kept. The lists are refilled in place rather than built anew: a server can list thousands of blocks,
/// a snapshot comes every few seconds, and the browser's garbage collector stops the page while it runs.
/// </summary>
public sealed class LiveViewState
{
    public const int DefaultPageSize = 50;
    public static readonly int[] PageSizes = [25, 50, 100];
    public const string DefaultBlockSort = nameof(BlockRowView.RunTimeMSAvg);
    public const string DefaultPlayerSort = nameof(PlayerRowView.CombinedRunTimeMSAvg);
    private const int TopCount = 5;

    // Text columns start A to Z, numbers start with the largest.
    private static readonly HashSet<string> TextColumns = new(StringComparer.Ordinal)
    {
        nameof(BlockRowView.BlockName), nameof(BlockRowView.GridName), nameof(BlockRowView.OwnerName), nameof(PlayerRowView.Name)
    };

    private static readonly Comparison<BlockRowView> HottestBlock = (a, b) =>
    {
        int order = b.RunTimeMSAvg.CompareTo(a.RunTimeMSAvg);
        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.BlockName, b.BlockName);
    };

    private static readonly Comparison<PlayerRowView> HottestPlayer = (a, b) =>
    {
        int order = b.CombinedRunTimeMSAvg.CompareTo(a.CombinedRunTimeMSAvg);
        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    };

    // Two of each so a held order can read the last order while the next one is filled.
    private readonly List<BlockRowView> _blocksA = new();
    private readonly List<BlockRowView> _blocksB = new();
    private readonly List<PlayerRowView> _playersA = new();
    private readonly List<PlayerRowView> _playersB = new();
    private List<BlockRowView> _blocks;
    private List<PlayerRowView> _players;
    private readonly List<BlockRowView> _pageBlocks = new();
    private readonly List<PlayerRowView> _pagePlayers = new();
    private readonly List<BlockRowView> _topBlocks = new(TopCount + 1);
    private readonly List<PlayerRowView> _topPlayers = new(TopCount + 1);
    private readonly HashSet<string> _playersWithBlocksOn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _blockPlace = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _playerPlace = new(StringComparer.Ordinal);
    private readonly Comparison<BlockRowView> _byBlockPlace;
    private readonly Comparison<PlayerRowView> _byPlayerPlace;
    private Comparison<BlockRowView> _blockOrder;
    private Comparison<PlayerRowView> _playerOrder;

    // BlocksOf, kept until the snapshot or the player changes.
    private ReportSnapshotView? _blocksOfSnapshot;
    private string? _blocksOfPlayer;
    private IReadOnlyList<BlockRowView> _blocksOf = Array.Empty<BlockRowView>();

    public LiveViewState()
    {
        _blocks = _blocksA;
        _players = _playersA;
        _blockOrder = BlockOrder(BlockSortColumn, BlockSortDescending);
        _playerOrder = PlayerOrder(PlayerSortColumn, PlayerSortDescending);
        _byBlockPlace = (a, b) =>
        {
            int order = Place(_blockPlace, a.EntityId).CompareTo(Place(_blockPlace, b.EntityId));
            return order != 0 ? order : _blockOrder(a, b);
        };
        _byPlayerPlace = (a, b) =>
        {
            int order = Place(_playerPlace, a.IdentityId).CompareTo(Place(_playerPlace, b.IdentityId));
            return order != 0 ? order : _playerOrder(a, b);
        };
    }

    public ReportSnapshotView? Snapshot { get; private set; }

    public string BlockFilter { get; private set; } = "";

    /// <summary>The one player whose blocks the Blocks table shows, from the player details' link; null for every player. Not saved.</summary>
    public string? OwnerFilterId { get; private set; }
    public string OwnerFilterName { get; private set; } = "";
    public string PlayerFilter { get; private set; } = "";
    public BlockStateFilter StateFilter { get; private set; } = BlockStateFilter.All;
    public string BlockSortColumn { get; private set; } = DefaultBlockSort;
    public bool BlockSortDescending { get; private set; } = true;
    public string PlayerSortColumn { get; private set; } = DefaultPlayerSort;
    public bool PlayerSortDescending { get; private set; } = true;
    public LiveTab Tab { get; private set; } = LiveTab.Overview;
    public int PageSize { get; private set; } = DefaultPageSize;
    public int BlockPage { get; private set; } = 1;
    public int PlayerPage { get; private set; } = 1;

    public IReadOnlyList<BlockRowView> VisibleBlocks => _blocks;
    public IReadOnlyList<PlayerRowView> VisiblePlayers => _players;
    public IReadOnlyList<BlockRowView> PageBlocks => _pageBlocks;
    public IReadOnlyList<PlayerRowView> PagePlayers => _pagePlayers;
    public IReadOnlyList<BlockRowView> TopBlocks => _topBlocks;
    public IReadOnlyList<PlayerRowView> TopPlayers => _topPlayers;

    public int BlockMatches => _blocks.Count;
    public int PlayerMatches => _players.Count;
    public int BlockPageCount => PageCount(BlockMatches, PageSize);
    public int PlayerPageCount => PageCount(PlayerMatches, PageSize);

    /// <summary>The text filter of the table on show.</summary>
    public string Filter => Tab == LiveTab.Players ? PlayerFilter : BlockFilter;

    /// <summary>The table on show is filtered: by its text and, for Blocks, the state chip and the owner.</summary>
    public bool Filtering => Tab == LiveTab.Players
        ? !string.IsNullOrWhiteSpace(PlayerFilter)
        : !string.IsNullOrWhiteSpace(BlockFilter) || StateFilter != BlockStateFilter.All || OwnerFilterId is not null;

    public string SortColumn => Tab == LiveTab.Players ? PlayerSortColumn : BlockSortColumn;
    public bool SortDescending => Tab == LiveTab.Players ? PlayerSortDescending : BlockSortDescending;

    public int OnCount { get; private set; }
    public int OffCount { get; private set; }
    public int RestingCount { get; private set; }
    public int OffenceBlockCount { get; private set; }

    public double MaxBlockAverage => _topBlocks.Count == 0 ? 0 : _topBlocks[0].RunTimeMSAvg;

    /// <summary>The players with at least one block on in the report: the only ones a turn-off can do anything for.</summary>
    public IReadOnlySet<string> PlayersWithBlocksOn => _playersWithBlocksOn;

    /// <summary>
    /// The report left blocks out, so a player with none listed, or none listed on, may still have blocks on: turn-off is
    /// offered for every player.
    /// </summary>
    public bool AnyPlayerMayHaveBlocksOn => Snapshot?.Truncated == true;

    /// <summary>
    /// The pointer is on a table: a new snapshot keeps the rows where they are, so a click lands on the row it was aimed
    /// at. Sorting, filtering and paging still apply at once.
    /// </summary>
    public bool HoldingOrder { get; private set; }

    // A snapshot came while the order was held, so the order is out of date when the hold ends.
    private bool _heldSnapshot;

    public SessionHistory History { get; } = new();

    // The server's totals, summed once per snapshot.
    public int TotalPlayers => Snapshot?.Players.Count ?? 0;
    public int TotalBlocks { get; private set; }
    public int ListedBlocks => Snapshot?.Blocks.Count ?? 0;
    public int TotalOffences { get; private set; }
    public int TotalRecompiles { get; private set; }
    public double CombinedLast { get; private set; }
    public double CombinedAverage { get; private set; }
    public long MemoryBytes { get; private set; }
    public bool MemoryUnmeasured { get; private set; }

    public string TabKey => Tab switch
    {
        LiveTab.Blocks => "blocks",
        LiveTab.Players => "players",
        _ => "overview"
    };

    public void SetSnapshot(ReportSnapshotView snapshot, bool resetHistory = false)
    {
        if (resetHistory)
            History.Clear();

        Snapshot = snapshot;
        SumTotals();
        History.Add(new HistoryPoint(DateTime.UtcNow, TotalPlayers, TotalBlocks, CombinedLast, CombinedAverage, TotalOffences, TotalRecompiles, MemoryBytes));
        FilterAndSort(resetPages: false);
    }

    /// <summary>Forgets the selected server's data and its owner filter, but keeps the admin's other filter and sort choices.</summary>
    public void SetServer()
    {
        Snapshot = null;
        History.Clear();
        SumTotals();
        HoldingOrder = false;
        // An identity id belongs to one world: it matches nobody on another server.
        OwnerFilterId = null;
        OwnerFilterName = "";
        FilterAndSort(resetPages: true);
    }

    /// <summary>The pointer is not on the new tab's table, if it has one: a hold ends with the tab.</summary>
    public void SetTab(LiveTab tab)
    {
        Tab = tab;
        SetHoldOrder(false);
    }

    /// <summary>
    /// Holds the rows' order, see <see cref="HoldingOrder"/>, or ends the hold. True when the rows were sorted again, as
    /// a snapshot came while they were held, so the rows on show may have changed; also when the hold had already ended
    /// as the table emptied.
    /// </summary>
    public bool SetHoldOrder(bool hold)
    {
        HoldingOrder = hold;
        if (hold || !_heldSnapshot) return false;
        FilterAndSort(resetPages: false);
        return true;
    }

    /// <summary>Sets the text filter of the table on show.</summary>
    public void SetFilter(string filter)
    {
        if (string.Equals(Filter, filter, StringComparison.Ordinal)) return;
        if (Tab == LiveTab.Players) PlayerFilter = filter;
        else BlockFilter = filter;
        FilterAndSort(resetPages: true);
    }

    public void SetStateFilter(BlockStateFilter filter)
    {
        if (StateFilter == filter) return;
        StateFilter = filter;
        FilterAndSort(resetPages: true);
    }

    /// <summary>Clears the filters of the table on show.</summary>
    public void ClearFilters()
    {
        if (Tab == LiveTab.Players)
            PlayerFilter = "";
        else
        {
            BlockFilter = "";
            StateFilter = BlockStateFilter.All;
            OwnerFilterId = null;
            OwnerFilterName = "";
        }
        FilterAndSort(resetPages: true);
    }

    /// <summary>Shows the Blocks table with one player's blocks only, as the player details' link does: by id, so no other player's match.</summary>
    public void ShowBlocksOf(PlayerRowView player)
    {
        Tab = LiveTab.Blocks;
        HoldingOrder = false;
        BlockFilter = "";
        StateFilter = BlockStateFilter.All;
        OwnerFilterId = player.IdentityId;
        OwnerFilterName = player.Name;
        FilterAndSort(resetPages: true);
    }

    public void ClearOwnerFilter()
    {
        OwnerFilterId = null;
        OwnerFilterName = "";
        FilterAndSort(resetPages: true);
    }

    /// <summary>Sorts the table on show by <paramref name="column"/>, or turns its order round when it is sorted by it already.</summary>
    public void SetSort(string column)
    {
        bool players = Tab == LiveTab.Players;
        string current = players ? PlayerSortColumn : BlockSortColumn;
        bool descending = string.Equals(current, column, StringComparison.Ordinal)
            ? !(players ? PlayerSortDescending : BlockSortDescending)
            : !TextColumns.Contains(column);

        if (players)
        {
            PlayerSortColumn = column;
            PlayerSortDescending = descending;
            _playerOrder = PlayerOrder(column, descending);
        }
        else
        {
            BlockSortColumn = column;
            BlockSortDescending = descending;
            _blockOrder = BlockOrder(column, descending);
        }

        FilterAndSort(resetPages: true);
    }

    public void SetPageSize(int pageSize)
    {
        if (!PageSizes.Contains(pageSize)) return;
        PageSize = pageSize;
        BlockPage = Math.Clamp(BlockPage, 1, BlockPageCount);
        PlayerPage = Math.Clamp(PlayerPage, 1, PlayerPageCount);
        SlicePages();
    }

    public void SetBlockPage(int page)
    {
        BlockPage = Math.Clamp(page, 1, BlockPageCount);
        Slice(_blocks, BlockPage, _pageBlocks);
    }

    public void SetPlayerPage(int page)
    {
        PlayerPage = Math.Clamp(page, 1, PlayerPageCount);
        Slice(_players, PlayerPage, _pagePlayers);
    }

    /// <summary>The player's blocks in the report, the heaviest average first. Kept until the snapshot or player changes.</summary>
    public IReadOnlyList<BlockRowView> BlocksOf(PlayerRowView player)
    {
        if (ReferenceEquals(_blocksOfSnapshot, Snapshot) && string.Equals(_blocksOfPlayer, player.IdentityId, StringComparison.Ordinal))
            return _blocksOf;

        List<BlockRowView> blocks = new();
        if (Snapshot is not null)
        {
            foreach (BlockRowView block in Snapshot.Blocks)
            {
                if (string.Equals(block.OwnerIdentityId, player.IdentityId, StringComparison.Ordinal))
                    blocks.Add(block);
            }
            blocks.Sort(HottestBlock);
        }
        _blocksOfSnapshot = Snapshot;
        _blocksOfPlayer = player.IdentityId;
        _blocksOf = blocks;
        return blocks;
    }

    public void ApplyPreferences(UiPreferences preferences)
    {
        PageSize = PageSizes.Contains(preferences.PageSize) ? preferences.PageSize : DefaultPageSize;
        Tab = Enum.IsDefined(preferences.Tab) ? preferences.Tab : LiveTab.Overview;
        StateFilter = Enum.IsDefined(preferences.StateFilter) ? preferences.StateFilter : BlockStateFilter.All;
        BlockSortColumn = string.IsNullOrWhiteSpace(preferences.BlockSortColumn) ? DefaultBlockSort : preferences.BlockSortColumn;
        BlockSortDescending = preferences.BlockSortDescending;
        PlayerSortColumn = string.IsNullOrWhiteSpace(preferences.PlayerSortColumn) ? DefaultPlayerSort : preferences.PlayerSortColumn;
        PlayerSortDescending = preferences.PlayerSortDescending;
        _blockOrder = BlockOrder(BlockSortColumn, BlockSortDescending);
        _playerOrder = PlayerOrder(PlayerSortColumn, PlayerSortDescending);
        BlockFilter = preferences.BlockFilter ?? "";
        PlayerFilter = preferences.PlayerFilter ?? "";
        FilterAndSort(resetPages: true);
    }

    public UiPreferences ToPreferences()
    {
        return new UiPreferences
        {
            Tab = Tab,
            BlockSortColumn = BlockSortColumn,
            BlockSortDescending = BlockSortDescending,
            PlayerSortColumn = PlayerSortColumn,
            PlayerSortDescending = PlayerSortDescending,
            BlockFilter = BlockFilter,
            PlayerFilter = PlayerFilter,
            StateFilter = StateFilter,
            PageSize = PageSize
        };
    }

    private void SumTotals()
    {
        int blocks = 0, offences = 0, recompiles = 0;
        double last = 0, average = 0;
        long memory = 0;
        bool unmeasured = false;
        if (Snapshot is not null)
        {
            foreach (PlayerRowView player in Snapshot.Players)
            {
                blocks += player.BlockCount;
                offences += player.Offences;
                recompiles += player.Recompiles;
                last += player.CombinedLastRunTimeMS;
                average += player.CombinedRunTimeMSAvg;
                memory += player.MemoryBytes;
                unmeasured |= player.MemoryUnmeasured;
            }
        }
        TotalBlocks = blocks;
        TotalOffences = offences;
        TotalRecompiles = recompiles;
        CombinedLast = last;
        CombinedAverage = average;
        MemoryBytes = memory;
        MemoryUnmeasured = unmeasured;
    }

    private void FilterAndSort(bool resetPages)
    {
        if (resetPages)
            BlockPage = PlayerPage = 1;

        bool hold = HoldingOrder && !resetPages;
        _heldSnapshot = hold;
        List<BlockRowView> blocks = ReferenceEquals(_blocks, _blocksA) ? _blocksB : _blocksA;
        List<PlayerRowView> players = ReferenceEquals(_players, _playersA) ? _playersB : _playersA;
        blocks.Clear();
        players.Clear();
        _topBlocks.Clear();
        _topPlayers.Clear();
        _playersWithBlocksOn.Clear();
        OnCount = OffCount = RestingCount = OffenceBlockCount = 0;

        if (Snapshot is not null)
        {
            string blockNeedle = BlockFilter.Trim();
            foreach (BlockRowView block in Snapshot.Blocks)
            {
                if (block.Enabled)
                {
                    _playersWithBlocksOn.Add(block.OwnerIdentityId);
                    if (!block.Resting) OnCount++;
                }
                else
                    OffCount++;
                if (block.Resting) RestingCount++;
                if (block.Offences > 0) OffenceBlockCount++;

                // The Overview: the whole server, whatever the tables are filtered by.
                AddToTop(_topBlocks, block, HottestBlock);

                if (ShowsState(block) && (OwnerFilterId is null || string.Equals(block.OwnerIdentityId, OwnerFilterId, StringComparison.Ordinal))
                    && (blockNeedle.Length == 0 || Matches(block.BlockName, blockNeedle)
                                          || Matches(block.GridName, blockNeedle) || Matches(block.OwnerName, blockNeedle)))
                    blocks.Add(block);
            }

            string playerNeedle = PlayerFilter.Trim();
            foreach (PlayerRowView player in Snapshot.Players)
            {
                AddToTop(_topPlayers, player, HottestPlayer);
                if (playerNeedle.Length == 0 || Matches(player.Name, playerNeedle) || Matches(player.SteamId, playerNeedle))
                    players.Add(player);
            }

            if (hold)
            {
                Places(_blocks, _blockPlace, static block => block.EntityId);
                Places(_players, _playerPlace, static player => player.IdentityId);
            }
            blocks.Sort(hold ? _byBlockPlace : _blockOrder);
            players.Sort(hold ? _byPlayerPlace : _playerOrder);
        }

        _blocks = blocks;
        _players = players;
        // The table on show went empty: its rows, and the pointer's place on them, are gone without a leave event.
        if ((Tab == LiveTab.Players ? players.Count : blocks.Count) == 0)
            HoldingOrder = false;
        BlockPage = Math.Clamp(BlockPage, 1, BlockPageCount);
        PlayerPage = Math.Clamp(PlayerPage, 1, PlayerPageCount);
        SlicePages();
    }

    private bool ShowsState(BlockRowView block) => StateFilter switch
    {
        BlockStateFilter.On => block.Enabled && !block.Resting,
        BlockStateFilter.Off => !block.Enabled,
        BlockStateFilter.Resting => block.Resting,
        BlockStateFilter.Offences => block.Offences > 0,
        _ => true
    };

    /// <summary>Keeps the first <see cref="TopCount"/> rows by <paramref name="order"/>, without sorting all of them.</summary>
    private static void AddToTop<T>(List<T> top, T row, Comparison<T> order)
    {
        if (top.Count == TopCount && order(row, top[TopCount - 1]) >= 0) return;
        int at = top.Count;
        while (at > 0 && order(row, top[at - 1]) < 0) at--;
        top.Insert(at, row);
        if (top.Count > TopCount) top.RemoveAt(TopCount);
    }

    /// <summary>Where each row stood in the order shown before, for a held order.</summary>
    private static void Places<T>(List<T> before, Dictionary<string, int> place, Func<T, string> key)
    {
        place.Clear();
        for (int index = 0; index < before.Count; index++)
            place[key(before[index])] = index;
    }

    /// <summary>A row's place in the order shown before; rows new to the list go after the others.</summary>
    private static int Place(Dictionary<string, int> place, string key) => place.TryGetValue(key, out int at) ? at : int.MaxValue;

    /// <summary>
    /// The table's order. Rows that tie are ordered by id, so they keep their places from one snapshot to the next. A
    /// script the memory check could not measure has no size: it is listed after the measured ones either way.
    /// </summary>
    private static Comparison<BlockRowView> BlockOrder(string column, bool descending)
    {
        Comparison<BlockRowView> key = column switch
        {
            nameof(BlockRowView.BlockName) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.BlockName, b.BlockName),
            nameof(BlockRowView.GridName) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.GridName, b.GridName),
            nameof(BlockRowView.OwnerName) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.OwnerName, b.OwnerName),
            nameof(BlockRowView.LastRunTimeMS) => static (a, b) => a.LastRunTimeMS.CompareTo(b.LastRunTimeMS),
            nameof(BlockRowView.PeekRunTimeMS) => static (a, b) => a.PeekRunTimeMS.CompareTo(b.PeekRunTimeMS),
            nameof(BlockRowView.Offences) => static (a, b) => a.Offences.CompareTo(b.Offences),
            nameof(BlockRowView.Recompiles) => static (a, b) => a.Recompiles.CompareTo(b.Recompiles),
            nameof(BlockRowView.MemoryBytes) => static (a, b) => a.MemoryBytes.CompareTo(b.MemoryBytes),
            _ => static (a, b) => a.RunTimeMSAvg.CompareTo(b.RunTimeMSAvg)
        };
        bool memory = column == nameof(BlockRowView.MemoryBytes);
        return (a, b) =>
        {
            if (memory && a.MemoryUnmeasured != b.MemoryUnmeasured) return a.MemoryUnmeasured ? 1 : -1;
            int order = descending ? key(b, a) : key(a, b);
            return order != 0 ? order : string.CompareOrdinal(a.EntityId, b.EntityId);
        };
    }

    private static Comparison<PlayerRowView> PlayerOrder(string column, bool descending)
    {
        Comparison<PlayerRowView> key = column switch
        {
            nameof(PlayerRowView.Name) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            nameof(PlayerRowView.SteamId) => static (a, b) => SteamId(a).CompareTo(SteamId(b)),
            nameof(PlayerRowView.BlockCount) => static (a, b) => a.BlockCount.CompareTo(b.BlockCount),
            nameof(PlayerRowView.CombinedLastRunTimeMS) => static (a, b) => a.CombinedLastRunTimeMS.CompareTo(b.CombinedLastRunTimeMS),
            nameof(PlayerRowView.Offences) => static (a, b) => a.Offences.CompareTo(b.Offences),
            nameof(PlayerRowView.Recompiles) => static (a, b) => a.Recompiles.CompareTo(b.Recompiles),
            nameof(PlayerRowView.MemoryBytes) => static (a, b) => a.MemoryBytes.CompareTo(b.MemoryBytes),
            _ => static (a, b) => a.CombinedRunTimeMSAvg.CompareTo(b.CombinedRunTimeMSAvg)
        };
        bool memory = column == nameof(PlayerRowView.MemoryBytes);
        return (a, b) =>
        {
            if (memory && a.MemoryUnmeasured != b.MemoryUnmeasured) return a.MemoryUnmeasured ? 1 : -1;
            int order = descending ? key(b, a) : key(a, b);
            return order != 0 ? order : string.CompareOrdinal(a.IdentityId, b.IdentityId);
        };
    }

    private static ulong SteamId(PlayerRowView player) =>
        ulong.TryParse(player.SteamId, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id) ? id : 0UL;

    private void SlicePages()
    {
        Slice(_blocks, BlockPage, _pageBlocks);
        Slice(_players, PlayerPage, _pagePlayers);
    }

    private void Slice<T>(List<T> rows, int page, List<T> slice)
    {
        slice.Clear();
        int start = Math.Max(0, (page - 1) * PageSize);
        int end = Math.Min(rows.Count, start + PageSize);
        for (int index = start; index < end; index++)
            slice.Add(rows[index]);
    }

    private static int PageCount(int count, int pageSize)
    {
        if (count <= 0) return 1;
        return (count + pageSize - 1) / pageSize;
    }

    private static bool Matches(string value, string needle) =>
        value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The last few snapshots this browser tab has seen, for the small trend charts. The series are built as a point is
/// added, not each time the page renders them.
/// </summary>
public sealed class SessionHistory
{
    public const int MaxPoints = 120;

    private readonly List<HistoryPoint> _points = new();

    public IReadOnlyList<HistoryPoint> Points => _points;

    public IReadOnlyList<double> Players { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> Blocks { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> CombinedLast { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> CombinedAverage { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> Offences { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> Recompiles { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> Memory { get; private set; } = Array.Empty<double>();

    public void Add(HistoryPoint point)
    {
        _points.Add(point);
        if (_points.Count > MaxPoints)
            _points.RemoveAt(0);
        BuildSeries();
    }

    public void Clear()
    {
        _points.Clear();
        BuildSeries();
    }

    private void BuildSeries()
    {
        int count = _points.Count;
        double[] players = new double[count], blocks = new double[count], last = new double[count], average = new double[count];
        double[] offences = new double[count], recompiles = new double[count], memory = new double[count];
        for (int index = 0; index < count; index++)
        {
            HistoryPoint point = _points[index];
            players[index] = point.Players;
            blocks[index] = point.Blocks;
            last[index] = point.CombinedLast;
            average[index] = point.CombinedAverage;
            offences[index] = point.Offences;
            recompiles[index] = point.Recompiles;
            memory[index] = point.Memory;
        }
        Players = players;
        Blocks = blocks;
        CombinedLast = last;
        CombinedAverage = average;
        Offences = offences;
        Recompiles = recompiles;
        Memory = memory;
    }
}
