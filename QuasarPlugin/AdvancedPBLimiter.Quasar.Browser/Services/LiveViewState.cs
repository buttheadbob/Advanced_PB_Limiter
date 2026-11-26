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

public sealed record PblTrendSeries(string Label, string Tone, IReadOnlyList<double> Values);

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
/// The live page's browser-side view model. Filtering, sorting and paging happen once per snapshot instead of once
/// per render, and the session history is bounded so a tab left open for hours does not grow forever.
/// </summary>
public sealed class LiveViewState
{
    public const int DefaultPageSize = 50;
    public static readonly int[] PageSizes = [25, 50, 100];

    private static readonly IComparer<IComparable> SameEverywhere = Comparer<IComparable>.Create(
        (x, y) => x is string a && y is string b
            ? StringComparer.OrdinalIgnoreCase.Compare(a, b)
            : Comparer<IComparable>.Default.Compare(x, y));

    public ReportSnapshotView? Snapshot { get; private set; }

    public string Filter { get; private set; } = "";
    public BlockStateFilter StateFilter { get; private set; } = BlockStateFilter.All;
    public string SortColumn { get; private set; } = nameof(BlockRowView.LastRunTimeMS);
    public bool SortDescending { get; private set; } = true;
    public LiveTab Tab { get; private set; } = LiveTab.Overview;
    public int PageSize { get; private set; } = DefaultPageSize;
    public int BlockPage { get; private set; } = 1;
    public int PlayerPage { get; private set; } = 1;

    public IReadOnlyList<BlockRowView> VisibleBlocks { get; private set; } = Array.Empty<BlockRowView>();
    public IReadOnlyList<PlayerRowView> VisiblePlayers { get; private set; } = Array.Empty<PlayerRowView>();
    public IReadOnlyList<BlockRowView> PageBlocks { get; private set; } = Array.Empty<BlockRowView>();
    public IReadOnlyList<PlayerRowView> PagePlayers { get; private set; } = Array.Empty<PlayerRowView>();
    public IReadOnlyList<BlockRowView> TopBlocks { get; private set; } = Array.Empty<BlockRowView>();
    public IReadOnlyList<PlayerRowView> TopPlayers { get; private set; } = Array.Empty<PlayerRowView>();

    public int BlockMatches => VisibleBlocks.Count;
    public int PlayerMatches => VisiblePlayers.Count;
    public int BlockPageCount => PageCount(BlockMatches, PageSize);
    public int PlayerPageCount => PageCount(PlayerMatches, PageSize);

    public int OnCount { get; private set; }
    public int OffCount { get; private set; }
    public int RestingCount { get; private set; }
    public int OffenceBlockCount { get; private set; }

    public double MaxBlockLast { get; private set; }
    public double MaxPlayerCombined { get; private set; }

    public SessionHistory History { get; } = new();

