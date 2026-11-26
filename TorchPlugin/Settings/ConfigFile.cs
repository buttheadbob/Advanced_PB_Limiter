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
        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private readonly object _timerLock = new();
        private readonly Timer _autosave;
        private string? _saveRefused;
        private bool _pending;
        private bool _disposed;
        private bool _swapFailureLogged;

        // The reason the last failed save was logged for, by the autosave or by whoever called Save, under the save lock:
        // a retry that fails the same way logs nothing, and the save that works after it says so.
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
        /// The live instance before the file is read: the limiter enforces these defaults while the async read runs, then
        /// <see cref="Adopt"/> copies the file's settings into this same instance.
        /// </summary>
        internal static ConfigFile Defaults(string path) => new(path, new Advanced_PB_LimiterConfig(), true, null);

        /// <summary>
        /// Copies the settings and the save state read from the file into this instance, which the limiter already holds.
        /// The change handler is detached while the copy runs, so adopting does not arm a save.
        /// </summary>
        internal void Adopt(ConfigFile loaded)
        {
            Data.PropertyChanged -= OnPropertyChanged;
            try
            {
                foreach (PropertyInfo property in ConfigProperties)
                    property.SetValue(Data, property.GetValue(loaded.Data));
                IsNew = loaded.IsNew;
                _saveRefused = loaded._saveRefused;
            }
            finally
            {
                Data.PropertyChanged += OnPropertyChanged;
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
        /// A missing file with a copy beside it, from its own interrupted save or the Damage rewrite, that could not be put
        /// back yet is not replaced by the defaults either: saved, they would be the config the next start sees, and that
        /// copy would only be kept aside.
        /// </summary>
        public static async Task<ConfigFile> LoadAsync(string path)
        {
            // RecoverInterruptedSaveAsync has run already: the plugin calls it first, before the Damage check reads the file.
            if (!File.Exists(path))
            {
                // A copy left by this file's own save or by the Damage rewrite, that could not be put back or even read yet.
                // Only a copy that may still restore it: one cut short can never, and would hold saves back at every start.
                string copy = TempPath(path);
                if (!await SafeFile.MayRestoreAsync(copy, SafeFile.IsWholeXml).ConfigureAwait(false))
                    copy = InstantPunishmentFile.CopyPath(path);
                if (!await SafeFile.MayRestoreAsync(copy, SafeFile.IsWholeXml).ConfigureAwait(false))
                    return await CreateNewAsync(path).ConfigureAwait(false);
                Log.Error("The config " + path + " is missing and the copy at " + copy + " could not be put back. Advanced PB Limiter runs on the defaults and saves nothing until Torch restarts, when the copy is restored.");
                return new ConfigFile(path, new Advanced_PB_LimiterConfig(), true, "the config is missing, and its copy at " + copy + " is restored when Torch restarts");
            }

            try
            {
                return new ConfigFile(path, await ReadAsync(path).ConfigureAwait(false), false, null);
            }
            catch (Exception e) when (SafeFile.IsReadFailure(e))
            {
                // Before the format errors: XmlSerializer wraps an I/O error met while reading in an InvalidOperationException.
                Log.Error(e, "The config could not be opened: " + path + ". Advanced PB Limiter runs on the defaults and saves nothing over that file until it is restarted.");
                return new ConfigFile(path, new Advanced_PB_LimiterConfig(), true, "the config " + path + " could not be opened when the plugin loaded");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or XmlException)
            {
                return await MoveAsideAsync(path, e).ConfigureAwait(false);
            }
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

        private static async Task<Advanced_PB_LimiterConfig> ReadAsync(string path)
        {
            byte[] bytes = await SafeFile.ReadAllBytesAsync(path).ConfigureAwait(false);
            using MemoryStream stream = new(bytes, false);
            using XmlReader reader = SafeXml.Reader(stream);
            return Serializer.Deserialize(reader) as Advanced_PB_LimiterConfig ?? throw new InvalidDataException("The file holds no config.");
        }

        private static async Task<ConfigFile> CreateNewAsync(string path)
        {
            ConfigFile created = Defaults(path);
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
        /// Queues a save and says only whether one can be made: false when saves are refused. The write is asynchronous;
        /// a failure is logged and retried by the autosave, not returned here.
        /// </summary>
        public bool Save()
        {
            if (_saveRefused is not null)
            {
                Log.Warn("Not saved until Torch restarts: " + _saveRefused + ".");
                return false;
            }

            _ = SaveAsync().ContinueWith(
                task => Log.Warn(task.Exception, "The config could not be saved"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return true;
        }

        /// <summary>
        /// Writes the config. Throws when it can't be written, or when the unreadable file could not be moved aside. A change
        /// waiting for the autosave is written by this save, so that autosave is called off rather than writing the file a
        /// second time; a change made after this point arms it again. If the write fails, the config, which then holds
        /// something the file does not, counts as waiting and the autosave tries it again in 30 seconds, then every 30
        /// seconds until a save works or <see cref="Dispose"/> runs. A refused save (see <see cref="LoadAsync"/>) is not tried
        /// again.
        /// </summary>
        public async Task SaveAsync()
        {
            if (_saveRefused is not null)
                throw new InvalidOperationException("Not saved until Torch restarts: " + _saveRefused + ".");

            // All under the save lock, which Dispose takes first: it then sees either the change still waiting, or this save
            // done, with a change whose write failed put back.
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_timerLock)
                {
                    _pending = false;
                    if (!_disposed)
                        _autosave.Change(Timeout.Infinite, Timeout.Infinite);
                }

                try
                {
                    using MemoryStream content = new();
                    using (StreamWriter text = new(content, new UTF8Encoding(false), 1024, true))
                        Serializer.Serialize(text, Data);
                    await WriteSafelyAsync(content).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Whoever called Save logs this failure; the retries that follow only log a different reason.
                    _autosaveFailure = Reason(e);
                    // Whatever this save was for, a waiting change or one that raises no change event (a privileged
                    // player added from the tab or a command), the config holds something the file does not: try again.
                    lock (_timerLock)
                    {
                        // The longer delay also replaces the one second a change made during this write armed, so a file
                        // that is held is not tried again at once, bar a callback that had already fired.
                        if (!_disposed)
                        {
                            _pending = true;
                            _autosave.Change(AutosaveRetryDelayMs, Timeout.Infinite);
                        }
                    }
                    throw;
                }

                if (_autosaveFailure is null) return;
                _autosaveFailure = null;
                Log.Info("The config was saved, after an earlier save had failed");
            }
            finally
            {
                _saveLock.Release();
            }
        }

        /// <summary>
        /// Through <see cref="SafeFile.WriteAsync"/>: a complete copy swapped in with one rename, written in place only where
        /// the swap fails, and one save of the file at a time, also with another instance of the plugin after a reload.
        /// </summary>
        private async Task WriteSafelyAsync(MemoryStream content)
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
                // No save is made while saves are refused, or while the limiter is retired (a reload or a game mismatch,
                // whose saves the plugin refuses), so there is nothing for the autosave to try.
                if (_disposed || _saveRefused is not null || LimiterCore.Retired) return;
                _pending = true;
                _autosave.Change(AutosaveDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>What tells two failed saves apart for the log: the kind of error and its message.</summary>
        private static string Reason(Exception error) => error.GetType().FullName + ": " + error.Message;

        // Runs on the timer thread, where an exception would end the process. SaveAsync clears the waiting change itself,
        // and keeps it waiting and arms the retry if the write fails. Under the save lock (Save takes it again, which a
        // semaphore does not allow re-entrantly, so this wrapper only calls SaveAsync), so the check below and the logged
        // reason see one save at a time.
        private async void Autosave(object? state)
        {
            try
            {
                lock (_timerLock)
                {
                    // Dispose may have run, or an explicit save written the change, after this call was queued.
                    if (_disposed || !_pending) return;
                }

                // Save records a failure's reason itself, so the reason logged before this try is kept to compare with.
                string? logged = _autosaveFailure;
                try
                {
                    await SaveAsync().ConfigureAwait(false);
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
                // An async void timer callback must not let an exception end the process.
                Log.Error(e, "The config autosave failed");
            }
        }

        // Runs when the plugin loads and in Dispose, where an exception would end the load or the reload.
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
        /// Stops the autosave and its retries, and starts one last save of a change still waiting for them, also one an
        /// autosave still writing gives back when its write fails. Torch calls this when it reloads plugins, not when its
        /// own server stops or restarts, so the process is still alive for that write to finish. <see cref="Save"/> still
        /// works afterwards.
        /// </summary>
        public void Dispose()
        {
            bool pending;
            lock (_timerLock)
            {
                if (_disposed) return;
                _disposed = true;
                pending = _pending;
                _pending = false;
                Data.PropertyChanged -= OnPropertyChanged;
                _autosave.Dispose();
            }

            if (pending)
                _ = SaveAfterDisposeAsync();
        }

        private async Task SaveAfterDisposeAsync()
        {
            try
            {
                await SaveAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Warn(e, "The config could not be saved when the plugin stopped, so its last change is not in the file");
            }
        }
    }
}
