#nullable enable
using System;
using ExtensionMethods;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Utils;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;

namespace Advanced_PB_Limiter.Commands
{
    /// <summary>What the chat commands do. The Torch and Magnetar command classes only declare them and pass the reply on.</summary>
    public static class LimiterCommands
    {
        private static readonly LimiterLog Log = new("Commands");
        private static IPbLimiterConfig Config => LimiterCore.Config;

        public const string PlayersOnlyMessage = "This command can only be used by players in game.";
        public const string StaffCommandsDisabledMessage = "CreatePrivilegedUser is disabled for staff in the config.  Please enable AllowStaffCommands to use this command.";
        private const int TimingsDurationMs = 5000;

        public static IEnumerable<string> About()
        {
            yield return "Advanced PB Limiter v" + LimiterCore.Host.Version.TrimStart('v') + " by SentorX"; // Torch's version has a v of its own.
            yield return "This is a plugin that limits the runtime and memory usage of programmable blocks to prevent server lag.";
            yield return "You can also generate detailed reports of your server with this plugin installed.";
            yield return "Advanced PB Limiter is open source and can be found on GitHub: https://github.com/buttheadbob/Advanced_PB_Limiter";
            yield return "For any issues, please contact SentorX through his Discord server.  DMs will be ignored unless you are a server owner or admin.";
            yield return "Discord Invite: https://discord.gg/rSuxGrHrrt";
        }