    public int TotalPlayers => Snapshot?.Players.Count ?? 0;
    public int TotalBlocks => Snapshot?.Players.Sum(player => player.BlockCount) ?? 0;
    public int TotalOffences => Snapshot?.Players.Sum(player => player.Offences) ?? 0;
    public int TotalRecompiles => Snapshot?.Players.Sum(player => player.Recompiles) ?? 0;
    public double CombinedLast => Snapshot?.Players.Sum(player => player.CombinedLastRunTimeMS) ?? 0;
    public double CombinedAverage => Snapshot?.Players.Sum(player => player.CombinedRunTimeMSAvg) ?? 0;
    public long MemoryBytes => Snapshot?.Players.Sum(player => player.MemoryBytes) ?? 0;
    public bool MemoryUnmeasured => Snapshot?.Players.Exists(player => player.MemoryUnmeasured) ?? false;
    public bool HasSnapshot => Snapshot is not null;

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
        History.Add(snapshot);
        FilterAndSort(resetPages: false);
    }

    /// <summary>Forgets the selected server's data, but keeps the admin's filter and sort choices.</summary>
    public void SetServer()
    {
        Snapshot = null;
        History.Clear();
        VisibleBlocks = Array.Empty<BlockRowView>();
        VisiblePlayers = Array.Empty<PlayerRowView>();
        PageBlocks = Array.Empty<BlockRowView>();
        PagePlayers = Array.Empty<PlayerRowView>();
        TopBlocks = Array.Empty<BlockRowView>();
        TopPlayers = Array.Empty<PlayerRowView>();
        OnCount = OffCount = RestingCount = OffenceBlockCount = 0;
        MaxBlockLast = MaxPlayerCombined = 0;
        BlockPage = PlayerPage = 1;
    }

    public void SetTab(LiveTab tab)
    {
        Tab = tab;
        if (tab == LiveTab.Blocks) BlockPage = 1;
        else if (tab == LiveTab.Players) PlayerPage = 1;
    }

    public void SetFilter(string filter)
    {
        if (string.Equals(Filter, filter, StringComparison.Ordinal)) return;
        Filter = filter;
        FilterAndSort(resetPages: true);
    }

    public void SetStateFilter(BlockStateFilter filter)
    {
        if (StateFilter == filter) return;
        StateFilter = filter;
        FilterAndSort(resetPages: true);
    }

    public void SetSort(string column)
    {
        if (string.Equals(SortColumn, column, StringComparison.Ordinal))
            SortDescending = !SortDescending;
        else
        {
            SortColumn = column;
            SortDescending = true;
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
        PageBlocks = Slice(VisibleBlocks, BlockPage);
    }

    public void SetPlayerPage(int page)
    {
        PlayerPage = Math.Clamp(page, 1, PlayerPageCount);
        PagePlayers = Slice(VisiblePlayers, PlayerPage);
    }

    public void ApplyPreferences(UiPreferences preferences)
    {
        PageSize = PageSizes.Contains(preferences.PageSize) ? preferences.PageSize : DefaultPageSize;
        Tab = Enum.IsDefined(preferences.Tab) ? preferences.Tab : LiveTab.Overview;
        StateFilter = Enum.IsDefined(preferences.StateFilter) ? preferences.StateFilter : BlockStateFilter.All;
        SortColumn = string.IsNullOrWhiteSpace(preferences.SortColumn) ? nameof(BlockRowView.LastRunTimeMS) : preferences.SortColumn;
        SortDescending = preferences.SortDescending;
        Filter = preferences.Filter ?? "";

        if (Snapshot is not null)
            FilterAndSort(resetPages: true);
    }

    public UiPreferences ToPreferences()
    {
        return new UiPreferences
        {
            Tab = Tab,
            SortColumn = SortColumn,
            SortDescending = SortDescending,
            Filter = Filter,
            StateFilter = StateFilter,
            PageSize = PageSize
        };
    }

    private void FilterAndSort(bool resetPages)
    {
        if (Snapshot is null)
        {
            VisibleBlocks = Array.Empty<BlockRowView>();
            VisiblePlayers = Array.Empty<PlayerRowView>();
            PageBlocks = Array.Empty<BlockRowView>();
            PagePlayers = Array.Empty<PlayerRowView>();
            TopBlocks = Array.Empty<BlockRowView>();
            TopPlayers = Array.Empty<PlayerRowView>();
            OnCount = OffCount = RestingCount = OffenceBlockCount = 0;
            MaxBlockLast = MaxPlayerCombined = 0;
            return;
        }

        string needle = Filter.Trim();
        bool filtering = needle.Length > 0;

        IEnumerable<BlockRowView> blocks = Snapshot.Blocks;
        if (filtering)
            blocks = blocks.Where(block => Matches(block.BlockName, needle)
                                           || Matches(block.GridName, needle)
                                           || Matches(block.OwnerName, needle));

        blocks = StateFilter switch
        {
            BlockStateFilter.On => blocks.Where(block => block.Enabled && !block.Resting),
            BlockStateFilter.Off => blocks.Where(block => !block.Enabled),
            BlockStateFilter.Resting => blocks.Where(block => block.Resting),
            BlockStateFilter.Offences => blocks.Where(block => block.Offences > 0),
            _ => blocks
        };

        Func<BlockRowView, IComparable> blockKey = SortColumn switch
        {
            nameof(BlockRowView.BlockName) => block => block.BlockName,
            nameof(BlockRowView.GridName) => block => block.GridName,
            nameof(BlockRowView.OwnerName) => block => block.OwnerName,
            nameof(BlockRowView.RunTimeMSAvg) => block => block.RunTimeMSAvg,
            nameof(BlockRowView.PeekRunTimeMS) => block => block.PeekRunTimeMS,
            nameof(BlockRowView.Offences) => block => block.Offences,
            nameof(BlockRowView.Recompiles) => block => block.Recompiles,
            nameof(BlockRowView.MemoryBytes) => block => MemorySortKey(block.MemoryBytes, block.MemoryUnmeasured),
            _ => block => block.LastRunTimeMS
        };

        blocks = SortDescending
            ? blocks.OrderByDescending(blockKey, SameEverywhere)
            : blocks.OrderBy(blockKey, SameEverywhere);

        VisibleBlocks = blocks.ToList();
        MaxBlockLast = VisibleBlocks.Count == 0 ? 0 : VisibleBlocks.Max(block => block.LastRunTimeMS);
        TopBlocks = VisibleBlocks
            .OrderByDescending(block => block.LastRunTimeMS)
            .ThenBy(block => block.BlockName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        OnCount = Snapshot.Blocks.Count(block => block.Enabled && !block.Resting);
        OffCount = Snapshot.Blocks.Count(block => !block.Enabled);
        RestingCount = Snapshot.Blocks.Count(block => block.Resting);
        OffenceBlockCount = Snapshot.Blocks.Count(block => block.Offences > 0);

        IEnumerable<PlayerRowView> players = Snapshot.Players;
        if (filtering)
            players = players.Where(player => Matches(player.Name, needle) || Matches(player.SteamId, needle));

        Func<PlayerRowView, IComparable> playerKey = SortColumn switch
        {
            nameof(PlayerRowView.Name) => player => player.Name,
            nameof(PlayerRowView.SteamId) => player => ulong.TryParse(player.SteamId, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id) ? id : 0UL,
            nameof(PlayerRowView.BlockCount) => player => player.BlockCount,
            nameof(PlayerRowView.CombinedRunTimeMSAvg) => player => player.CombinedRunTimeMSAvg,
            nameof(PlayerRowView.Offences) => player => player.Offences,
            nameof(PlayerRowView.Recompiles) => player => player.Recompiles,
            nameof(PlayerRowView.MemoryBytes) => player => MemorySortKey(player.MemoryBytes, player.MemoryUnmeasured),
            _ => player => player.CombinedLastRunTimeMS
        };

        players = SortDescending
            ? players.OrderByDescending(playerKey, SameEverywhere)
            : players.OrderBy(playerKey, SameEverywhere);

        VisiblePlayers = players.ToList();
        MaxPlayerCombined = VisiblePlayers.Count == 0 ? 0 : VisiblePlayers.Max(player => player.CombinedLastRunTimeMS);
        TopPlayers = VisiblePlayers
            .OrderByDescending(player => player.CombinedLastRunTimeMS)
            .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (resetPages)
            BlockPage = PlayerPage = 1;

        BlockPage = Math.Clamp(BlockPage, 1, BlockPageCount);
        PlayerPage = Math.Clamp(PlayerPage, 1, PlayerPageCount);
        SlicePages();
    }

    private void SlicePages()
    {
        PageBlocks = Slice(VisibleBlocks, BlockPage);
        PagePlayers = Slice(VisiblePlayers, PlayerPage);
    }

    private T[] Slice<T>(IReadOnlyList<T> rows, int page)
    {
        if (rows.Count == 0) return Array.Empty<T>();
        int start = Math.Max(0, (page - 1) * PageSize);
        int count = Math.Min(PageSize, rows.Count - start);
        if (count <= 0) return Array.Empty<T>();

        T[] slice = new T[count];
        for (int index = 0; index < count; index++)
            slice[index] = rows[start + index];
        return slice;
    }

    private static int PageCount(int count, int pageSize)
    {
        if (count <= 0) return 1;
        return (count + pageSize - 1) / pageSize;
    }

    private static bool Matches(string value, string needle) =>
        value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static long MemorySortKey(long bytes, bool unmeasured) => unmeasured ? long.MaxValue : bytes;
}

/// <summary>The last few snapshots this browser tab has seen, for the small trend charts.</summary>
public sealed class SessionHistory
{
    public const int MaxPoints = 120;

    private readonly List<HistoryPoint> _points = new();

    public IReadOnlyList<HistoryPoint> Points => _points;

    public void Add(ReportSnapshotView snapshot)
    {
        _points.Add(new HistoryPoint(
            DateTime.UtcNow,
            snapshot.Players.Count,
            snapshot.Players.Sum(player => player.BlockCount),
            snapshot.Players.Sum(player => player.CombinedLastRunTimeMS),
            snapshot.Players.Sum(player => player.CombinedRunTimeMSAvg),
            snapshot.Players.Sum(player => player.Offences),
            snapshot.Players.Sum(player => player.Recompiles),
            snapshot.Players.Sum(player => player.MemoryBytes)));

        if (_points.Count > MaxPoints)
            _points.RemoveAt(0);
    }

    public IReadOnlyList<double> Values(Func<HistoryPoint, double> selector)
    {
        double[] values = new double[_points.Count];
        for (int index = 0; index < _points.Count; index++)
            values[index] = selector(_points[index]);
        return values;
    }

    public IReadOnlyList<PblTrendSeries> Series()
    {
        return
        [
            new PblTrendSeries("Combined last", "primary", Values(point => point.CombinedLast)),
            new PblTrendSeries("Combined average", "info", Values(point => point.CombinedAverage)),
        ];
    }

    public IReadOnlyList<double> Players => Values(point => point.Players);
    public IReadOnlyList<double> Blocks => Values(point => point.Blocks);
    public IReadOnlyList<double> CombinedLast => Values(point => point.CombinedLast);
    public IReadOnlyList<double> CombinedAverage => Values(point => point.CombinedAverage);
    public IReadOnlyList<double> Offences => Values(point => point.Offences);
    public IReadOnlyList<double> Recompiles => Values(point => point.Recompiles);
    public IReadOnlyList<double> Memory => Values(point => point.Memory);

    public void Clear() => _points.Clear();
}
