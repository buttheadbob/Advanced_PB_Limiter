using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Utils;
using Sandbox;
using Sandbox.ModAPI.Ingame;

namespace Advanced_PB_Limiter.Commands
{
    /// <summary>What the chat commands do. The Torch and Magnetar command classes only declare them and pass the reply on.</summary>
    public static class LimiterCommands
    {
        private static readonly LimiterLog Log = new("Commands");
        private static IPbLimiterConfig Config => LimiterCore.Config;

        public const string PlayersOnlyMessage = "This command can only be used by players in game.";
        public const string StaffCommandsDisabledMessage = "Staff commands are disabled in the config.  Please enable them to use this command.";
        private const int TimingsDurationMs = 5000;

        public static IEnumerable<string> About()
        {
            yield return "Advanced PB Limiter v" + LimiterCore.Host.Version.TrimStart('v') + " by SentorX"; // Torch's version has a v of its own.
            yield return "This is a plugin that limits the runtime and memory usage of programmable blocks to prevent server lag.";
            yield return "You can also generate detailed reports of your server with this plugin installed.";
            yield return "Advanced PB Limiter is open source and can be found on GitHub: https://github.com/buttheadbob/Advanced_PB_Limiter";
            yield return "For any issues, please contact SentorX through his Discord server.  DM's will be ignored unless you are a server owner or admin.";
            yield return "Discord Invite: https://discord.gg/rSuxGrHrrt";
        }

        /// <param name="steamId">The caller, or null for the console.</param>
        public static string Limits(ulong? steamId)
        {
            StringBuilder sb = new();
            if (steamId is null || !Config.TryGetPrivilegedPlayer(steamId.Value, out PrivilegedPlayer? privileged) || privileged is null)
            {
                sb.AppendLine();
                sb.AppendLine("Server Limits");
                sb.AppendLine("--------------");
                sb.AppendLine("Max PB Runtime: " + Config.MaxRunTimeMS + "ms");
                sb.AppendLine("Max PB Runtime Average: " + Config.MaxRunTimeMSAvg + "ms");
                sb.AppendLine("Max PB Runtime (All Pb's Combined): " + Config.MaxAllBlocksCombinedRunTimeMS + "ms");
                sb.AppendLine("Max PB Runtime Average (All Pb's Combined): " + Config.MaxAllBlocksCombinedRunTimeMSAvg + "ms");
                sb.AppendLine("Max Offences Before Punishment: " + Config.MaxOffencesBeforePunishment);
                sb.AppendLine("Punishment Type: " + Config.Punishment);
                sb.AppendLine("Graceful Shutdown Period: " + Config.GracefulShutDownRequestDelay + " seconds.");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("You are a privileged user.  Your limits are:");
            sb.AppendLine("-----------------------");
            sb.AppendLine("Max PB Runtime: " + privileged.RuntimeAllowance + "ms");
            sb.AppendLine("Max PB Runtime Average: " + privileged.RuntimeAverageAllowance + "ms");
            sb.AppendLine("Combined Limits Active: " + !privileged.NoCombinedLimits);
            sb.AppendLine("Max PB Runtime (All Pb's Combined): " + privileged.CombinedRuntimeAllowance + "ms");
            sb.AppendLine("Max PB Runtime Average (All Pb's Combined): " + privileged.CombinedRuntimeAverageAllowance + "ms");
            sb.AppendLine("Max Offences Before Punishment: " + privileged.OffencesBeforePunishment);
            sb.AppendLine("Punishment Type: " + privileged.Punishment);
            sb.AppendLine("Graceful Shutdown Period: " + privileged.GracefulShutDownRequestDelay + " seconds.");
            return sb.ToString();
        }

        /// <summary>Generates the caller's report on another thread. <paramref name="respond"/> is called from that thread.</summary>
        public static void MyStats(ulong steamId, Action<string> respond)
        {
            RunInBackground(() => ReportManager.PlayerStatsRequest(steamId, respond));
        }

        /// <summary>Lists the caller's runtimes on another thread. <paramref name="respond"/> is called from that thread.</summary>
        public static void Runtimes(long identityId, Action<string> respond)
        {
            RunInBackground(() => ReportManager.PlayerRuntimesRequest(identityId, respond));
        }

        /// <summary>Generates the server report on another thread. <paramref name="respond"/> is called from that thread.</summary>
        public static void GetReport(Action<string> respond)
        {
            RunInBackground(() => ReportManager.GetReport(respond));
        }

        // Fire and forget. Nothing waits on the task, so a failure would otherwise go unseen.
        private static void RunInBackground(Action action)
        {
            Task.Run(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Error(e, "A report failed");
                }
            });
        }

