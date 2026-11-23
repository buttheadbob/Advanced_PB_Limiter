using System;
using System.Threading;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>Wakes the loops the profile queue and the Live view run.</summary>
    internal static class Signals
    {
        /// <summary>
        /// Wakes a loop waiting on <paramref name="wake"/> (a SemaphoreSlim with a maximum of 1) now instead of at its next
        /// pass: once, however often it is called before the loop runs, and nothing once it is disposed.
        /// </summary>
        public static void Wake(SemaphoreSlim wake)
        {
            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already woken.
            }
            catch (ObjectDisposedException)
            {
                // Stopping.
            }
        }
    }
}
