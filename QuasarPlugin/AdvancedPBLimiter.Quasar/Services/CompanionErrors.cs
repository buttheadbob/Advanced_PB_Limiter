using System;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>The companion channel's own failures, for the profile queue's log lines and retry decision.</summary>
internal static class CompanionErrors
{
    /// <summary>
    /// A server started before this plugin was enabled has no companion until it restarts, which Quasar's agent reports
    /// as "Companion plugin '...' is not loaded or does not expose a Quasar handler." Other errors come from the server or
    /// the companion itself; a restart would not help, so they get no hint.
    /// </summary>
    public static string RestartHint(Exception e) =>
        e.Message.Contains("is not loaded or does not expose a Quasar handler", StringComparison.Ordinal)
            ? " If the server was started before this plugin was enabled, it needs a restart."
            : "";

    /// <summary>
    /// Quasar's own failures for a command whose answer it stopped waiting for after it was handed to the agent: the agent
    /// disconnected, or connected again, before it answered. The server may have carried it out.
    /// </summary>
    public static bool MayHaveArrived(Exception e) =>
        e is InvalidOperationException
        && (e.Message == "Agent disconnected before responding to the command." || e.Message == "Agent connection was replaced.");
}
