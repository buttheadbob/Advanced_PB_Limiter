#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Replaces a file through a complete copy written beside it, so a crash or kill at any point leaves the old file, the
    /// new file, or a complete copy that <see cref="RecoverAsync"/> puts back. Writes and recoveries of the same file run
    /// one at a time: within this copy of the assembly through a gate per file, and across processes, and copies of the
    /// assembly in one process (an old and a new one after a Torch reload), through &lt;file&gt;.lock beside it, opened for
    /// one holder at a time, which the OS lets go when the holder closes it or exits. The lock is held across awaits, so it
    /// is not a thread-affine mutex.
    /// </summary>
    public static class SafeFile
    {
        public enum Recovery
        {
            /// <summary>Nothing was left over, or only a copy that was not complete, which is removed.</summary>
            None,
            /// <summary>The file was missing or cut short by an interrupted write and was restored from the complete copy.</summary>
            Restored,
            /// <summary>A complete copy that differs from the complete file in use was moved to a name of its own.</summary>
            CopyKept,
            /// <summary>Such a copy could not be moved to a name of its own, so it is still at the copy's name.</summary>
            KeepFailed
        }

        /// <summary>The outcome of <see cref="WriteAsync"/>: the swap error when the file was written in place, else null, and the name a kept copy got.</summary>
        public readonly struct WriteResult
        {
            public Exception? SwapError { get; }
            public string? Kept { get; }

            public WriteResult(Exception? swapError, string? kept)
            {
                SwapError = swapError;
                Kept = kept;
            }
        }

        /// <summary>The outcome of <see cref="RecoverAsync"/>, with the name a kept copy got, or why it could not be kept.</summary>
        public readonly struct RecoverResult
        {
            public Recovery Recovery { get; }
            public string? Kept { get; }

            public RecoverResult(Recovery recovery, string? kept)
            {
                Recovery = recovery;
                Kept = kept;
            }
        }

        // Windows is the only system with this separator. Case-insensitive paths only there.
        private static readonly bool OnWindows = Path.DirectorySeparatorChar == '\\';

        // How long a lock another process holds is waited for. A write or recovery takes milliseconds, so a lock held longer
        // than this is stuck, or held by something other than this plugin.
        private const int LockWaitSeconds = 15;

        // One gate per file, keyed by its full path (case-insensitive on Windows), in this copy of the assembly.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

        // Folders found to hold a lock file, and folders already reported, by full path.
        private static readonly ConcurrentDictionary<string, byte> FolderHolds = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);

        /// <summary>
        /// Where the host's log takes a warning from this class: a folder where no lock file can be made, said once per
        /// folder. The plugin sets it as it loads; null says nothing.
        /// </summary>
        public static Action<string>? Warning { get; set; }

        /// <summary>
        /// Holds the lock of <paramref name="path"/> until disposed, for a caller that reads and writes it as one step with
        /// <see cref="WriteHeldAsync"/> and <see cref="RecoverHeldAsync"/>. Not taken again by the same caller: those don't take it.
        /// Where no lock file can be made beside the file (a read-only folder, no access), only the gate in this process is
        /// held, and <see cref="Warning"/> says so once for that folder; a folder that is not there yet holds no file to
        /// protect, and is not reported. Throws IOException when another process holds the lock file longer than
        /// <see cref="LockWaitSeconds"/>.
        /// </summary>
        public static async Task<IDisposable> HoldAsync(string path)
        {
            SemaphoreSlim gate = Gates.GetOrAdd(GateKey(path), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            LockHold hold = new(gate);
            try
            {
                await hold.OpenLockFileAsync(path + ".lock").ConfigureAwait(false);
                return hold;
            }
            catch
            {
                hold.Dispose();
                throw;
            }
        }

        private static string GateKey(string path)
        {
            string key = Path.GetFullPath(path);
            return OnWindows ? key.ToUpperInvariant() : key;
        }

        /// <summary>The gate of one file in this process, and the lock file beside it once open; both are let go on Dispose.</summary>
        private sealed class LockHold : IDisposable
        {
            private readonly SemaphoreSlim _gate;
            private FileStream? _lockFile;
            private int _released;

            public LockHold(SemaphoreSlim gate) => _gate = gate;

            /// <summary>
            /// Opens <paramref name="lockPath"/> for this holder alone, creating it if needed. The OS refuses any other open of
            /// it until this one closes, also from this process: a share mode on Windows, an flock elsewhere, which works where
            /// byte-range locks do not, as on a Windows folder mounted into a Linux container. Retries only while another
            /// holder has it, up to <see cref="LockWaitSeconds"/>. On Linux a file that is held and a folder that is read-only
            /// fail alike, so the first failure checks the folder with a file under a new name, which no one else can hold.
            /// </summary>
            public async Task OpenLockFileAsync(string lockPath)
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(lockPath)) ?? "";
                DateTime giveUp = DateTime.UtcNow.AddSeconds(LockWaitSeconds);
                bool folderChecked = false;
                while (true)
                {
                    try
                    {
                        Open(lockPath);
                        return;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // No folder yet, so no file in it for anyone to write: a fresh install, before the first save makes it.
                        return;
                    }
                    catch (Exception e) when (e is UnauthorizedAccessException or NotSupportedException)
                    {
                        Report(folder, $"No lock file can be made at {lockPath} ({e.Message})");
                        return;
                    }
                    catch (IOException e)
                    {
                        if (!folderChecked)
                        {
                            folderChecked = true;
                            if (!CanHoldFiles(folder))
                            {
                                Report(folder, $"No lock file can be made at {lockPath} ({e.Message})");
                                return;
                            }
                        }
                        if (DateTime.UtcNow >= giveUp)
                            throw new IOException($"{lockPath} stayed held by another process for {LockWaitSeconds.ToString(CultureInfo.InvariantCulture)} seconds ({e.Message}), so the file was not written. "
                                                  + "Only this plugin opens it: check that no second copy of the plugin, or another server, uses the same folder.", e);
                        // Another holder has it, an antivirus scan say: the OS lets go when that holder closes it.
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                }
            }

            // Called once per hold, so no lock file is open yet; one would be closed first, so this holder owns exactly one.
            // Synchronous on purpose: it runs inside the async retry loop, where closing a stream would otherwise block it.
            private void Open(string lockPath)
            {
                _lockFile?.Dispose();
                _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                _lockFile?.Dispose();
                _lockFile = null;
                _gate.Release();
            }
        }

        /// <summary>
        /// True when a file can be made and held in <paramref name="folder"/>: one made there under a new name, which no other
        /// process can be holding, is opened for this process alone and removed. A folder that can is remembered; one that
        /// can't is checked again at its next conflict, as a full disk or a scan may have been passing.
        /// </summary>
        private static bool CanHoldFiles(string folder)
        {
            if (FolderHolds.ContainsKey(folder)) return true;
            string probe = Path.Combine(folder, ".apbl-lock-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                    FolderHolds.TryAdd(folder, 0);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static void Report(string folder, string reason)
        {
            if (!Reported.TryAdd(folder, 0)) return;
            Warning?.Invoke(reason + ". Writes of files in " + folder + " are not locked against other processes; within this process they still run one at a time.");
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="temp"/>, flushed to disk, then swaps it in for
        /// <paramref name="path"/> with one rename. Where the swap fails, for example a file mounted on its own into a
        /// container or held open by another program, the file is written in place and the copy is kept until that write
        /// is done, so a write cut short is repaired by <see cref="RecoverAsync"/>. Where the copy can't be written, a full
        /// disk say, the file is left as it is. While the file itself is missing or not whole, a complete copy an earlier
        /// write left at <paramref name="temp"/> that differs from <paramref name="content"/> may be the only complete copy,
        /// so it is first moved to a name of its own, <see cref="WriteResult.Kept"/>; if it can't be moved, nothing is
        /// written. Beside a whole file such a copy is an earlier write that never landed, and <paramref name="content"/>
        /// replaces it. Throws IOException or UnauthorizedAccessException when nothing could be written. Returns the swap
        /// error when the file was written in place, otherwise null.
        /// </summary>
        public static async Task<WriteResult> WriteAsync(string path, string temp, byte[] content, Func<byte[], bool> isComplete)
        {
            using (IDisposable hold = await HoldAsync(path).ConfigureAwait(false))
                return await WriteHeldAsync(path, temp, content, isComplete).ConfigureAwait(false);
        }

        /// <summary>As <see cref="WriteAsync"/>, for a caller already in <see cref="HoldAsync"/>.</summary>
        public static async Task<WriteResult> WriteHeldAsync(string path, string temp, byte[] content, Func<byte[], bool> isComplete)
        {
            string? kept = null;
            if (File.Exists(temp) && !await IsWholeAsync(path, isComplete).ConfigureAwait(false))
            {
                byte[] earlier = await ReadAllBytesAsync(temp).ConfigureAwait(false);
                if (isComplete(earlier) && !earlier.SequenceEqual(content))
                    kept = KeptFile.Keep(temp, ".kept");
            }
            string keptNote = kept is null ? "" : $" A complete copy an earlier write left was kept as {kept}.";

            try
            {
                await WriteFileAsync(temp, content, FileShare.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DeleteIfPresent(temp);
                if (kept is null) throw;
                throw new IOException($"{temp} could not be written ({e.Message}).{keptNote}", e);
            }

            Exception swapError;
            try
            {
                Replace(temp, path);
                return new WriteResult(null, kept);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                swapError = e;
            }

            try
            {
                await WriteFileAsync(path, content, FileShare.Read).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{path} could not be swapped into place ({swapError.Message}) or written in place ({e.Message}). A complete copy is kept at {temp}.{keptNote}", e);
            }
            DeleteIfPresent(temp);
            return new WriteResult(swapError, kept);
        }

        /// <summary>
        /// Moves <paramref name="temp"/> over <paramref name="path"/> in one step. File.Replace on Windows moves the old file
        /// away first, so a crash there could leave no file; MoveFileEx replaces it in one step. Elsewhere File.Replace is a
        /// single rename. Fails with IOException or UnauthorizedAccessException.
        /// </summary>
        public static void Replace(string temp, string path)
        {
            if (OnWindows)
            {
                // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
                if (!MoveFileEx(temp, path, 9))
                {
                    int error = Marshal.GetLastWin32Error();
                    string message = $"{new Win32Exception(error).Message} : '{path}'";
                    if (error == 5) throw new UnauthorizedAccessException(message);
                    throw new IOException(message, unchecked((int)0x80070000 | (error & 0xFFFF)));
                }
            }
            else if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        /// <summary>
        /// Deals with a copy a write left beside the file, before the file is read. A copy that is not complete is removed.
        /// A complete copy puts back a file that is missing or was cut short, by moving the copy into place, or writing it in
        /// place when that fails; the copy is removed only once the file is complete again. A complete copy that differs
        /// from a complete file is moved to a name the next write does not use, <see cref="RecoverResult.Kept"/>: it may hold
        /// the admin's newest settings. If it can't be moved, the result is KeepFailed and <see cref="RecoverResult.Kept"/>
        /// says why. A copy that can't be read now is left for the next start. A file that is there but not whole is moved
        /// aside before the copy replaces it, and <see cref="RecoverResult.Kept"/> then names where.
        /// <paramref name="isComplete"/> tells a whole file from one cut short. Throws only when a file that is missing or cut
        /// short could not be restored; the file is then put back as it was where it can be, the copy is kept for the next
        /// start, and the message says which.
        /// </summary>
        public static async Task<RecoverResult> RecoverAsync(string path, string temp, Func<byte[], bool> isComplete)
        {
            using (IDisposable hold = await HoldAsync(path).ConfigureAwait(false))
                return await RecoverHeldAsync(path, temp, isComplete).ConfigureAwait(false);
        }

        /// <summary>As <see cref="RecoverAsync"/>, for a caller already in <see cref="HoldAsync"/>.</summary>
        public static async Task<RecoverResult> RecoverHeldAsync(string path, string temp, Func<byte[], bool> isComplete)
        {
            string? kept = null;
            if (!File.Exists(temp)) return new RecoverResult(Recovery.None, null);
            byte[] copy;
            byte[]? current;
            try
            {
                copy = await ReadAllBytesAsync(temp).ConfigureAwait(false);
                current = File.Exists(path) ? await ReadAllBytesAsync(path).ConfigureAwait(false) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new RecoverResult(Recovery.None, null); // Held by another program, say; both are left for the next start.
            }
            if (!isComplete(copy))
            {
                DeleteIfPresent(temp);
                return new RecoverResult(Recovery.None, null);
            }

            if (current is not null && isComplete(current))
            {
                if (current.SequenceEqual(copy))
                {
                    DeleteIfPresent(temp);
                    return new RecoverResult(Recovery.None, null);
                }
                try
                {
                    kept = KeptFile.Keep(temp, ".kept");
                    return new RecoverResult(Recovery.CopyKept, kept);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    kept = e.Message; // The file itself is complete and is read as it is.
                    return new RecoverResult(Recovery.KeepFailed, kept);
                }
            }

            // A file that is there but not whole may be a hand edit rather than a write cut short, so it is moved aside first.
            // A crash between the two moves leaves the file missing and the copy complete, which the next start restores.
            if (current is not null)
            {
                try
                {
                    kept = KeptFile.Keep(path, ".before-restore");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    kept = null; // It is replaced below as it is.
                }
            }
            try
            {
                Replace(temp, path);
            }
            catch (Exception swapError) when (swapError is IOException or UnauthorizedAccessException)
            {
                try
                {
                    await WriteFileAsync(path, copy, FileShare.Read).ConfigureAwait(false);
                }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
                {
                    // Neither way worked: the file goes back as it was, so nothing is left missing or half written, and the
                    // complete copy stays at its name for the next start.
                    string putBack = PutBack(path, kept, current is not null);
                    throw new IOException($"{path} could not be restored from the complete copy at {temp}: it could not be swapped in ({swapError.Message}) "
                                          + $"or written in place ({writeError.Message}). {putBack} The copy is kept for the next start.", writeError);
                }
                DeleteIfPresent(temp);
            }
            return new RecoverResult(Recovery.Restored, kept);
        }

        /// <summary>
        /// True when the copy beside a missing file may still restore it at the next start: it is complete, or it can't be read
        /// now (held by another program, say) and so may be. A copy that is there but cut short can never restore anything.
        /// </summary>
        public static async Task<bool> MayRestoreAsync(string copy, Func<byte[], bool> isComplete)
        {
            try
            {
                return isComplete(await ReadAllBytesAsync(copy).ConfigureAwait(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return File.Exists(copy);
            }
        }

        /// <summary>
        /// After a failed restore: the file that was moved aside goes back to its name, over anything the failed write left.
        /// Says what is at the file's name now.
        /// </summary>
        private static string PutBack(string path, string? movedAside, bool hadFile)
        {
            if (movedAside is null && !hadFile)
            {
                DeleteIfPresent(path); // Anything there is part of the failed write.
                return "There is no file there, as before.";
            }
            if (movedAside is null)
                return "The file, which was not whole and could not be moved aside, may now hold part of the copy.";
            try
            {
                DeleteIfPresent(path);
                File.Move(movedAside, path);
                return "The file is back as it was.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"The file that was there is at {movedAside} and could not be moved back ({e.Message}).";
            }
        }

        /// <summary>
        /// Removes a leftover copy when it holds exactly what is about to be written: it was the same write, interrupted, so
        /// it holds nothing the admin could lose. True when it is gone.
        /// </summary>
        public static async Task<bool> DropIfSameAsync(string copyPath, byte[] content)
        {
            byte[] copy;
            try
            {
                copy = await ReadAllBytesAsync(copyPath).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            if (!copy.SequenceEqual(content)) return false;
            DeleteIfPresent(copyPath);
            return !File.Exists(copyPath);
        }


        /// <summary>The log line for a <see cref="RecoverAsync"/> result, or null when there is nothing to say.</summary>
        public static string? Describe(Recovery recovery, string path, string temp, string? kept)
        {
            return recovery switch
            {
                Recovery.Restored when kept is not null => $"{path} was not a whole file and was restored from the complete copy an interrupted write left at {temp}. The file it replaced was moved to {kept}.",
                Recovery.Restored => $"{path} was missing or cut short after an interrupted write and was restored from the complete copy at {temp}.",
                Recovery.KeepFailed => $"A complete copy of {path} from an interrupted write differs from the file in use and was left at {temp}, as it could not be moved to a name of its own ({kept}). Move it elsewhere if it holds settings you want.",
                Recovery.CopyKept => $"A complete copy of {path} from an interrupted write differs from the file in use and was kept as {kept}, in case it holds settings you want. Delete it when it is not needed.",
                _ => null
            };
        }

        /// <summary>
        /// True when reading a file failed for the file system, not for what it holds: an I/O or access error, also when
        /// XmlSerializer wraps one met part way through the read in an InvalidOperationException, once per serializer: a
        /// config whose properties are read by serializers of their own wraps it more than once. Such a file is left where
        /// it is, never set aside as unreadable.
        /// </summary>
        public static bool IsReadFailure(Exception e)
        {
            for (Exception? current = e; current is not null; current = current.InnerException)
            {
                if (current is IOException or UnauthorizedAccessException) return true;
                if (current is not InvalidOperationException) return false;
            }
            return false;
        }

        /// <summary>True when the bytes are a whole XML document with a root element, not one cut short.</summary>
        public static bool IsWholeXml(byte[] bytes)
        {
            try
            {
                using MemoryStream read = new(bytes, false);
                using XmlReader reader = SafeXml.Reader(read);
                return XDocument.Load(reader).Root is not null;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        /// <summary>
        /// False when the file is missing or not whole. A file that exists but can't be read now, held by another program
        /// say, counts as whole: it was not cut short by a write of ours, which would have left it readable.
        /// </summary>
        public static async Task<bool> IsWholeAsync(string path, Func<byte[], bool> isComplete)
        {
            if (!File.Exists(path)) return false;
            try
            {
                return isComplete(await ReadAllBytesAsync(path).ConfigureAwait(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>The bytes of a file, for <see cref="InstantPunishmentFile"/>, which reads and writes it as one step.</summary>
        internal static Task<byte[]> ReadAllBytesAsync(string path)
        {
#if NETCOREAPP
            return File.ReadAllBytesAsync(path);
#else
            return ReadAllBytesFrameworkAsync(path);
#endif
        }

#if !NETCOREAPP
        private static async Task<byte[]> ReadAllBytesFrameworkAsync(string path)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using MemoryStream memory = new();
            await stream.CopyToAsync(memory).ConfigureAwait(false);
            return memory.ToArray();
        }
#endif

        /// <summary>
        /// Writes <paramref name="bytes"/> to <paramref name="path"/>, replacing it. Write-through, so the data is on disk when
        /// the write completes, before any rename puts it in place.
        /// </summary>
        private static async Task WriteFileAsync(string path, byte[] bytes, FileShare share)
        {
            const FileOptions options = FileOptions.Asynchronous | FileOptions.WriteThrough;
#if NETCOREAPP
            await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, share, 4096, options);
#else
            using FileStream stream = new(path, FileMode.Create, FileAccess.Write, share, 4096, options);
#endif
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static void DeleteIfPresent(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only a copy left beside the file; it is dealt with at the next start.
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string source, string destination, int flags);
    }
}
