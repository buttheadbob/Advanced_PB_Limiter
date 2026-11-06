using Advanced_PB_Limiter.QuasarContracts;
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

    public ProfileQueue(LimiterReports reports, ProfileStore store, ILogger<ProfileQueue> log)
    {
        _reports = reports;
        _store = store;
        _log = log;
    }

    /// <summary>Tries now instead of at the next retry.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already scheduled.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await _wake.WaitAsync(RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // This loop must outlive one bad pass, or queued profiles would silently never arrive.
                _log.LogError(e, "The PB Limiter profile queue failed a pass");
                await Task.Delay(RetryInterval, stoppingToken);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<QuasarCompanionServer> connected = _reports.GetServers();
        _store.ServersSeen(connected);

        foreach (PendingApply pending in _store.Pending)
        {
            if (connected.All(s => s.ServerId != pending.ServerId))
            {
                _store.FailPending(pending.Id, "Waiting for the server to connect.");
                continue;
            }

            await SendAsync(pending, stoppingToken);
        }
    }

    private async Task SendAsync(PendingApply pending, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            ProfileData applied = await _reports.ApplyProfileAsync(pending.ServerId, pending.SettingsJson, timeout.Token);
            _store.CompletePending(pending, applied.Settings, applied.PrivilegedPlayers);
            _log.LogInformation("{User} applied PB Limiter profile {Profile} to {Server}: {Settings} settings, {Players} privileged players",
                pending.QueuedBy, pending.ProfileName, pending.ServerName, applied.Settings, applied.PrivilegedPlayers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _store.FailPending(pending.Id, "The server did not answer in time.");
        }
        catch (Exception e)
        {
            // Tried again every few seconds, so only a new reason is worth a log line.
            if (_store.FailPending(pending.Id, e.Message))
                _log.LogInformation("PB Limiter profile {Profile} is still waiting for {Server}: {Reason}", pending.ProfileName, pending.ServerName, e.Message);
        }
    }
}
