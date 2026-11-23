#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Advanced_PB_Limiter.QuasarContracts
{
    /// <summary>
    /// Which failure reasons are worth a log line. Quasar retries every few seconds, and a lagging or broken server answers
    /// with the same few reasons over and over, sometimes in turn and sometimes between successes: each reason is logged
    /// once per key, and again only once <see cref="Window"/> has passed since. The newest <see cref="PerKey"/> reasons are
    /// kept per key, for at most <see cref="MaxKeys"/> keys. Thread-safe. Compiled into the companion, the Quasar plugin and the Magnetar plugin (for companions before 1.3.3), not the Torch plugin.
    /// </summary>
    public sealed class RecentReasons
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        public const int PerKey = 4;
        public const int MaxKeys = 1024;

        private readonly object _lock = new object();
        private readonly Dictionary<string, List<KeyValuePair<string, long>>> _byKey = new Dictionary<string, List<KeyValuePair<string, long>>>(StringComparer.Ordinal);
        private readonly Func<long> _nowMs;

        public RecentReasons() : this(() => Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000))
        {
        }

        /// <summary>With a clock in milliseconds, for tests.</summary>
        public RecentReasons(Func<long> nowMs)
        {
            _nowMs = nowMs;
        }

        /// <summary>True, and the reason remembered, when it was not logged for this key within <see cref="Window"/>.</summary>
        public bool IsNew(string key, string reason)
        {
            long now = _nowMs();
            long window = (long)Window.TotalMilliseconds;
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out List<KeyValuePair<string, long>>? recent))
                {
                    if (_byKey.Count >= MaxKeys)
                        Prune(now, window);
                    recent = new List<KeyValuePair<string, long>>(PerKey);
                    _byKey[key] = recent;
                }

                for (int i = 0; i < recent.Count; i++)
                {
                    if (!string.Equals(recent[i].Key, reason, StringComparison.Ordinal))
                        continue;
                    if (now - recent[i].Value < window)
                        return false;
                    recent.RemoveAt(i);
                    break;
                }

                if (recent.Count == PerKey)
                    recent.RemoveAt(0);
                recent.Add(new KeyValuePair<string, long>(reason, now));
                return true;
            }
        }

        /// <summary>Forgets a key that is done with, a profile that was applied say.</summary>
        public void Forget(string key)
        {
            lock (_lock)
                _byKey.Remove(key);
        }

        private static bool AllPast(List<KeyValuePair<string, long>> reasons, long now, long window)
        {
            for (int i = 0; i < reasons.Count; i++)
            {
                if (now - reasons[i].Value < window)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Drops the keys whose reasons are all past the window, or when every one is still recent, the key logged least
        /// recently, so the others keep their reasons.
        /// </summary>
        private void Prune(long now, long window)
        {
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, List<KeyValuePair<string, long>>> entry in _byKey)
            {
                if (AllPast(entry.Value, now, window))
                    stale.Add(entry.Key);
            }

            if (stale.Count == 0)
            {
                // More keys failing at once than kept: the one logged least recently may be logged again early, never missed.
                string? oldest = null;
                long oldestAt = long.MaxValue;
                foreach (KeyValuePair<string, List<KeyValuePair<string, long>>> entry in _byKey)
                {
                    long newest = entry.Value.Count == 0 ? long.MinValue : entry.Value[entry.Value.Count - 1].Value;
                    if (newest < oldestAt)
                    {
                        oldestAt = newest;
                        oldest = entry.Key;
                    }
                }
                if (oldest is not null)
                    stale.Add(oldest);
            }
            foreach (string key in stale)
                _byKey.Remove(key);
        }
    }
}