        /// <param name="steamId">The caller, or null for the console.</param>
        public static string Limits(ulong? steamId)
        {
            if (LimiterCore.Retired) return LimiterCore.RetiredMessage;
            StringBuilder sb = new();
            if (steamId is null || !Config.TryGetPrivilegedPlayer(steamId.Value, out PrivilegedPlayer? privileged) || privileged is null)
            {
                sb.AppendLine();
                sb.AppendLine("Server Limits");
                sb.AppendLine("--------------");
                sb.Append("Max PB Runtime: ").AppendLine(Limit(Config.MaxRunTimeMS));
                sb.Append("Max PB Runtime Average: ").AppendLine(Limit(Config.MaxRunTimeMSAvg));
                // The combined limits apply only while the server checks them.
                if (Config.CheckAllUserBlocksCombined)
                {
                    sb.Append("Max PB Runtime (All PBs Combined): ").AppendLine(CombinedLimit(Config.MaxAllBlocksCombinedRunTimeMS));
                    sb.Append("Max PB Runtime Average (All PBs Combined): ").AppendLine(CombinedLimit(Config.MaxAllBlocksCombinedRunTimeMSAvg));
                }
                else
                {
                    sb.AppendLine("Combined Limits Active: False");
                }
                sb.Append("Max Offences Before Punishment: ").Append(InvariantText.Of(Config.MaxOffencesBeforePunishment)).AppendLine();
                sb.Append("Punishment Type: ").Append(Config.Punishment.ToString()).AppendLine();
                sb.Append("Graceful Shutdown Period: ").Append(Seconds(Config.GracefulShutDownRequestDelay)).AppendLine();
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("You are a privileged user.  Your limits are:");
            sb.AppendLine("-----------------------");
            sb.Append("Max PB Runtime: ").AppendLine(Limit(privileged.RuntimeAllowance));
            sb.Append("Max PB Runtime Average: ").AppendLine(Limit(privileged.RuntimeAverageAllowance));
            bool combinedActive = Config.CheckAllUserBlocksCombined && !privileged.NoCombinedLimits;
            sb.Append("Combined Limits Active: ").Append(InvariantText.Of(combinedActive)).AppendLine();
            if (combinedActive)
            {
                sb.Append("Max PB Runtime (All PBs Combined): ").AppendLine(CombinedLimit(privileged.CombinedRuntimeAllowance));
                sb.Append("Max PB Runtime Average (All PBs Combined): ").AppendLine(CombinedLimit(privileged.CombinedRuntimeAverageAllowance));
            }
            sb.Append("Max Offences Before Punishment: ").Append(InvariantText.Of(privileged.OffencesBeforePunishment)).AppendLine();
            sb.Append("Punishment Type: ").Append(privileged.Punishment.ToString()).AppendLine();
            sb.Append("Graceful Shutdown Period: ").Append(Seconds(privileged.GracefulShutDownRequestDelay)).AppendLine();
            return sb.ToString();
        }

        private static string Seconds(int seconds) => InvariantText.Count(seconds, "second", "seconds");

        // As the limiter reads them: a block limit is off at 0 only, a combined limit at 0 or less.
        private static string Limit(double milliseconds) => milliseconds != 0 ? InvariantText.Of(milliseconds) + "ms" : "no limit";
        private static string CombinedLimit(double milliseconds) => milliseconds > 0 ? InvariantText.Of(milliseconds) + "ms" : "no limit";

        /// <summary>Generates the caller's report on another thread. <paramref name="respond"/> is called from that thread.</summary>
        public static void MyStats(ulong steamId, Action<string> respond)
        {
            if (LimiterCore.Retired)
            {
                respond(LimiterCore.RetiredMessage);
                return;
            }
            RunInBackground(() => ReportManager.PlayerStatsRequest(steamId, respond));
        }

        /// <summary>Lists the caller's runtimes on another thread. <paramref name="respond"/> is called from that thread.</summary>
        public static void Runtimes(long identityId, Action<string> respond)
        {
            if (LimiterCore.Retired)
            {
                respond(LimiterCore.RetiredMessage);
                return;
            }
            RunInBackground(() => ReportManager.PlayerRuntimesRequest(identityId, respond));
        }

        /// <summary>Generates the server report on another thread. <paramref name="respond"/> is called from that thread.</summary>
        public static void GetReport(Action<string> respond)
        {
            if (LimiterCore.Retired)
            {
                respond(LimiterCore.RetiredMessage);
                return;
            }
            RunInBackground(() => ReportManager.GetReportAsync(respond));
        }

        // Fire and forget. Nothing waits on the task, so a failure would otherwise go unseen.
        private static void RunInBackground(Func<Task> action)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.Error(e, "A report failed");
                }
            });
        }

        public static string RequestPbShutdown(int seconds)
        {
            if (LimiterCore.Retired) return LimiterCore.RetiredMessage;
            // Scripts read negative values as warnings, see README.
            if (seconds < 0)
                return "The seconds until restart cannot be negative.";

            MySandboxGame.Static.Invoke(() => // Running a script has to happen on the game thread.
            {
                foreach (TrackedPlayer? tplayer in TrackingManager.PlayersTracked.Values)
                {
                    if (tplayer == null) continue;
                    foreach (TrackedPBBlock? pbBlock in tplayer.GetAllPBBlocks())
                    {
                        pbBlock?.ProgrammableBlock?.Run("GracefulShutDown::" + seconds.ToString(CultureInfo.InvariantCulture), UpdateType.Script);
                    }
                }
            }, "Advanced_PB_Limiter");
            return $"Sent the shutdown request to all tracked programmable blocks, with {Seconds(seconds)} until restart.";
        }

        public static string SetDebug(bool debug)
        {
            if (CannotChangeConfig() is { } refusal) return refusal;
            string saved = SaveChange(() => Config.DebugReporting = debug);
            return "Debug mode is now " + (debug ? "enabled." : "disabled.") + saved;
        }

        /// <summary>Logs the limiter's own timings for a few seconds. Too noisy to leave on.</summary>
        public static string ShowTimings()
        {
            if (LimiterCore.Retired) return LimiterCore.RetiredMessage;
            Config.ShowTimingsBurp = true;
            _ = StopTimingsAsync(Interlocked.Increment(ref _timingsRequest));
            return $"Logging timings for {InvariantText.Of(TimingsDurationMs / 1000)} seconds.";
        }

        private static int _timingsRequest;

        /// <summary>Only the latest request turns them off, so a second one is not cut short by the first one's timer.</summary>
        private static async Task StopTimingsAsync(int request)
        {
            await Task.Delay(TimingsDurationMs).ConfigureAwait(false);
            if (Volatile.Read(ref _timingsRequest) == request)
                Config.ShowTimingsBurp = false;
        }

        /// <summary>Why no command may change the config now, or null: the limiter was retired, or this is a node of a managed cluster.</summary>
        private static string? CannotChangeConfig() => LimiterCore.Retired ? LimiterCore.RetiredMessage : LimiterCore.Host.ConfigChangeRefusal;

        /// <summary>Makes a command's change and saves it, once: "" when saved, else the part of the reply that says it was not.</summary>
        private static string SaveChange(Action change) => LimiterCore.Host.ChangeConfig(change) ? "" : NotSavedMessage;

        private const string NotSavedMessage = " It could not be saved, so unless a later save succeeds it lasts only until the server restarts; the log says why.";

        public static string SetEnabled(bool enabled)
        {
            if (CannotChangeConfig() is { } refusal) return refusal;
            string saved = SaveChange(() => Config.Enabled = enabled);
            return "Advanced PB Limiter is now " + (enabled ? "enabled." : "disabled.") + saved;
        }

        public static string SetStaffCommandsAllowed(bool allowed)
        {
            if (CannotChangeConfig() is { } refusal) return refusal;
            string saved = SaveChange(() => Config.AllowStaffCommands = allowed);
            return "CreatePrivilegedUser is now " + (allowed ? "enabled" : "disabled") + " for staff." + saved;
        }

        public static string CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        {
            if (CannotChangeConfig() is { } refusal) return refusal;
            if (!Config.AllowStaffCommands)
                return StaffCommandsDisabledMessage;

            // Identities with no Steam ID, NPCs among them, all share 0, so an entry for it would apply to every one of them.
            if (steamId == 0)
                return "The Steam ID cannot be 0.";

            // A negative allowance would make every run an offence, and NaN would never be exceeded.
            if (!Utils.Limits.IsLimit(maxRunTimeMS) || !Utils.Limits.IsLimit(maxRunTimeMSAvg) || !Utils.Limits.IsLimit(maxAllBlocksCombinedRunTimeMS) || !Utils.Limits.IsLimit(maxAllBlocksCombinedRunTimeMSAvg)
                || !Utils.Limits.IsCount(offencesBeforePunishment) || !Utils.Limits.IsCount(gracefulShutDownRequestDelay))
                return "Limits must be numbers of 0 or more, and offences and the shutdown delay cannot be negative.";

            // A number the enum does not have would be saved as one, and the config would then fail to save or load.
            if (!Enum.IsDefined(typeof(Enums.Punishment), punishment))
                return "The punishment must be TurnOff, Damage or Destroy.";

            bool exists = Config.TryGetPrivilegedPlayer(steamId, out PrivilegedPlayer? existing);

            // Anything the command does not set stays as it was. A copy, which replaces the entry: the game thread reads the
            // one in use, so it is never changed field by field.
            PrivilegedPlayer player = existing?.Copy() ?? new PrivilegedPlayer { SteamId = steamId };
            if (string.IsNullOrWhiteSpace(player.Name))
                player.Name = PlayerName(steamId);
            player.RuntimeAllowance = maxRunTimeMS;
            player.RuntimeAverageAllowance = maxRunTimeMSAvg;
            player.NoCombinedLimits = !combinedLimits;
            player.CombinedRuntimeAllowance = maxAllBlocksCombinedRunTimeMS;
            player.CombinedRuntimeAverageAllowance = maxAllBlocksCombinedRunTimeMSAvg;
            player.OffencesBeforePunishment = offencesBeforePunishment;
            player.Punishment = punishment;
            player.GracefulShutDownRequestDelay = gracefulShutDownRequestDelay;

            // The command takes no damage amount. A new row gets 0, the server's PunishDamageAmount, instead of
            // PrivilegedPlayer's own 100, which would replace the server's setting for this player.
            if (existing is null)
                player.DamageAmount = 0;

            string saved = SaveChange(() => Config.SetPrivilegedPlayer(player));
            return (exists ? "Privileged user updated." : "Privileged user created.") + saved;
        }


        /// <summary>The name the player last had on this server, or the Steam ID for a player who has never joined it.</summary>
        private static string PlayerName(ulong steamId)
        {
            long identityId = MySession.Static?.Players.TryGetIdentityId(steamId) ?? 0;
            MyIdentity? identity = identityId == 0 ? null : MySession.Static!.Players.TryGetIdentity(identityId);
            return string.IsNullOrWhiteSpace(identity?.DisplayName) ? steamId.ToString(CultureInfo.InvariantCulture) : identity!.DisplayName.SafeName();
        }

        public static IEnumerable<string> DetailedStaffHelp()
        {
            yield return "Advanced PB Limiter Staff Commands:";
            yield return "!PBLimiter GetReport - Generates a full report of this server.";
            yield return "!PBLimiter Disable - Disables the Advanced PB Limiter.";
            yield return "!PBLimiter Enable - Enables the Advanced PB Limiter.";
            yield return "!PBLimiter requestpbshutdown [seconds] - Sends the shutdown request to all scripts that monitor for it, with the seconds until restart, 1 if left out.";
            yield return "!PBLimiter debug <true|false> - Turns debug logging on or off.";
            yield return "!PBLimiter CreatePrivilegedUser <steamId> <maxRunTimeMS> <maxRunTimeMSAvg> <combinedLimits true|false> <maxCombinedRunTimeMS> <maxCombinedRunTimeMSAvg> <offencesBeforePunishment> <TurnOff|Damage|Destroy> <gracefulShutdownSeconds> - Creates a privileged user or overwrites an existing one.";
            yield return "!PBLimiter AllowStaffCommands - Allows staff to use CreatePrivilegedUser. Only the server owner can use this command.";
            yield return "!PBLimiter DenyStaffCommands - Stops staff using CreatePrivilegedUser. Only the server owner can use this command.";
        }

        public static IEnumerable<string> StaffHelp()
        {
            yield return "Advanced PB Limiter Staff Commands:";
            yield return "!PBLimiter GetReport";
            yield return "!PBLimiter Disable";
            yield return "!PBLimiter Enable";
            yield return "!PBLimiter requestpbshutdown";
            yield return "!PBLimiter debug";
            yield return "!PBLimiter CreatePrivilegedUser";
            yield return "!PBLimiter AllowStaffCommands";
            yield return "!PBLimiter DenyStaffCommands";
        }
    }
}
