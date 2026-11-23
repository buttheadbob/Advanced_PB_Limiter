using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using AdvancedPBLimiter.Quasar.Services;
using ExtensionMethods;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// The browser SDK operations the Quasar 1.7 pages call. The pages run in the browser; the companion channel and the
/// profile store stay in the worker, so every request is authorized and audited here.
/// </summary>
internal static class QuasarUiOperations
{
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TurnOffTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProfileReadTimeout = TimeSpan.FromSeconds(20);

    public static void Register(IServiceCollection services, QuasarPluginContext context)
    {
        services.AddQuasarOperation<EmptyPayload, ServerListResponse>(
            context.PluginId, UiOperations.Servers, [QuasarPolicyNames.CanView],
            (caller, _, token) => ServersAsync(caller, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Description = "Lists the servers connected to Quasar and those seen before, with their last seen time."
            });

        services.AddQuasarOperation<ReadSnapshotRequest, ReportSnapshot>(
            context.PluginId, UiOperations.Snapshot, [QuasarPolicyNames.CanView],
            (caller, request, token) => SnapshotAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Description = "Reads the live programmable-block report of one connected server."
            });

        services.AddQuasarOperation<TurnOffBlockUiRequest, TurnOffResult>(
            context.PluginId, UiOperations.TurnOffBlock, [QuasarPolicyNames.CanControlServers],
            (caller, request, token) => TurnOffBlockAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Description = "Turns off one tracked programmable block on a connected server."
            });

        services.AddQuasarOperation<TurnOffPlayerUiRequest, TurnOffResult>(
            context.PluginId, UiOperations.TurnOffPlayer, [QuasarPolicyNames.CanControlServers],
            (caller, request, token) => TurnOffPlayerAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Description = "Turns off every tracked programmable block of one player on a connected server."
            });

        services.AddQuasarOperation<EmptyPayload, ProfilesStateResponse>(
            context.PluginId, UiOperations.ProfilesState, [QuasarPolicyNames.CanEditConfigs],
            (caller, _, token) => ProfilesStateAsync(caller, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Description = "Lists the saved limiter profiles, the queued applies and the known servers."
            });

        services.AddQuasarOperation<ProfileSaveRequest, ProfileActionResult>(
            context.PluginId, UiOperations.ProfileSave, [QuasarPolicyNames.CanEditConfigs],
            (caller, request, token) => SaveProfileAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Description = "Reads a connected server's limiter settings and saves them as a named profile."
            });

        services.AddQuasarOperation<ProfileNameRequest, ProfileActionResult>(
            context.PluginId, UiOperations.ProfileDelete, [QuasarPolicyNames.CanEditConfigs],
            (caller, request, token) => DeleteProfileAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Description = "Deletes a saved limiter profile. Applies already queued still go ahead."
            });

        services.AddQuasarOperation<ProfileQueueRequest, ProfileActionResult>(
            context.PluginId, UiOperations.ProfileQueue, [QuasarPolicyNames.CanEditConfigs],
            (caller, request, token) => QueueProfileAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Description = "Queues a saved profile to be applied to the chosen servers, including ones that are offline now."
            });

        services.AddQuasarOperation<ProfileCancelRequest, ProfileActionResult>(
            context.PluginId, UiOperations.ProfileCancel, [QuasarPolicyNames.CanEditConfigs],
            (caller, request, token) => CancelPendingAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Description = "Cancels a queued profile apply that has not started sending."
            });

        services.AddQuasarOperation<ProfileForgetRequest, ProfileActionResult>(
            context.PluginId, UiOperations.ProfileForget, [QuasarPolicyNames.CanEditConfigs],
            (caller, request, token) => ForgetServerAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Description = "Forgets an offline server and drops anything waiting to apply to it."
            });
    }

    private static async Task<ServerListResponse> ServersAsync(QuasarOperationContext caller, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        List<QuasarCompanionServer> connected = reports.GetServers();
        token.ThrowIfCancellationRequested();
        _ = store.RecordSeenAsync(connected);
        return new ServerListResponse { Servers = ServerInfos(store, connected) };
    }

    private static async Task<ProfilesStateResponse> ProfilesStateAsync(QuasarOperationContext caller, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        List<QuasarCompanionServer> connected = reports.GetServers();
        token.ThrowIfCancellationRequested();
        _ = store.RecordSeenAsync(connected);
        return State(store, connected);
    }

    private static async Task<ReportSnapshot> SnapshotAsync(QuasarOperationContext caller, ReadSnapshotRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SnapshotTimeout);
        return await reports.GetSnapshotAsync(request.ServerId ?? "", SnapshotTimeout, timeout.Token);
    }

    private static async Task<TurnOffResult> TurnOffBlockAsync(QuasarOperationContext caller, TurnOffBlockUiRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        if (!long.TryParse(request.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long entityId))
            throw new ArgumentException("The block id is not a number.", nameof(request));
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TurnOffTimeout);
        return await reports.TurnOffBlockAsync(request.ServerId ?? "", entityId, TurnOffTimeout, timeout.Token);
    }

    private static async Task<TurnOffResult> TurnOffPlayerAsync(QuasarOperationContext caller, TurnOffPlayerUiRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        if (!long.TryParse(request.IdentityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long identityId))
            throw new ArgumentException("The player id is not a number.", nameof(request));
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TurnOffTimeout);
        return await reports.TurnOffPlayerAsync(request.ServerId ?? "", identityId, TurnOffTimeout, timeout.Token);
    }

    private static async Task<ProfileActionResult> SaveProfileAsync(QuasarOperationContext caller, ProfileSaveRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        string name = (request.Name ?? "").Trim();
        string serverId = request.ServerId ?? "";
        if (name.Length == 0)
            return Unchanged("A profile needs a name.", store, reports);
        if (!name.IsSafeProfileName())
            return Unchanged("The profile name must be 1 to 64 characters, with no control or hidden formatting characters.", store, reports);

        token.ThrowIfCancellationRequested();
        string serverName = reports.GetServers().FirstOrDefault(s => s.ServerId == serverId)?.DisplayName ?? serverId;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProfileReadTimeout);
        ProfileData data = await reports.ReadProfileAsync(serverId, timeout.Token);
        token.ThrowIfCancellationRequested();
        StoreResult saved = await store.SaveProfileAsync(new SavedProfile
        {
            Name = name,
            SavedFrom = serverName,
            SavedUtc = DateTime.UtcNow,
            SettingsJson = data.SettingsJson,
            Settings = data.Settings,
            PrivilegedPlayers = data.PrivilegedPlayers
        });

        string summary = InvariantText.Count(data.Settings, "setting", "settings") + ", " +
                         InvariantText.Count(data.PrivilegedPlayers, "privileged player", "privileged players") + ".";
        string message = saved.Changed
            ? $"Saved {name.SafeName()} from {serverName.SafeName()}: {summary}"
            : $"{name.SafeName()} was not saved.";
        return Result(saved, message, store, reports);
    }

    private static async Task<ProfileActionResult> DeleteProfileAsync(QuasarOperationContext caller, ProfileNameRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        string requested = request.Name ?? "";
        token.ThrowIfCancellationRequested();
        StoreResult result = await store.DeleteProfileAsync(requested);
        string name = requested.SafeName();
        string message = result.Changed
            ? $"Deleted {name}. Anything already waiting to apply still goes ahead."
            : $"{name} was already deleted.";
        return Result(result, message, store, reports);
    }

    private static async Task<ProfileActionResult> QueueProfileAsync(QuasarOperationContext caller, ProfileQueueRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        ProfileQueue queue = caller.Services.GetRequiredService<ProfileQueue>();

        string requested = request.ProfileName ?? "";
        SavedProfile? profile = store.GetProfiles().FirstOrDefault(p => string.Equals(p.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            return Unchanged($"{requested.SafeName()} was deleted meanwhile, so it was not queued.", store, reports);

        // A server connected now but not listed yet is recorded first, so it can be queued for as well.
        token.ThrowIfCancellationRequested();
        await store.RecordSeenAsync(reports.GetServers());
        List<string> keys = request.ServerKeys is { Count: > 0 }
            ? request.ServerKeys
            : (request.ServerIds ?? new List<string>()).Select(id => ServerPickKeys.For("", "", id)).ToList();
        List<KnownServer> servers = store.ResolveKnown(keys);
        if (servers.Count == 0)
            return Unchanged($"{profile.Name.SafeName()} was not queued: the chosen servers were forgotten.", store, reports);

        string user = caller.User.Identity?.Name ?? "unknown";
        token.ThrowIfCancellationRequested();
        QueueResult queued = await store.QueueAsync(profile, servers, user);
        if (queued.Result.Changed)
            queue.Wake();

        string name = profile.Name.SafeName();
        string message = queued.Result.Changed
            ? $"Applying {name} to {InvariantText.Count(queued.Servers, "server", "servers")}. Progress is shown below."
            : $"{name} was not queued: the chosen servers were forgotten.";
        return Result(queued.Result, message, store, reports);
    }

    private static async Task<ProfileActionResult> CancelPendingAsync(QuasarOperationContext caller, ProfileCancelRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        PendingApply? pending = store.GetPending().Find(p => p.Id == request.Id);
        token.ThrowIfCancellationRequested();
        CancelResult result = await store.CancelPendingAsync(request.Id);

        string name = (pending?.ProfileName ?? "The profile").SafeName();
        string server = (pending?.ServerName ?? "the server").SafeName();
        string message = result.Outcome switch
        {
            CancelOutcome.Cancelled => pending?.MayBeApplied == true
                ? $"Cancelled {name} for {server}. An earlier send may already have applied it."
                : $"Cancelled {name} for {server}.",
            CancelOutcome.Sending => $"{name} for {server} is already being sent, so it can no longer be cancelled.",
            CancelOutcome.Stopping => $"Quasar is stopping, so {name} for {server} was not cancelled.",
            _ => $"{name} for {server} was no longer waiting: it was applied, cancelled or replaced already, or its server forgotten."
        };
        return new ProfileActionResult
        {
            Changed = result.Outcome == CancelOutcome.Cancelled,
            Saved = result.Saved,
            Message = message,
            State = State(store, reports.GetServers())
        };
    }

    private static async Task<ProfileActionResult> ForgetServerAsync(QuasarOperationContext caller, ProfileForgetRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        string serverId = request.ServerId ?? "";
        KnownServer? known = store.GetKnownServers().Find(s => s.ServerId == serverId);
        string name = (known?.DisplayName ?? serverId).SafeName();

        if (reports.GetServers().Any(s => s.ServerId == serverId))
            return Unchanged($"{name} is connected again, so it was not forgotten.", store, reports);

        token.ThrowIfCancellationRequested();
        StoreResult result = await store.ForgetServerAsync(serverId);
        string message = result.Changed
            ? $"Forgot {name} and anything waiting for it."
            : $"{name} was already forgotten.";
        return Result(result, message, store, reports);
    }

    private static ProfileActionResult Result(StoreResult result, string message, ProfileStore store, LimiterReports reports) => new()
    {
        Changed = result.Changed,
        Saved = result.Saved,
        Message = message,
        State = State(store, reports.GetServers())
    };

    private static ProfileActionResult Unchanged(string message, ProfileStore store, LimiterReports reports) => new()
    {
        Changed = false,
        Saved = true,
        Message = message,
        State = State(store, reports.GetServers())
    };

    private static ProfilesStateResponse State(ProfileStore store, List<QuasarCompanionServer> connected) => new()
    {
        CanSave = store.CanSave,
        // The settings themselves stay in the worker: they are large, and no page reads them from this poll.
        Profiles = store.GetProfiles().Select(profile => new SavedProfileRow
        {
            Name = profile.Name,
            SavedFrom = profile.SavedFrom,
            SavedUtc = profile.SavedUtc,
            Settings = profile.Settings,
            PrivilegedPlayers = profile.PrivilegedPlayers
        }).ToList(),
        Pending = store.GetPending().Select(pending => new PendingApplyRow
        {
            Id = pending.Id,
            ServerId = pending.ServerId,
            ServerName = pending.ServerName,
            ProfileName = pending.ProfileName,
            QueuedBy = pending.QueuedBy,
            QueuedUtc = pending.QueuedUtc,
            Sequence = pending.Sequence,
            LastError = pending.LastError,
            Sending = pending.Sending,
            Refused = pending.Refused,
            MayBeApplied = pending.MayBeApplied
        }).ToList(),
        Applied = store.GetApplied(),
        KnownServers = store.GetKnownServers(),
        ConnectedServers = ServerInfos(store, connected).Where(s => s.Connected).ToList()
    };

    private static List<ServerInfo> ServerInfos(ProfileStore store, List<QuasarCompanionServer> connected)
    {
        Dictionary<string, ServerInfo> byId = new(StringComparer.Ordinal);
        foreach (KnownServer known in store.GetKnownServers())
        {
            byId[known.ServerId] = new ServerInfo
            {
                ServerId = known.ServerId,
                DisplayName = known.DisplayName,
                ClusterId = known.ClusterId,
                ClusterSlot = known.ClusterSlot,
                Known = true,
                LastSeenUtc = store.LastSeenUtc(known)
            };
        }

        foreach (QuasarCompanionServer server in connected)
        {
            if (!byId.TryGetValue(server.ServerId, out ServerInfo? info))
            {
                info = new ServerInfo { ServerId = server.ServerId };
                byId[server.ServerId] = info;
            }
            info.DisplayName = server.DisplayName;
            info.ClusterId = server.ClusterId;
            info.ClusterSlot = server.ClusterSlot;
            info.Connected = true;
            info.LastSeenUtc = DateTime.UtcNow;
        }

        return byId.Values.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
