namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// What the browser remembers between visits. Kept in localStorage, never sent to the server, and only UI choices:
/// no server data or player data is stored here.
/// </summary>
public sealed class UiPreferences
{
    public string? ServerKey { get; set; }
    public int RefreshSeconds { get; set; } = 5;
    public LiveTab Tab { get; set; } = LiveTab.Overview;
    public string SortColumn { get; set; } = "LastRunTimeMS";
    public bool SortDescending { get; set; } = true;
    public string Filter { get; set; } = "";
    public BlockStateFilter StateFilter { get; set; } = BlockStateFilter.All;
    public int PageSize { get; set; } = 50;
}
