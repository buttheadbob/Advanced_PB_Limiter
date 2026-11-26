using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.Settings
{
    /// <summary>
    /// The plugin's config file, in the format Torch's Persistent wrote: XmlSerializer of Advanced_PB_LimiterConfig, UTF-8.
    /// A file that is not a config is moved aside and never saved over. Saves go through <see cref="SafeFile"/>, a complete
    /// copy swapped in with one rename, and a change to a setting is saved a second after the last change, as Persistent did.
    /// When that save fails, the change keeps waiting and is tried again every 30 seconds until it is saved. That retry is
    /// what saves it later: Torch's own server does not dispose its plugins when it stops or restarts, so a change that is
    /// not saved before the process ends is lost.
    /// </summary>
    internal sealed class ConfigFile : IDisposable
    {
        private const int AutosaveDelayMs = 1000;

        // Long enough that a file another program holds for a while, a backup or an editor say, is not tried over and over.
        private const int AutosaveRetryDelayMs = 30_000;

        private static readonly TorchLog Log = new("Advanced PB Limiter");
        private static readonly XmlSerializer Serializer = new(typeof(Advanced_PB_LimiterConfig));

        private readonly string _path;
        // Saves run one at a time, in order: each waits for the one before it. Nothing to dispose, so a save queued while
        // Dispose runs still runs, rather than failing on a disposed lock.
        private readonly object _saveQueueLock = new();
        private Task _lastSave = Task.CompletedTask;
        private readonly object _timerLock = new();
        private readonly Timer _autosave;
        private string? _saveRefused;
        private bool _pending;
        private bool _disposed;
        private bool _swapFailureLogged;

        // The reason the last failed save was logged for, by the autosave or by whoever called SaveAsync, one save at a
        // time: a retry that fails the same way logs nothing, and the save that works after it says so.
        private string? _autosaveFailure;

        public Advanced_PB_LimiterConfig Data { get; }

        /// <summary>True when these are the defaults, not values read from the file.</summary>
        public bool IsNew { get; private set; }

        /// <summary>No save is made until Torch restarts, see <see cref="LoadAsync"/>; a failed save is not tried again then.</summary>
        public bool SavesRefused => _saveRefused is not null;

        private ConfigFile(string path, Advanced_PB_LimiterConfig data, bool isNew, string? saveRefused)
        {
            _path = path;
            _saveRefused = saveRefused;
            Data = data;
            IsNew = isNew;
            _autosave = new Timer(Autosave, null, Timeout.Infinite, Timeout.Infinite);
            Data.PropertyChanged += OnPropertyChanged;
        }

        /// <summary>
        /// The live instance before the file is read: the tab and the limiter hold it from the start, and
        /// <see cref="Adopt"/> later copies the file's settings into it. It saves nothing until then, so defaults edited
        /// meanwhile never replace the file.
        /// </summary>
        internal static ConfigFile Defaults(string path) => new(path, new Advanced_PB_LimiterConfig(), true, "the config has not been read yet");

        /// <summary>
        /// Copies the settings and the save state read from the file into this instance, which the limiter already holds.
        /// The change handler is detached while the copy runs, so adopting does not arm a save. A change <paramref name="loaded"/>
        /// still had waiting, its first save having failed, is taken over and tried again here, as the loaded instance is
        /// disposed after this.
        /// </summary>
        internal void Adopt(ConfigFile loaded)
        {
            bool pending;
            lock (loaded._timerLock)
            {
                pending = loaded._pending;
                loaded._pending = false;
            }

            Data.PropertyChanged -= OnPropertyChanged;
            try
            {
                foreach (PropertyInfo property in ConfigProperties)
                    property.SetValue(Data, property.GetValue(loaded.Data));
                IsNew = loaded.IsNew;
                _saveRefused = loaded._saveRefused;
                _autosaveFailure = loaded._autosaveFailure;
            }
            finally
            {
                Data.PropertyChanged += OnPropertyChanged;
            }

            if (!pending) return;
            lock (_timerLock)
            {
                if (_disposed || _saveRefused is not null) return;
                _pending = true;
                _autosave.Change(AutosaveRetryDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>No save is made from now until Torch restarts, for <paramref name="reason"/>.</summary>
        internal void RefuseSaves(string reason)
        {
            lock (_timerLock)
            {
                _saveRefused = reason;
                _pending = false;
                if (!_disposed)
                    _autosave.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private static readonly PropertyInfo[] ConfigProperties = typeof(Advanced_PB_LimiterConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            .ToArray();

        /// <summary>
        /// Reads the file, or starts from the defaults when there is none or it can't be read. A save that was interrupted
        /// is dealt with first, see <see cref="SafeFile.RecoverAsync"/>. A file that is not a config is moved aside; a file
        /// that can't be opened, held by another program say, is left alone and nothing is saved over it until a restart.
        /// A missing or cut-short file with a copy beside it, from its own interrupted save or the Damage rewrite, that could
        /// not be put back yet is not replaced by the defaults either: saved, they would be the config the next start sees,
        /// and that copy would only be kept aside. The next start restores the copy.
        /// </summary>
        public static async Task<ConfigFile> LoadAsync(string path)
        {
            // RecoverInterruptedSaveAsync has run already: the plugin calls it first, before the Damage check reads the file.
            if (!File.Exists(path))
            {
                if (await WaitingCopyAsync(path).ConfigureAwait(false) is not { } copy)
                    return await CreateNewAsync(path).ConfigureAwait(false);
                Log.Error("The config " + path + " is missing and the copy at " + copy + " could not be put back. Advanced PB Limiter runs on the defaults and saves nothing until Torch restarts, when the copy is restored.");
                return new ConfigFile(path, new Advanced_PB_LimiterConfig(), true, "the config is missing, and its copy at " + copy + " is restored when Torch restarts");
            }

            try
            {
                return new ConfigFile(path, Read(await SafeFile.ReadAllBytesAsync(path).ConfigureAwait(false)), false, null);
            }
            catch (Exception e) when (SafeFile.IsReadFailure(e))
            {
                // Before the format errors: XmlSerializer wraps an I/O error met while reading in an InvalidOperationException.
                Log.Error(e, "The config could not be opened: " + path + ". Advanced PB Limiter runs on the defaults and saves nothing over that file until it is restarted.");
                return new ConfigFile(path, new Advanced_PB_LimiterConfig(), true, "the config " + path + " could not be opened when the plugin loaded");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or XmlException)
            {
                // Cut short beside a complete copy (a rename and then a write in place that both failed, on a full disk say):
                // the next start moves it aside and restores the copy, as long as nothing is saved over it first.
                if (await WaitingCopyAsync(path).ConfigureAwait(false) is { } copy)
                {
                    Log.Error(e, "The config " + path + " could not be read and the copy at " + copy + " could not be put back. Advanced PB Limiter runs on the defaults and saves nothing until Torch restarts, when the copy is restored.");
                    return new ConfigFile(path, new Advanced_PB_LimiterConfig(), true, "the config could not be read, and its copy at " + copy + " is restored when Torch restarts");
                }
                return await MoveAsideAsync(path, e).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The copy left by this file's own save or by the Damage rewrite that may still restore it, or null. Only a copy
        /// that may: one cut short never can, and would hold saves back at every start.
        /// </summary>
        private static async Task<string?> WaitingCopyAsync(string path)
        {
            foreach (string copy in new[] { TempPath(path), InstantPunishmentFile.CopyPath(path) })
            {
                if (await SafeFile.MayRestoreAsync(copy, SafeFile.IsWholeXml).ConfigureAwait(false))
                    return copy;
            }
            return null;
        }

        /// <summary>
        /// Deals with a save that was interrupted (<see cref="SafeFile.RecoverAsync"/>), before anything else reads the
        /// file. Call it once per load: a copy it could not keep aside is warned about each time.
        /// </summary>
        public static async Task RecoverInterruptedSaveAsync(string path)
        {
            string temp = TempPath(path);
            try
            {
                SafeFile.RecoverResult recovery = await SafeFile.RecoverAsync(path, temp, SafeFile.IsWholeXml).ConfigureAwait(false);
                string? line = SafeFile.Describe(recovery.Recovery, path, temp, recovery.Kept);
                if (line is not null)
                    Log.Warn(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn(e, "Could not restore the config from " + temp + "; the config is read as it is");
            }
        }

        private static string TempPath(string path) => path + ".tmp";

        private static Advanced_PB_LimiterConfig Read(byte[] bytes)
        {
            using MemoryStream stream = new(bytes, false);
            using XmlReader reader = SafeXml.Reader(stream);
            return Serializer.Deserialize(reader) as Advanced_PB_LimiterConfig ?? throw new InvalidDataException("The file holds no config.");
        }

        private static async Task<ConfigFile> CreateNewAsync(string path)
        {
            ConfigFile created = new(path, new Advanced_PB_LimiterConfig(), true, null);
            await created.TrySaveAsync("The new config could not be saved").ConfigureAwait(false);
            return created;
        }

        /// <summary>
        /// Keeps the unreadable file for the admin to fix, under a name no earlier file holds (<see cref="KeptFile.Keep"/>).
        /// If it can't be moved, the defaults are never saved over it.
        /// </summary>
        private static async Task<ConfigFile> MoveAsideAsync(string path, Exception readError)
        {
            string aside;
            try
            {
                aside = KeptFile.Keep(path, ".unreadable");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error(readError, "The config could not be read: " + path);
                Log.Error(e, "It could not be moved aside either. Advanced PB Limiter runs on the defaults and saves nothing over that file until it is restarted.");
                return new ConfigFile(path, new Advanced_PB_LimiterConfig(), true, "the config " + path + " could not be read or moved aside");
            }

            Log.Error(readError, "The config could not be read and was moved to " + aside + ". Advanced PB Limiter runs on the defaults; fix that file and put it back to use it again.");
            return await CreateNewAsync(path).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes the config. Throws when it can't be written, when saves are refused (see <see cref="LoadAsync"/>), or once
        /// <see cref="Dispose"/> has run. A change waiting for the autosave is written by this save, so that autosave is
        /// called off rather than writing the file a second time; a change made after this point arms it again. If the
        /// write fails, the config, which then holds something the file does not, counts as waiting and the autosave tries
        /// it again in 30 seconds, then every 30 seconds until a save works or <see cref="Dispose"/> runs.
        /// </summary>
        public Task SaveAsync()
        {
            lock (_timerLock)
            {
                if (_disposed)
                    throw new InvalidOperationException("The plugin has stopped, so the config is not saved.");
            }
            return SaveCoreAsync(false);
        }

        /// <summary>
        /// The save, after any save queued before it, so a save waits for one already writing. With
        /// <paramref name="onlyIfPending"/>, for the autosave and the last save in Dispose, it writes only when a change still
        /// waits once the lock is held, and a refused save is skipped rather than thrown.
        /// </summary>
        private async Task SaveCoreAsync(bool onlyIfPending)
        {
            if (_saveRefused is not null)
            {
                if (onlyIfPending) return;
                throw new InvalidOperationException("Not saved until Torch restarts: " + _saveRefused + ".");
            }

            TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task previous;
            lock (_saveQueueLock)
            {
                previous = _lastSave;
                _lastSave = done.Task;
            }
            try
            {
                // Never faults: every save completes its own task in the finally below.
                await previous.ConfigureAwait(false);
                lock (_timerLock)
                {
                    if (onlyIfPending && !_pending) return;
                    _pending = false;
                    if (!_disposed)
                        _autosave.Change(Timeout.Infinite, Timeout.Infinite);
                }

                try
                {
                    await WriteSafelyAsync(Serialize(Data)).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Whoever called SaveAsync logs this failure; the retries that follow only log a different reason.
                    _autosaveFailure = Reason(e);
                    // Whatever this save was for, a waiting change or one that raises no change event (a privileged
                    // player added from the tab or a command), the config holds something the file does not: try again,
                    // by the autosave, or by the last save in Dispose when that is waiting for this one.
                    lock (_timerLock)
                    {
                        _pending = true;
                        // The longer delay also replaces the one second a change made during this write armed, so a file
                        // that is held is not tried again at once, bar a callback that had already fired.
                        if (!_disposed)
                            _autosave.Change(AutosaveRetryDelayMs, Timeout.Infinite);
                    }
                    throw;
                }

                if (_autosaveFailure is null) return;
                _autosaveFailure = null;
                Log.Info("The config was saved, after an earlier save had failed");
            }
            finally
            {
                done.SetResult(true);
            }
        }

        private static byte[] Serialize(Advanced_PB_LimiterConfig data)
        {
            using MemoryStream content = new();
            using (StreamWriter text = new(content, new UTF8Encoding(false), 1024, true))
                Serializer.Serialize(text, data);
            return content.ToArray();
        }

        /// <summary>
        /// Through <see cref="SafeFile.WriteAsync"/>: a complete copy swapped in with one rename, written in place only where
        /// the swap fails, and one save of the file at a time, also with another instance of the plugin after a reload.
        /// </summary>
        private async Task WriteSafelyAsync(byte[] content)
        {
            SafeFile.WriteResult write = await SafeFile.WriteAsync(_path, TempPath(_path), content, SafeFile.IsWholeXml).ConfigureAwait(false);
            if (write.Kept is not null)
                Log.Warn("A complete copy of the config an earlier save left at " + TempPath(_path) + " was kept as " + write.Kept + " before this save, in case it holds settings you want.");
            if (write.SwapError is null || _swapFailureLogged) return;
            // For example a file mounted on its own into a container, or held open by another program.
            _swapFailureLogged = true;
            Log.Warn(write.SwapError, "The config could not be swapped into place, so it is written in place");
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Runtime only, never written (see Advanced_PB_LimiterConfig.ShowTimingsBurp): the file holds nothing new.
            if (e.PropertyName == nameof(Advanced_PB_LimiterConfig.ShowTimingsBurp)) return;
            lock (_timerLock)
            {
                // No save is made while saves are refused (also before the file is read), or while the limiter is retired
                // (a reload or a game mismatch, whose saves the plugin refuses), so there is nothing for the autosave to try.
                if (_disposed || _saveRefused is not null || LimiterCore.Retired) return;
                _pending = true;
                _autosave.Change(AutosaveDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>What tells two failed saves apart for the log: the kind of error and its message.</summary>
        private static string Reason(Exception error) => error.GetType().FullName + ": " + error.Message;

        // The timer's callback, on a pool thread. The save runs as a task that logs every failure itself.
        private void Autosave(object? state) => _ = AutosaveAsync();

        // SaveCoreAsync clears the waiting change itself, and keeps it waiting and arms the retry if the write fails.
        private async Task AutosaveAsync()
        {
            try
            {
                lock (_timerLock)
                {
                    // Dispose may have run, or an explicit save written the change, after this call was queued.
                    if (_disposed || !_pending) return;
                }

                // A failure's reason is recorded by the save itself, so the reason logged before this try is kept to compare with.
                string? logged = _autosaveFailure;
                try
                {
                    await SaveCoreAsync(true).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (string.Equals(Reason(e), logged, StringComparison.Ordinal)) return;
                    string retry = (AutosaveRetryDelayMs / 1000).ToString(CultureInfo.InvariantCulture);
                    Log.Warn(e, "The config could not be saved. The change is tried again every " + retry + " seconds; this is logged again only if the reason changes");
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "The config autosave failed");
            }
        }

        // Runs when a new config is created, where an exception would end the load.
        private async Task TrySaveAsync(string failure)
        {
            try
            {
                await SaveAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Warn(e, failure);
            }
        }

        /// <summary>
        /// Stops the autosave and its retries, waits for a save already writing, and then writes a change still waiting,
        /// also one that save gave back when its write failed, and waits for that too. Torch calls Dispose synchronously and
        /// may end the process soon after, so this is the one place the plugin blocks on a save. Every await on the save
        /// path uses ConfigureAwait(false), so the save needs no thread the caller may hold. No save is made afterwards.
        /// </summary>
        public void Dispose()
        {
            lock (_timerLock)
            {
                if (_disposed) return;
                _disposed = true;
                Data.PropertyChanged -= OnPropertyChanged;
                _autosave.Dispose();
            }

            SaveLastChangeAsync().GetAwaiter().GetResult();
        }

        private async Task SaveLastChangeAsync()
        {
            try
            {
                await SaveCoreAsync(true).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Warn(e, "The config could not be saved when the plugin stopped, so its last change is not in the file");
            }
        }
    }
}
