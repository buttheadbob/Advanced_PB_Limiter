#nullable enable
using System;
using System.Collections.Generic;

namespace Advanced_PB_Limiter.QuasarContracts
{
    // The browser SDK operations the Quasar 1.7 dashboard calls, and the shapes it exchanges with the worker adapter.
    // Compiled into the browser assembly and the worker adapter, and holds no game or Quasar types.

    /// <summary>An operation with no request body.</summary>
    public class EmptyPayload
    {
    }

    public static class UiOperations
    {
        /// <summary><see cref="EmptyPayload"/> in, <see cref="ServerListResponse"/> out.</summary>
        public const string Servers = "servers.list";

        /// <summary><see cref="ReadSnapshotRequest"/> in, <see cref="ReportSnapshot"/> out.</summary>
        public const string Snapshot = "report.read";

        /// <summary><see cref="TurnOffBlockUiRequest"/> in, <see cref="TurnOffResult"/> out.</summary>
        public const string TurnOffBlock = "block.turnoff";

        /// <summary><see cref="TurnOffPlayerUiRequest"/> in, <see cref="TurnOffResult"/> out.</summary>
        public const string TurnOffPlayer = "player.turnoff";

        /// <summary><see cref="EmptyPayload"/> in, <see cref="ProfilesStateResponse"/> out.</summary>
        public const string ProfilesState = "profiles.state";

        /// <summary><see cref="ProfileSaveRequest"/> in, <see cref="ProfileActionResult"/> out.</summary>
        public const string ProfileSave = "profiles.save";

        /// <summary><see cref="ProfileNameRequest"/> in, <see cref="ProfileActionResult"/> out.</summary>
        public const string ProfileDelete = "profiles.delete";

        /// <summary><see cref="ProfileQueueRequest"/> in, <see cref="ProfileActionResult"/> out.</summary>
        public const string ProfileQueue = "profiles.queue";

        /// <summary><see cref="ProfileCancelRequest"/> in, <see cref="ProfileActionResult"/> out.</summary>
        public const string ProfileCancel = "profiles.cancel";

        /// <summary><see cref="ProfileForgetRequest"/> in, <see cref="ProfileActionResult"/> out.</summary>
        public const string ProfileForget = "profiles.forget";
    }

    public class ServerInfo
    {
        public string ServerId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ClusterId { get; set; } = "";
        public string ClusterSlot { get; set; } = "";
        public bool Connected { get; set; }
        public bool Known { get; set; }
        public DateTime LastSeenUtc { get; set; }

        /// <summary>
        /// What a page keeps its selection by. A cluster node that restarts comes back in the same slot under a new
        /// <see cref="ServerId"/>, and the worker resolves the slot to the node now in it, so the slot is the stable key;
        /// a server outside a cluster is keyed by its id.
        /// </summary>
        public string PickKey => ServerPickKeys.For(ClusterId, ClusterSlot, ServerId);
    }

    public class ServerListResponse
    {
        public List<ServerInfo> Servers { get; set; } = new();
    }

    public class ReadSnapshotRequest
    {
        public string ServerId { get; set; } = "";
    }

    public class TurnOffBlockUiRequest
    {
        public string ServerId { get; set; } = "";

        /// <summary>A decimal string, because the browser's number loses precision above 2^53; the worker parses it.</summary>
        public string EntityId { get; set; } = "";
    }

    public class TurnOffPlayerUiRequest
    {
        public string ServerId { get; set; } = "";

        /// <summary>A decimal string, as <see cref="TurnOffBlockUiRequest.EntityId"/>.</summary>
        public string IdentityId { get; set; } = "";
    }

    /// <summary>A server's settings saved under a name.</summary>
    public class SavedProfile
    {
        public string Name { get; set; } = "";
        public string SavedFrom { get; set; } = "";
        public DateTime SavedUtc { get; set; }
        public string SettingsJson { get; set; } = "";
        public int Settings { get; set; }
        public int PrivilegedPlayers { get; set; }
    }

    /// <summary>A profile on its way to a server. It holds its own copy, so editing the profile later does not change what was asked for.</summary>
    public class PendingApply
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string SettingsJson { get; set; } = "";
        public string QueuedBy { get; set; } = "";
        public DateTime QueuedUtc { get; set; }
        public long Sequence { get; set; }
        public string? LastError { get; set; }
        public bool Sending { get; set; }
        public bool Refused { get; set; }
        public bool MayBeApplied { get; set; }

