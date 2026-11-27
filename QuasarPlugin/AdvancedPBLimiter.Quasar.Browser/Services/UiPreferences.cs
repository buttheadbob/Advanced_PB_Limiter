namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// What the browser remembers between visits. Kept in localStorage, never sent to the server, and only UI choices:
/// no server data or player data is stored here. The Blocks and Players tables keep a filter and a sort each.
/// </summary>
public sealed class UiPreferences
{
    /// <summary>The localStorage key.</summary>
    public const string Key = "ui.v2";

    /// <summary>Version 1 kept one sort and one filter for both tables. It is not read, and is removed.</summary>
    public const string OldKey = "ui.v1";

    public string? ServerKey { get; set; }
    public int RefreshSeconds { get; set; } = 5;
    public LiveTab Tab { get; set; } = LiveTab.Overview;
    public string BlockSortColumn { get; set; } = LiveViewState.DefaultBlockSort;
    public bool BlockSortDescending { get; set; } = true;
    public string PlayerSortColumn { get; set; } = LiveViewState.DefaultPlayerSort;
    public bool PlayerSortDescending { get; set; } = true;
    public string BlockFilter { get; set; } = "";
    public string PlayerFilter { get; set; } = "";
    public BlockStateFilter StateFilter { get; set; } = BlockStateFilter.All;
    public int PageSize { get; set; } = 50;
}
