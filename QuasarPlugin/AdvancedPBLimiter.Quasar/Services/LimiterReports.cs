using Advanced_PB_Limiter.QuasarContracts;
using Quasar.Plugin.Abstractions.Companion;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>Asks a server's companion for its report, and to turn blocks off.</summary>
public sealed class LimiterReports
{
    private readonly IQuasarCompanionChannel _channel;

    public LimiterReports(IQuasarCompanionChannel channel)
    {
        _channel = channel;
    }

    /// <summary>Connected servers. A cluster shows each node on its own, since a request can only go to one node.</summary>
    public IReadOnlyList<QuasarCompanionServer> GetServers()
    {
        return _channel.GetConnectedServers().OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<ReportSnapshot> GetSnapshotAsync(string serverId, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<EmptyPayload, ReportSnapshot>(serverId, ReportOperations.CompanionId,
            ReportOperations.Snapshot, new EmptyPayload(), cancellationToken);
    }

    public Task<TurnOffResult> TurnOffBlockAsync(string serverId, long entityId, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<TurnOffBlockRequest, TurnOffResult>(serverId, ReportOperations.CompanionId,
            ReportOperations.TurnOffBlock, new TurnOffBlockRequest { EntityId = entityId }, cancellationToken);
    }

    public Task<TurnOffResult> TurnOffPlayerAsync(string serverId, long identityId, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<TurnOffPlayerRequest, TurnOffResult>(serverId, ReportOperations.CompanionId,
            ReportOperations.TurnOffPlayer, new TurnOffPlayerRequest { IdentityId = identityId }, cancellationToken);
    }

    public Task<ProfileData> ReadProfileAsync(string serverId, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<EmptyPayload, ProfileData>(serverId, ReportOperations.CompanionId,
            ReportOperations.ProfileRead, new EmptyPayload(), cancellationToken);
    }

    public Task<ProfileData> ApplyProfileAsync(string serverId, string settingsJson, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<ProfileApplyRequest, ProfileData>(serverId, ReportOperations.CompanionId,
            ReportOperations.ProfileApply, new ProfileApplyRequest { SettingsJson = settingsJson }, cancellationToken);
    }
}
