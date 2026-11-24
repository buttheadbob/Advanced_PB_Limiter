namespace AdvancedPBLimiter.Quasar.Services;

public sealed record PblTab(string Key, string Label, string Icon, int? Count = null);

public sealed record PblStateChip(string Label, BlockStateFilter Filter, int Count);
