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
    private static readonly TimeSpan HistoryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TurnOffTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProfileReadTimeout = TimeSpan.FromSeconds(20);

    private const string ClusterNodeRefusal = "Advanced PB Limiter does not run on Quasar cluster nodes, so a profile can't be saved from or applied to one.";

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

        services.AddQuasarOperation<ReadSnapshotRequest, ReportSnapshotView>(
            context.PluginId, UiOperations.Snapshot, [QuasarPolicyNames.CanView],
            (caller, request, token) => SnapshotAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Description = "Reads the live programmable-block report of one connected server."
            });

        services.AddQuasarOperation<BlockHistoryUiRequest, BlockHistoryView>(
            context.PluginId, UiOperations.BlockHistory, [QuasarPolicyNames.CanView],
            (caller, request, token) => BlockHistoryAsync(caller, request, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Description = "Reads the last 50 run times of up to 100 tracked programmable blocks of one connected server, with their limits."
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

        services.AddQuasarOperation<ProfileNameRequest, ProfileViewResponse?>(
            context.PluginId, UiOperations.ProfileView, [QuasarPolicyNames.CanEditConfigs],
            (caller, request, token) => Task.FromResult(ViewProfile(caller, request, token)),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Description = "Shows the settings and privileged players a saved limiter profile holds."
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

    private static async Task<ReportSnapshotView> SnapshotAsync(QuasarOperationContext caller, ReadSnapshotRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SnapshotTimeout);
        ReportSnapshot snapshot = await reports.GetSnapshotAsync(request.ServerId ?? "", SnapshotTimeout, timeout.Token);
        return View(snapshot);
    }

    /// <summary>Maps the server's numeric ids to the browser's decimal strings.</summary>
    private static ReportSnapshotView View(ReportSnapshot snapshot) => new()
    {
        Installed = snapshot.Installed,
        Online = snapshot.Online,
        Inactive = snapshot.Inactive,
        Enabled = snapshot.Enabled,
        Version = snapshot.Version,
        Truncated = snapshot.Truncated,
        Players = snapshot.Players.ConvertAll(player => new PlayerRowView
        {
            IdentityId = player.IdentityId.ToString(CultureInfo.InvariantCulture),
            SteamId = player.SteamId.ToString(CultureInfo.InvariantCulture),
            Name = player.Name,
            IsNpc = player.IsNpc,
            IsPrivileged = player.IsPrivileged,
            BlockCount = player.BlockCount,
            CombinedLastRunTimeMS = player.CombinedLastRunTimeMS,
            CombinedRunTimeMSAvg = player.CombinedRunTimeMSAvg,
            Offences = player.Offences,
            Recompiles = player.Recompiles,
            MemoryBytes = player.MemoryBytes,
            MemoryUnmeasured = player.MemoryUnmeasured
        }),
        Blocks = snapshot.Blocks.ConvertAll(block => new BlockRowView
        {
            EntityId = block.EntityId.ToString(CultureInfo.InvariantCulture),
            OwnerIdentityId = block.OwnerIdentityId.ToString(CultureInfo.InvariantCulture),
            OwnerName = block.OwnerName,
            GridName = block.GridName,
            BlockName = block.BlockName,
            Enabled = block.Enabled,
            LastRunTimeMS = block.LastRunTimeMS,
            RunTimeMSAvg = block.RunTimeMSAvg,
            PeekRunTimeMS = block.PeekRunTimeMS,
            Offences = block.Offences,
            Recompiles = block.Recompiles,
            MemoryBytes = block.MemoryBytes,
            MemoryUnmeasured = block.MemoryUnmeasured,
            Resting = block.Resting
        })
    };

    private static async Task<BlockHistoryView> BlockHistoryAsync(QuasarOperationContext caller, BlockHistoryUiRequest request, CancellationToken token)
    {
        LimiterReports reports = caller.Services.GetRequiredService<LimiterReports>();
        List<string> ids = request.EntityIds ?? [];
        List<string> owners = request.OwnerIds ?? [];
        List<long> entityIds = new();
        List<long> ownerIds = new();
        for (int index = 0; index < ids.Count && index < BlockHistoryResult.MaxBlocks; index++)
        {
            if (!long.TryParse(ids[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long entityId))
                throw new ArgumentException("A block id is not a number.", nameof(request));
            entityIds.Add(entityId);
            // An owner that is missing or not a number only costs the server a search for the block.
            ownerIds.Add(index < owners.Count && long.TryParse(owners[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ownerId) ? ownerId : 0);
        }
        if (entityIds.Count == 0)
            return new BlockHistoryView();

        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HistoryTimeout);
        BlockHistoryResult result = await reports.GetBlockHistoryAsync(request.ServerId ?? "", entityIds, ownerIds, timeout.Token);
        return new BlockHistoryView
        {
            Blocks = result.Blocks.ConvertAll(block => new BlockHistoryRowView
            {
                EntityId = block.EntityId.ToString(CultureInfo.InvariantCulture),
                RunTimesMS = block.RunTimesMS,
                LeftOutOfAverage = block.LeftOutOfAverage,
                Outcomes = block.Outcomes,
                AverageMS = block.AverageMS,
                RunTimeLimitMS = block.RunTimeLimitMS,
                AverageLimitMS = block.AverageLimitMS,
                InstantKillMS = block.InstantKillMS,
                InstantKillStrikes = block.InstantKillStrikes,
                InstantKillStrikeWindow = block.InstantKillStrikeWindow
            })
        };
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
        QuasarCompanionServer? source = reports.GetServers().FirstOrDefault(s => s.ServerId == serverId);
        if (!string.IsNullOrEmpty(source?.ClusterId))
            return Unchanged(ClusterNodeRefusal, store, reports);
        string serverName = source?.DisplayName ?? serverId;
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

    /// <summary>The profile's settings as name and value, read from the JSON the server exported, with no server asked.</summary>
    private static ProfileViewResponse? ViewProfile(QuasarOperationContext caller, ProfileNameRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ProfileStore store = caller.Services.GetRequiredService<ProfileStore>();
        SavedProfile? profile = store.GetProfiles().Find(p => string.Equals(p.Name, request.Name, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return null;

        ProfileViewResponse view = new() { Name = profile.Name, SavedFrom = profile.SavedFrom, SavedUtc = profile.SavedUtc };
        try
        {
            // Null only in a profiles file edited by hand: read as empty, which then fails as unreadable.
            using System.Text.Json.JsonDocument settings = System.Text.Json.JsonDocument.Parse(profile.SettingsJson ?? "");
            if (settings.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new QuasarOperationException(422, Unreadable);
            using System.Text.Json.JsonElement.ObjectEnumerator properties = settings.RootElement.EnumerateObject();
            while (properties.MoveNext())
            {
                System.Text.Json.JsonProperty setting = properties.Current;
                if (setting.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    AddPlayers(view.PrivilegedPlayers, setting.Value);
                else
                    view.Settings.Add(new ProfileSettingView { Name = setting.Name, Value = Text(setting.Value) });
            }
        }
        catch (System.Text.Json.JsonException)
        {
            throw new QuasarOperationException(422, Unreadable);
        }
        return view;
    }

    // The page names the profile before this.
    private const string Unreadable = "its saved settings can't be read. Save it again from a server.";

    /// <summary>Each privileged player as "Name (SteamId)", or whichever of the two is set; a row with neither is left out.</summary>
    private static void AddPlayers(List<string> players, System.Text.Json.JsonElement rows)
    {
        using System.Text.Json.JsonElement.ArrayEnumerator entries = rows.EnumerateArray();
        while (entries.MoveNext())
        {
            System.Text.Json.JsonElement entry = entries.Current;
            if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            string name = entry.TryGetProperty("Name", out System.Text.Json.JsonElement n) ? Text(n) : "";
            string steamId = entry.TryGetProperty("SteamId", out System.Text.Json.JsonElement s) ? Text(s) : "";
            if (name.Length == 0)
            {
                if (steamId.Length > 0) players.Add(steamId);
            }
            else
                players.Add(steamId.Length == 0 ? name : $"{name} ({steamId})");
        }
    }

    /// <summary>A value as text: a string without its quotes, null as nothing, anything else as written.</summary>
    private static string Text(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => "",
        _ => value.GetRawText()
    };

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
        List<QuasarCompanionServer> connected = reports.GetServers();
        await store.RecordSeenAsync(connected);
        List<string> keys = request.ServerKeys is { Count: > 0 }
            ? new List<string>(request.ServerKeys)
            : (request.ServerIds ?? new List<string>()).ConvertAll(id => ServerPickKeys.For("", "", id));
        // The page lists no cluster nodes, and the store keeps none; a request from an older page, or one made by hand, may
        // still name one, by its cluster slot or by the id of a node connected now.
        HashSet<string> nodeIds = connected.Where(server => !string.IsNullOrEmpty(server.ClusterId)).Select(server => server.ServerId).ToHashSet(StringComparer.Ordinal);
        int clusterNodes = keys.RemoveAll(key => ServerPickKeys.TryCluster(key, out _, out _) || nodeIds.Contains(ServerPickKeys.ServerIdOf(key)));
        List<KnownServer> servers = store.ResolveKnown(keys);
        if (servers.Count == 0)
            return Unchanged(clusterNodes > 0
                ? $"{profile.Name.SafeName()} was not queued. {ClusterNodeRefusal}"
                : $"{profile.Name.SafeName()} was not queued: none of the chosen servers is known any more.", store, reports);

        string user = caller.User.Identity?.Name ?? "unknown";
        token.ThrowIfCancellationRequested();
        QueueResult queued = await store.QueueAsync(profile, servers, user);
        if (queued.Result.Changed)
            queue.Wake();

        string name = profile.Name.SafeName();
        string leftOut = clusterNodes > 0
            ? $" {InvariantText.Count(clusterNodes, "cluster node was", "cluster nodes were")} left out: Advanced PB Limiter does not run on them."
            : "";
        string message = queued.Result.Changed
            ? $"Applying {name} to {InvariantText.Count(queued.Servers, "server", "servers")}. Progress is shown below.{leftOut}"
            : $"{name} was not queued: none of the chosen servers is known any more.";
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
            _ when pending is null => "Nothing to cancel: it already finished or was removed.",
            CancelOutcome.Cancelled when pending.Refused => $"Dismissed {name} for {server}.",
            CancelOutcome.Cancelled => pending.MayBeApplied
                ? $"Cancelled {name} for {server}. An earlier send may already have applied it."
                : $"Cancelled {name} for {server}.",
            CancelOutcome.Sending => $"{name} for {server} is already being sent, so it can no longer be cancelled.",
            CancelOutcome.Stopping => $"Quasar is stopping, so {name} for {server} was not cancelled.",
            _ => $"Nothing to cancel for {name} on {server}: it already finished or was removed."
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

        if (reports.GetServers().Exists(s => s.ServerId == serverId))
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
        Profiles = store.GetProfiles().ConvertAll(profile => new SavedProfileRow
        {
            Name = profile.Name,
            SavedFrom = profile.SavedFrom,
            SavedUtc = profile.SavedUtc,
            Settings = profile.Settings,
            PrivilegedPlayers = profile.PrivilegedPlayers
        }),
        Pending = store.GetPending().ConvertAll(pending => new PendingApplyRow
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
        }),
        Applied = store.GetApplied(),
        KnownServers = store.GetKnownServers(),
        ConnectedServers = ServerInfos(store, connected).FindAll(s => s.Connected)
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