        public static void RequestPbShutdown(int seconds)
        {
            MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be forced on the game thread.
            {
                foreach (TrackedPlayer? tplayer in TrackingManager.PlayersTracked.Values)
                {
                    if (tplayer == null) continue;
                    foreach (TrackedPBBlock? pbBlock in tplayer.GetAllPBBlocks)
                    {
                        pbBlock?.ProgrammableBlock?.Run($"GracefulShutDown::{seconds}", UpdateType.Script);
                    }
                }
            }, "Advanced_PB_Limiter");
        }

        public static string SetDebug(bool debug)
        {
            Config.DebugReporting = debug;
            return "Debug mode is now " + (debug ? "enabled." : "disabled.");
        }

        /// <summary>Logs the limiter's own timings for a few seconds. Too noisy to leave on.</summary>
        public static string ShowTimings()
        {
            Config.ShowTimingsBurp = true;
            Task.Delay(TimingsDurationMs).ContinueWith(_ => Config.ShowTimingsBurp = false);
            return $"Logging timings for {TimingsDurationMs / 1000} seconds.";
        }

        public static string SetEnabled(bool enabled)
        {
            Config.Enabled = enabled;
            return "Advanced PB Limiter is now " + (enabled ? "enabled." : "disabled.");
        }

        public static string SetStaffCommandsAllowed(bool allowed)
        {
            Config.AllowStaffCommands = allowed;
            LimiterCore.Host.SaveConfig();
            return "Staff commands are now " + (allowed ? "enabled." : "disabled.");
        }

        public static string CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        {
            if (!Config.AllowStaffCommands)
                return StaffCommandsDisabledMessage;

            bool exists = Config.TryGetPrivilegedPlayer(steamId, out PrivilegedPlayer? existing);

            // Anything the command does not set stays as it was.
            PrivilegedPlayer player = existing ?? new PrivilegedPlayer { SteamId = steamId };
            player.RuntimeAllowance = maxRunTimeMS;
            player.RuntimeAverageAllowance = maxRunTimeMSAvg;
            player.NoCombinedLimits = !combinedLimits;
            player.CombinedRuntimeAllowance = maxAllBlocksCombinedRunTimeMS;
            player.CombinedRuntimeAverageAllowance = maxAllBlocksCombinedRunTimeMSAvg;
            player.OffencesBeforePunishment = offencesBeforePunishment;
            player.Punishment = punishment;
            player.GracefulShutDownRequestDelay = gracefulShutDownRequestDelay;

            Config.SetPrivilegedPlayer(player);
            LimiterCore.Host.SaveConfig();
            return exists ? "Privileged user updated." : "Privileged user created.";
        }

        public static IEnumerable<string> DetailedStaffHelp()
        {
            yield return "Advanced PB Limiter Staff Commands:";
            yield return "!PBLimiter GetReport - Generates a full report of this server.";
            yield return "!PBLimiter Disable - Disables the Advanced PB Limiter.";
            yield return "!PBLimiter Enable - Enables the Advanced PB Limiter.";
            yield return "!PBLimiter AllowStaffCommands - Allows staff commands to be used. Only the server owner can use this command.";
            yield return "!PBLimiter DenyStaffCommands - Disables staff commands from being used. Only the server owner can use this command.";
        }

        public static IEnumerable<string> StaffHelp()
        {
            yield return "Advanced PB Limiter Staff Commands:";
            yield return "!PBLimiter GetReport";
            yield return "!PBLimiter Disable";
            yield return "!PBLimiter Enable";
            yield return "!PBLimiter AllowStaffCommands";
            yield return "!PBLimiter DenyStaffCommands";
        }
    }
}
