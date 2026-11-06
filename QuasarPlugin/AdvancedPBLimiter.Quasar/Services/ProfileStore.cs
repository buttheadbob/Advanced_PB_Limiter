using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Companion;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>A server's settings saved under a name.</summary>
public sealed class SavedProfile
{
    public string Name { get; set; } = "";
    public string SavedFrom { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public string SettingsJson { get; set; } = "";
    public int Settings { get; set; }
    public int PrivilegedPlayers { get; set; }
}

/// <summary>A profile on its way to a server. It holds its own copy, so editing the profile later does not change what was asked for.</summary>
public sealed class PendingApply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string SettingsJson { get; set; } = "";
    public string QueuedBy { get; set; } = "";
    public DateTime QueuedUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class AppliedProfile
{
    public string ServerName { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string QueuedBy { get; set; } = "";
    public DateTime AppliedUtc { get; set; }
    public int Settings { get; set; }
    public int PrivilegedPlayers { get; set; }
}

/// <summary>A server seen connected at some point. Quasar only lists connected ones, and a profile has to be able to wait for the others.</summary>
public sealed class KnownServer
{
    public string ServerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>Profiles, the queue and known servers, kept in one file under Quasar's install folder. All of it is safe to call from any thread.</summary>
public sealed class ProfileStore
{
    private const int MaxApplied = 25;

    // Limits a known server to being re-saved once in a while, every poll of every page would otherwise write the file.
    private static readonly TimeSpan SeenSaveInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<ProfileStore> _log;
    private State _state = new();

    public ProfileStore(string installDirectory, ILogger<ProfileStore> log)
    {
        string folder = Path.Combine(installDirectory, "plugin-data", "advancedpblimiter");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "profiles.json");
        _log = log;
        Load();
    }

    /// <summary>Raised after any change, outside the lock, on whichever thread made it.</summary>
    public event Action? Changed;

    public IReadOnlyList<SavedProfile> Profiles
    {
        get { lock (_lock) return _state.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    }

    public IReadOnlyList<PendingApply> Pending
    {
        get { lock (_lock) return _state.Pending.OrderBy(p => p.QueuedUtc).ToList(); }
    }

    public IReadOnlyList<AppliedProfile> Applied
    {
        get { lock (_lock) return _state.Applied.ToList(); }
    }

    public IReadOnlyList<KnownServer> KnownServers
    {
        get { lock (_lock) return _state.KnownServers.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    }

    public void SaveProfile(SavedProfile profile)
    {
        Update(state =>
        {
            state.Profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase));
            state.Profiles.Add(profile);
        });
    }

    public void DeleteProfile(string name)
    {
        Update(state => state.Profiles.RemoveAll(p => p.Name == name));
    }

    /// <summary>A server waits for one profile at most: the newest request is the one the admin meant.</summary>
    public void Queue(SavedProfile profile, IEnumerable<KnownServer> servers, string queuedBy)
    {
        DateTime now = DateTime.UtcNow;
        Update(state =>
        {
            foreach (KnownServer server in servers)
            {
                state.Pending.RemoveAll(p => p.ServerId == server.ServerId);
                state.Pending.Add(new PendingApply
                {
                    ServerId = server.ServerId,
                    ServerName = server.DisplayName,
                    ProfileName = profile.Name,
                    SettingsJson = profile.SettingsJson,
                    QueuedBy = queuedBy,
                    QueuedUtc = now
                });
            }
        });
    }

    public void CancelPending(Guid id)
    {
        Update(state => state.Pending.RemoveAll(p => p.Id == id));
    }

    public void CompletePending(PendingApply pending, int settings, int privilegedPlayers)
    {
        Update(state =>
        {
            // By id: a newer request for this server may have replaced it while this one was on its way, and that one still has to run.
            state.Pending.RemoveAll(p => p.Id == pending.Id);
            state.Applied.Insert(0, new AppliedProfile
            {
                ServerName = pending.ServerName,
                ProfileName = pending.ProfileName,
                QueuedBy = pending.QueuedBy,
                AppliedUtc = DateTime.UtcNow,
                Settings = settings,
                PrivilegedPlayers = privilegedPlayers
            });
            if (state.Applied.Count > MaxApplied)
                state.Applied.RemoveRange(MaxApplied, state.Applied.Count - MaxApplied);
        });
    }

    /// <summary>Records why a profile is still waiting. True when that is a new reason, which is what is worth logging.</summary>
    public bool FailPending(Guid id, string error)
    {
        bool changed;
        lock (_lock)
        {
            PendingApply? pending = _state.Pending.Find(p => p.Id == id);
            changed = pending != null && pending.LastError != error;
            if (changed)
            {
                pending!.LastError = error;
                Save();
            }
        }

        if (changed)
            Changed?.Invoke();
        return changed;
    }

    public void ServersSeen(IEnumerable<QuasarCompanionServer> servers)
    {
        bool changed = false;
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (QuasarCompanionServer server in servers)
            {
                KnownServer? known = _state.KnownServers.Find(s => s.ServerId == server.ServerId);
                if (known == null)
                {
                    _state.KnownServers.Add(new KnownServer { ServerId = server.ServerId, DisplayName = server.DisplayName, LastSeenUtc = now });
                    changed = true;
                }
                else if (known.DisplayName != server.DisplayName || now - known.LastSeenUtc > SeenSaveInterval)
                {
                    known.DisplayName = server.DisplayName;
                    known.LastSeenUtc = now;
                    changed = true;
                }
            }

            if (changed)
                Save();
        }

        if (changed)
            Changed?.Invoke();
    }

    public void ForgetServer(string serverId)
    {
        Update(state =>
        {
            state.KnownServers.RemoveAll(s => s.ServerId == serverId);
            state.Pending.RemoveAll(p => p.ServerId == serverId);
        });
    }

    private void Update(Action<State> change)
    {
        lock (_lock)
        {
            change(_state);
            Save();
        }

        Changed?.Invoke();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json) ?? new State();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Kept for the admin to look at, the next save would otherwise replace it.
            string aside = _path + ".unreadable";
            _log.LogError(e, "Could not read the PB Limiter profiles, moved them to {File}", aside);
            File.Move(_path, aside, overwrite: true);
        }
    }

    // Written beside the file and moved over it, so a crash mid-write cannot lose the profiles.
    private void Save()
    {
        try
        {
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException e)
        {
            _log.LogError(e, "Could not save the PB Limiter profiles");
        }
    }

    private sealed class State
    {
        public List<SavedProfile> Profiles { get; set; } = new();
        public List<PendingApply> Pending { get; set; } = new();
        public List<AppliedProfile> Applied { get; set; } = new();
        public List<KnownServer> KnownServers { get; set; } = new();
    }
}
