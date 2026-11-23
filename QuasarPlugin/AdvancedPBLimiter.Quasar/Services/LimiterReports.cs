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
    public List<QuasarCompanionServer> GetServers()
    {
        return _channel.GetConnectedServers().OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<ReportSnapshot> GetSnapshotAsync(string serverId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<SnapshotRequest, ReportSnapshot>(serverId, ReportOperations.CompanionId,
            ReportOperations.Snapshot, new SnapshotRequest { TimeoutMs = Deadline(timeout), ExpiresUnixMs = Expires(timeout) }, cancellationToken);
    }

    // The requests carry a deadline shorter than the caller's timeout, counted from when the server receives them, so the
    // companion refuses work while Quasar still waits for the answer. The request's time on the way, which can include
    // waiting on the agent behind another request, is covered by the expiry on Quasar's clock sent with it.

    public Task<TurnOffResult> TurnOffBlockAsync(string serverId, long entityId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<TurnOffBlockRequest, TurnOffResult>(serverId, ReportOperations.CompanionId,
            ReportOperations.TurnOffBlock, new TurnOffBlockRequest { EntityId = entityId, TimeoutMs = Deadline(timeout), ExpiresUnixMs = Expires(timeout) }, cancellationToken);
    }

    public Task<TurnOffResult> TurnOffPlayerAsync(string serverId, long identityId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<TurnOffPlayerRequest, TurnOffResult>(serverId, ReportOperations.CompanionId,
            ReportOperations.TurnOffPlayer, new TurnOffPlayerRequest { IdentityId = identityId, TimeoutMs = Deadline(timeout), ExpiresUnixMs = Expires(timeout) }, cancellationToken);
    }

    public Task<ProfileData> ReadProfileAsync(string serverId, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<object, ProfileData>(serverId, ReportOperations.CompanionId,
            ReportOperations.ProfileRead, new object(), cancellationToken);
    }

    /// <param name="requestId">The queue entry, the same on every send of it: see <see cref="ProfileApplyRequest.RequestId"/>.</param>
    public Task<ProfileData> ApplyProfileAsync(string serverId, string settingsJson, string requestId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _channel.SendAsync<ProfileApplyRequest, ProfileData>(serverId, ReportOperations.CompanionId,
            ReportOperations.ProfileApply, new ProfileApplyRequest { SettingsJson = settingsJson, RequestId = requestId, TimeoutMs = Deadline(timeout), ExpiresUnixMs = Expires(timeout) }, cancellationToken);
    }

    /// <summary>The timeout less a margin for the way there and back: a third of it, at most 5 seconds. At least 1 ms, as 0 means no limit.</summary>
    private static int Deadline(TimeSpan timeout)
    {
        double margin = Math.Min(timeout.TotalMilliseconds / 3, 5000);
        return (int)Math.Clamp(timeout.TotalMilliseconds - margin, 1, int.MaxValue);
    }

    /// <summary>
    /// When Quasar stops waiting, less a second for the answer's way back, as Unix milliseconds on Quasar's clock, and
    /// never before the deadline: see <see cref="ProfileApplyRequest.ExpiresUnixMs"/>.
    /// </summary>
    private static long Expires(TimeSpan timeout) =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(Deadline(timeout), (long)timeout.TotalMilliseconds - 1000);
}
