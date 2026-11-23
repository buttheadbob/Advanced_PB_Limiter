#nullable enable
using System;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>
/// A Quasar profile this server can never apply, whatever the try: one that can't be read, or holds a value the setting
/// does not allow. QuasarBridge answers it as refused, so Quasar stops sending it, rather than as a failure it retries.
/// </summary>
internal sealed class ProfileRejectedException : InvalidOperationException
{
    public ProfileRejectedException(string message, Exception inner) : base(message, inner)
    {
    }
}