        // Every member is a string or a value, so this is a full copy.
        public PendingApply Clone() => (PendingApply)MemberwiseClone();
    }

    public class AppliedProfile
    {
        public string ServerName { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string QueuedBy { get; set; } = "";
        public DateTime AppliedUtc { get; set; }
        public int Settings { get; set; }
        public int PrivilegedPlayers { get; set; }
    }

    /// <summary>A server seen connected at some point. Quasar only lists connected ones, and a profile has to be able to wait for the others.</summary>
    public class KnownServer
    {
        public string ServerId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
        public string ClusterId { get; set; } = "";
        public string ClusterSlot { get; set; } = "";
        public long LastAppliedSequence { get; set; }

        // Every member is a string or a value, so this is a full copy.
        public KnownServer Clone() => (KnownServer)MemberwiseClone();
    }

    /// <summary>What a change did. <see cref="Saved"/> is false when a change is in memory only and is lost when Quasar restarts.</summary>
    public readonly record struct StoreResult(bool Changed, bool Saved);

    /// <summary>What queueing a profile did, and for how many servers: fewer than asked when some were forgotten meanwhile.</summary>
    public readonly record struct QueueResult(StoreResult Result, int Servers);

    public enum CancelOutcome
    {
        Cancelled,
        /// <summary>The queue had already claimed it and is sending it.</summary>
        Sending,
        /// <summary>Already applied, cancelled or replaced.</summary>
        NotWaiting,
        /// <summary>Quasar was stopping the store, so nothing was changed.</summary>
        Stopping
    }

    public readonly record struct CancelResult(CancelOutcome Outcome, bool Saved);

    /// <summary>
    /// A saved profile as the browser sees it. The settings stay in the worker and are sent only when a page asks for
    /// them, not with every poll of the profiles state.
    /// </summary>
    public class SavedProfileRow
    {
        public string Name { get; set; } = "";
        public string SavedFrom { get; set; } = "";
        public DateTime SavedUtc { get; set; }
        public int Settings { get; set; }
        public int PrivilegedPlayers { get; set; }
    }

    /// <summary>A queued request as the browser sees it; its own copy of the settings stays in the worker.</summary>
    public class PendingApplyRow
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string QueuedBy { get; set; } = "";
        public DateTime QueuedUtc { get; set; }
        public long Sequence { get; set; }
        public string? LastError { get; set; }
        public bool Sending { get; set; }
        public bool Refused { get; set; }
        public bool MayBeApplied { get; set; }
    }

    public class ProfilesStateResponse
    {
        public bool CanSave { get; set; }
        public List<SavedProfileRow> Profiles { get; set; } = new();
        public List<PendingApplyRow> Pending { get; set; } = new();
        public List<AppliedProfile> Applied { get; set; } = new();
        public List<KnownServer> KnownServers { get; set; } = new();
        public List<ServerInfo> ConnectedServers { get; set; } = new();
    }

    public class ProfileSaveRequest
    {
        public string ServerId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class ProfileNameRequest
    {
        public string Name { get; set; } = "";
    }

    public class ProfileQueueRequest
    {
        public string ProfileName { get; set; } = "";

        /// <summary>
        /// <see cref="ServerInfo.PickKey"/>s, not ids: a cluster slot stays valid across a node restart, and the worker
        /// resolves it to the node now in the slot.
        /// </summary>
        public List<string> ServerKeys { get; set; } = new();

        /// <summary>Ids from a page loaded before 1.3.3, which sent no keys; read only when <see cref="ServerKeys"/> is empty.</summary>
        public List<string> ServerIds { get; set; } = new();
    }

    public class ProfileCancelRequest
    {
        public Guid Id { get; set; }
    }

    public class ProfileForgetRequest
    {
        public string ServerId { get; set; } = "";
    }

    /// <summary>What a profile action did, with the state after it, so a page shows the result and the new lists at once.</summary>
    public class ProfileActionResult
    {
        public bool Changed { get; set; }
        public bool Saved { get; set; }
        public string Message { get; set; } = "";
        public ProfilesStateResponse State { get; set; } = new();
    }
}
