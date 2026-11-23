using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.QuasarContracts;
using AdvancedPBLimiter.Quasar.Pages;
using ExtensionMethods;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Companion;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// Sends queued profiles to their servers, one request at a time. A server that is off, still loading or not answering
/// keeps its place and is tried again, so an admin can apply a profile to servers that are down.
/// </summary>
public sealed class ProfileQueue : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly LimiterReports _reports;
    private readonly ProfileStore _store;
    private readonly ILogger<ProfileQueue> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    // Only a new reason is logged, per profile and for the pass: a pass or a send that keeps failing would log every few seconds.
    private readonly RecentReasons _logged = new();
    private const string PassKey = "pass";

    public ProfileQueue(LimiterReports reports, ProfileStore store, ILogger<ProfileQueue> log)
    {
        _reports = reports;
        _store = store;
        _log = log;
    }

    /// <summary>Tries now instead of at the next retry.</summary>
    public void Wake() => Signals.Wake(_wake);

    /// <summary>Stops the queue, then saves when each server was last seen, before Quasar disposes the store.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _store.SaveSeenAsync();
    }

    public override void Dispose()
    {
        // Stopped first, so the loop sees its token cancelled before the semaphore it waits on is gone.
        base.Dispose();
        _wake.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _store.Loaded.WaitAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunPassAsync(stoppingToken);
                await _wake.WaitAsync(RetryInterval, stoppingToken);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException && stoppingToken.IsCancellationRequested)
        {
            // Quasar is stopping. A pass still running when the host stops waiting for it finds the semaphore disposed.
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // This loop must outlive one bad pass, or queued profiles would silently never arrive.
            if (!_logged.IsNew(PassKey, e.Message)) return;
            _log.LogError(e, "The PB Limiter profile queue failed a pass");
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<QuasarCompanionServer> connected = _reports.GetServers();
        await _store.ServersSeenAsync(connected);

        foreach (PendingApply pending in _store.GetPending())
        {
            if (pending.Refused)
                continue; // Refused for good by the server; it waits to be cancelled.
            if (connected.All(s => s.ServerId != pending.ServerId))
            {
                await _store.FailPendingAsync(pending.Id, "Waiting for the server to connect.");
                continue;
            }

            // This pass took its copy earlier; the admin may have cancelled or replaced it, or a page moved it to a new
            // incarnation of its cluster node, while another server was being sent to. The claim settles that: from here a
            // cancel is refused, a request gone or moved is skipped until a pass sees its server connected, and what is sent
            // is the request as claimed.
            PendingApply? claimed = await _store.TryStartSendingAsync(pending.Id, pending.ServerId);
            if (claimed != null)
                await SendAsync(claimed, stoppingToken);
        }
    }

    private async Task SendAsync(PendingApply pending, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            ProfileData applied = await _reports.ApplyProfileAsync(pending.ServerId, pending.SettingsJson, pending.Id.ToString("N"), RequestTimeout, timeout.Token);
            AnswerOutcome outcome = applied.Refused is { } refused
                ? await _store.RefusePendingAsync(pending, refused)
                : await _store.CompletePendingAsync(pending, applied.Settings, applied.PrivilegedPlayers);
            _logged.Forget(pending.Id.ToString());
            if (outcome == AnswerOutcome.Moved)
                MovedOnTheWay(pending);
            else
                LogAnswer(pending, applied, outcome);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The 1.3.3 companion does not start it after the deadline it carries, as far as the two clocks agree, and one it
            // started in time can finish after this stopped waiting. It is sent again, and that companion answers the repeat
            // with the first answer rather than applying it twice.
            await _store.TimedOutAsync(pending.Id, "The server did not answer in time; it is sent again, which a server running the 1.3.3 companion applies at most once while it keeps running.");
        }
        catch (Exception e) when (CompanionErrors.MayHaveArrived(e))
        {
            // Sent, but the agent dropped before it answered: the server may have applied it, so it is handled as a timeout.
            await _store.TimedOutAsync(pending.Id, "The server's connection dropped before it answered; it is sent again, which a server running the 1.3.3 companion applies at most once while it keeps running.");
        }
        catch (Exception e)
        {
            await _store.StopSendingAsync(pending.Id);
            // Tried again every few seconds, so only a new reason is worth a log line. Decided apart from the reason the page
            // shows, which a timeout in between replaces.
            await _store.FailPendingAsync(pending.Id, e.Message + CompanionErrors.RestartHint(e));
            if (!_logged.IsNew(pending.Id.ToString(), e.Message)) return;
            // A profile or server name never reaches a log line unsanitized, so one cannot forge or confuse a line.
            string profile = pending.ProfileName.SafeName();
            string server = pending.ServerName.SafeName();
            string reason = e.Message.SafeName();
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("PB Limiter profile {Profile} is still waiting for {Server}: {Reason}", profile, server, reason);
            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug(e, "Why PB Limiter profile {Profile} is waiting", profile);
        }
    }

    /// <summary>
    /// Logs a server's answer that was recorded, that came after its server was forgotten (Gone: a request on its way is
    /// never replaced, so nothing else makes it Gone), or that came as Quasar stopped the store.
    /// </summary>
    private void LogAnswer(PendingApply pending, ProfileData applied, AnswerOutcome outcome)
    {
        if (!_log.IsEnabled(LogLevel.Information)) return;
        string profile = pending.ProfileName.SafeName();
        string server = pending.ServerName.SafeName();
        string queuedBy = pending.QueuedBy.SafeName();
        if (outcome == AnswerOutcome.Stopping)
        {
            _log.LogInformation("{Server} answered PB Limiter profile {Profile} as Quasar was stopping, so the answer is not recorded and the request is left as last saved",
                server, profile);
            return;
        }
        bool gone = outcome == AnswerOutcome.Gone;
        if (applied.Refused is { } refused)
        {
            string reason = refused.SafeName();
            if (gone)
                _log.LogInformation("{Server} refused PB Limiter profile {Profile}, but the server was forgotten meanwhile, so nothing is marked: {Reason}",
                    server, profile, reason);
            else
                _log.LogInformation("{Server} refused PB Limiter profile {Profile}, it is not sent again: {Reason}", server, profile, reason);
        }
        else if (gone)
        {
            _log.LogInformation("{Server} applied PB Limiter profile {Profile}, but the server was forgotten meanwhile, so it is not recorded: {Settings}, {Players}",
                server, profile, InvariantText.Count(applied.Settings, "setting", "settings"), InvariantText.Count(applied.PrivilegedPlayers, "privileged player", "privileged players"));
        }
        else
        {
            _log.LogInformation("{User} applied PB Limiter profile {Profile} to {Server}: {Settings}, {Players}",
                queuedBy, profile, server, InvariantText.Count(applied.Settings, "setting", "settings"), InvariantText.Count(applied.PrivilegedPlayers, "privileged player", "privileged players"));
        }
    }

    /// <summary>
    /// The request was moved to a new incarnation of its cluster node while on its way, so the answer came from a node that
    /// is gone. It waits for the new node, which is tried now rather than at the next retry.
    /// </summary>
    private void MovedOnTheWay(PendingApply sent)
    {
        Wake();
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("{Server} was replaced by a new node while PB Limiter profile {Profile} was on its way, the profile now waits for that node",
                sent.ServerName, sent.ProfileName);
    }
}
