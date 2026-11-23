#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Replaces a file through a complete copy written beside it, so a crash or kill at any point leaves the old file, the
    /// new file, or a complete copy that <see cref="Recover"/> puts back. Writes and recoveries of the same file run one at
    /// a time, also across copies of the plugin's assembly in one process (an old and a new one after a Torch reload, say),
    /// as the lock is a named mutex rather than a static field each copy has its own of.
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

        // Windows is the only system with this separator; Magnetar compiles this file without the .NET version symbols.
        private static readonly bool OnWindows = Path.DirectorySeparatorChar == '\\';

        /// <summary>
        /// Holds the lock of <paramref name="path"/> until disposed, for a caller that reads and writes it as one step with
        /// <see cref="WriteHeld"/> and <see cref="RecoverHeld"/>. Not taken again by the same caller: those don't take it.
        /// </summary>
        public static IDisposable Hold(string path)
        {
            string key = Path.GetFullPath(path);
            if (OnWindows) key = key.ToUpperInvariant();
            // A mutex name can't hold a path, so it holds a hash of it (64-bit FNV-1a over UTF-8). Two paths with the same
            // hash only wait for each other.
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(key))
                hash = unchecked((hash ^ b) * 1099511628211UL);
            return new Held("apbl-safefile-" + hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Where a named mutex can't be made (on Linux .NET keeps them under $HOME, which may be missing or read-only), a
        // lock per name in this copy of the assembly is used instead: it still keeps this plugin's own writes apart.
        private static readonly Dictionary<string, object> FallbackLocks = new(StringComparer.Ordinal);

        private sealed class Held : IDisposable
        {
            private readonly Mutex? _mutex;
            private readonly object? _fallback;

            public Held(string name)
            {
                try
                {
                    _mutex = new Mutex(false, name);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
                {
                    lock (FallbackLocks)
                    {
                        if (!FallbackLocks.TryGetValue(name, out _fallback))
                            FallbackLocks[name] = _fallback = new object();
                    }
                    Monitor.Enter(_fallback);
                    return;
                }

                try
                {
                    _mutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    // A thread that held it ended without letting go; this one holds it now, and the file is checked as usual.
                }
                catch
                {
                    _mutex.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (_fallback is not null)
                {
                    Monitor.Exit(_fallback);
                    return;
                }
                _mutex!.ReleaseMutex();
                _mutex.Dispose();
            }
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="temp"/>, flushed to disk, then swaps it in for
        /// <paramref name="path"/> with one rename. Where the swap fails, for example a file mounted on its own into a
        /// container or held open by another program, the file is written in place and the copy is kept until that write
        /// is done, so a write cut short is repaired by <see cref="Recover"/>. Where the copy can't be written, a full disk
        /// say, the file is left as it is. While the file itself is missing or not whole, a complete copy an earlier write
        /// left at <paramref name="temp"/> that differs from <paramref name="content"/> may be the only complete copy, so it
        /// is first moved to a name of its own, <paramref name="kept"/>; if it can't be moved, nothing is written. Beside a
        /// whole file such a copy is an earlier write that never landed, and <paramref name="content"/> replaces it. Throws IOException or
        /// UnauthorizedAccessException when nothing could be written. Returns the swap error when the file was written in
        /// place, otherwise null.
        /// </summary>
        public static Exception? Write(string path, string temp, MemoryStream content, Func<byte[], bool> isComplete, out string? kept)
        {
            using (Hold(path))
                return WriteHeld(path, temp, content, isComplete, out kept);
        }

        /// <summary>As <see cref="Write"/>, for a caller already in <see cref="Hold"/>.</summary>
        public static Exception? WriteHeld(string path, string temp, MemoryStream content, Func<byte[], bool> isComplete, out string? kept)
        {
            kept = null;
            if (File.Exists(temp) && !IsWhole(path, isComplete))
            {
                byte[] earlier = File.ReadAllBytes(temp);
                if (isComplete(earlier) && !earlier.SequenceEqual(content.ToArray()))
                    kept = KeptFile.Keep(temp, ".kept");
            }
            string keptNote = kept is null ? "" : $" A complete copy an earlier write left was kept as {kept}.";

            try
            {
                WriteFile(temp, content, FileShare.None);
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
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                swapError = e;
            }

            try
            {
                WriteFile(path, content, FileShare.Read);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{path} could not be swapped into place ({swapError.Message}) or written in place ({e.Message}). A complete copy is kept at {temp}.{keptNote}", e);
            }
            DeleteIfPresent(temp);
            return swapError;
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
        /// from a complete file is moved to a name the next write does not use, <paramref name="kept"/>: it may hold the
        /// admin's newest settings. If it can't be moved, the result is KeepFailed and <paramref name="kept"/> says why. A
        /// copy that can't be read now is left for the next start. A file that is there but not whole is moved aside before
        /// the copy replaces it, and <paramref name="kept"/> then names where. <paramref name="isComplete"/> tells a whole
        /// file from one cut short. Throws only when a file that is missing or cut short could not be restored; the file is
        /// then put back as it was where it can be, the copy is kept for the next start, and the message says which.
        /// </summary>
        public static Recovery Recover(string path, string temp, Func<byte[], bool> isComplete, out string? kept)
        {
            using (Hold(path))
                return RecoverHeld(path, temp, isComplete, out kept);
        }

        /// <summary>As <see cref="Recover"/>, for a caller already in <see cref="Hold"/>.</summary>
        public static Recovery RecoverHeld(string path, string temp, Func<byte[], bool> isComplete, out string? kept)
        {
            kept = null;
            if (!File.Exists(temp)) return Recovery.None;
            byte[] copy;
            byte[]? current;
            try
            {
                copy = File.ReadAllBytes(temp);
                current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Recovery.None; // Held by another program, say; both are left for the next start.
            }
            if (!isComplete(copy))
            {
                DeleteIfPresent(temp);
                return Recovery.None;
            }

            if (current is not null && isComplete(current))
            {
                if (current.SequenceEqual(copy))
                {
                    DeleteIfPresent(temp);
                    return Recovery.None;
                }
                try
                {
                    kept = KeptFile.Keep(temp, ".kept");
                    return Recovery.CopyKept;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    kept = e.Message; // The file itself is complete and is read as it is.
                    return Recovery.KeepFailed;
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
                    using (MemoryStream complete = new(copy, false))
                        WriteFile(path, complete, FileShare.Read);
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
            return Recovery.Restored;
        }

        /// <summary>
        /// True when the copy beside a missing file may still restore it at the next start: it is complete, or it can't be read
        /// now (held by another program, say) and so may be. A copy that is there but cut short can never restore anything.
        /// </summary>
        public static bool MayRestore(string copy, Func<byte[], bool> isComplete)
        {
            try
            {
                return isComplete(File.ReadAllBytes(copy));
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
        public static bool DropIfSame(string copyPath, byte[] content)
        {
            byte[] copy;
            try
            {
                copy = File.ReadAllBytes(copyPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            if (!copy.SequenceEqual(content)) return false;
            DeleteIfPresent(copyPath);
            return !File.Exists(copyPath);
        }

        /// <summary>The log line for a <see cref="Recover"/> result, or null when there is nothing to say.</summary>
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
        private static bool IsWhole(string path, Func<byte[], bool> isComplete)
        {
            if (!File.Exists(path)) return false;
            try
            {
                return isComplete(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static void WriteFile(string path, MemoryStream content, FileShare share)
        {
            using FileStream stream = new(path, FileMode.Create, FileAccess.Write, share);
            content.WriteTo(stream);
            stream.Flush(true);
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
