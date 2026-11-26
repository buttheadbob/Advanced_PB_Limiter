using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Sandbox;
using VRage.Plugins;
using VRage.Utils;

namespace AdvancedPBLimiter.QuasarCompanion
{
    /// <summary>
    /// Answers the Advanced PB Limiter Quasar plugin. The data comes from the Magnetar plugin through its QuasarBridge,
    /// found by name because this project cannot reference it. Requests arrive off the game thread.
    /// </summary>
    // ReSharper disable once UnusedType.Global
    public sealed class ReportCompanion : IPlugin, IQuasarCompanionRequestHandler
    {
        private const string BridgeTypeName = "Advanced_PB_Limiter.MagnetarHost.QuasarBridge";

        // States of a request queued on the game thread: whichever of the work and the give-up comes first wins.
        private const int Waiting = 0;
        private const int Started = 1;
        private const int Abandoned = 2;

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        private volatile Type? _bridge;
        private readonly object _search = new object(); // Held for the one search of the assemblies loaded at start.
        private bool _searched; // Under _search: the assemblies loaded so far were searched and new ones are watched.
        // The reasons written to the server log, per operation: a failing profile apply is resent while the Reports page's
        // snapshots succeed, and a lagging server's snapshots fail with two reasons in turn and between successes.
        private readonly RecentReasons _logged = new RecentReasons();

        public string PluginId => ReportOperations.CompanionId;

        public void Init(object gameInstance)
        {
        }

        public void Update()
        {
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }

        public async Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(CompanionPluginRequest request, CancellationToken cancellationToken)
        {
            var response = new CompanionPluginResponse { CorrelationId = request.CorrelationId, SchemaVersion = 1 };
            try
            {
                switch (request.Operation)
                {
                    case ReportOperations.Snapshot:
                        var snapshot = Read<SnapshotRequest>(request);
                        response.PayloadJson = await GetSnapshotJson(DeadlineOf(snapshot.TimeoutMs, snapshot.ExpiresUnixMs), cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.TurnOffBlock:
                        var block = Read<TurnOffBlockRequest>(request);
                        response.PayloadJson = await TurnOff("TurnOffBlock", block.EntityId, DeadlineOf(block.TimeoutMs, block.ExpiresUnixMs), cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.TurnOffPlayer:
                        var player = Read<TurnOffPlayerRequest>(request);
                        response.PayloadJson = await TurnOff("TurnOffPlayer", player.IdentityId, DeadlineOf(player.TimeoutMs, player.ExpiresUnixMs), cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.ProfileRead:
                        response.PayloadJson = await OnLoadedWorld(bridge => (string)Invoke(bridge, "GetProfileJson")!, 0, cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.ProfileApply:
                        var profile = Read<ProfileApplyRequest>(request);
                        // A limiter that is inactive for good, on a cluster node, never applies it: refused, so Quasar stops
                        // sending it. One inactive until it is updated fails below, in OnLoadedWorld, and Quasar sends it again.
                        var target = FindBridge();
                        response.PayloadJson = InactiveOf(target) is { } inactive && InactiveForGood(target)
                            ? JsonConvert.SerializeObject(new ProfileData { Refused = inactive }, Json)
                            : await OnLoadedWorld(bridge => ApplyOnce(bridge, profile), DeadlineOf(profile.TimeoutMs, profile.ExpiresUnixMs), cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown operation '{request.Operation}'.");
                }

                response.Success = true;
            }
            catch (Exception e)
            {
                Exception cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                response.Success = false;
                response.Error = cause.Message;
                // Quasar shows the message. The server log keeps where it came from, with what caused it, once per reason
                // for each operation and again only after RecentReasons.Window: Quasar asks every few seconds.
                if (_logged.IsNew(LogKey(request.Operation), cause.Message))
                    MyLog.Default.WriteLine(e);
            }
            return response;
        }

        /// <summary>The operation, or one shared key for any unknown one, so a stray name can't grow the reasons kept.</summary>
        private static string LogKey(string? operation)
        {
            switch (operation)
            {
                case ReportOperations.Snapshot:
                case ReportOperations.TurnOffBlock:
                case ReportOperations.TurnOffPlayer:
                case ReportOperations.ProfileRead:
                case ReportOperations.ProfileApply:
                    return operation;
                default:
                    return "";
            }
        }

        /// <summary>
        /// Builds the snapshot on this thread when the server plugin can do that: it reads tracking data and the block
        /// and grid names the game thread cached, so there is no need to wait for the game thread, which during a world
        /// load can be busy for minutes. An older server plugin has no such marker and reads live block state, so it is
        /// asked on the game thread, as before, with the request's deadline.
        /// </summary>
        private async Task<string> GetSnapshotJson(long deadline, CancellationToken cancellationToken)
        {
            var bridge = FindBridge();
            if (bridge == null)
                return JsonConvert.SerializeObject(new ReportSnapshot { Installed = false }, Json);

            if (!(bool)Invoke(bridge, "IsOnline")!)
                return JsonConvert.SerializeObject(new ReportSnapshot { Installed = true, Online = false, Inactive = InactiveOf(bridge) }, Json);

            cancellationToken.ThrowIfCancellationRequested();

            MethodInfo? anyThread = bridge.GetMethod("ReportSnapshotOnAnyThread", BindingFlags.Public | BindingFlags.Static);
            if (anyThread?.Invoke(null, null) is bool allowed && allowed)
            {
                RefuseLate(deadline);
                return (string)Invoke(bridge, "GetReportJson")!;
            }

            // An older server plugin reads live block state here: it must run on the game thread.
            return await OnGameThread(() => (string)Invoke(bridge, "GetReportJson")!, deadline, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Refuses a request that has already waited past its own deadline, as OnGameThread does.</summary>
        private static void RefuseLate(long deadline)
        {
            if (deadline != 0 && Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("The request waited on this server for longer than it allows before it could run, so it was not run.");
        }

        private async Task<string> TurnOff(string method, long id, long deadline, CancellationToken cancellationToken)
        {
            int turnedOff = await OnLoadedWorld(bridge => (int)Invoke(bridge, method, id)!, deadline, cancellationToken).ConfigureAwait(false);
            return JsonConvert.SerializeObject(new TurnOffResult { TurnedOff = turnedOff }, Json);
        }

        private Task<T> OnLoadedWorld<T>(Func<Type, T> work, long deadline, CancellationToken cancellationToken)
        {
            var bridge = FindBridge() ?? throw new InvalidOperationException("Advanced PB Limiter is not loaded on this server.");
            if (InactiveOf(bridge) is { } inactive)
                throw new InvalidOperationException(inactive);
            if (!(bool)Invoke(bridge, "IsOnline")!)
                throw new InvalidOperationException("The world has not finished loading.");

            return OnGameThread(() => work(bridge), deadline, cancellationToken);
        }

        private static T Read<T>(CompanionPluginRequest request) where T : class
        {
            return JsonConvert.DeserializeObject<T>(request.PayloadJson ?? "", Json)
                   ?? throw new InvalidOperationException("The request is empty.");
        }

        /// <summary>
        /// The limiter's bridge, once its assembly is loaded. The assemblies already loaded are searched once, on the first
        /// request; after that only each newly loaded one is looked at, as it loads. Quasar asks every few seconds, a server
        /// has hundreds of assemblies, and every script compile adds one. A request that comes while the first one searches
        /// waits for that search, so it never reports the limiter missing before it was looked for.
        /// </summary>
        private Type? FindBridge()
        {
            Type? found = _bridge;
            if (found != null)
                return found;

            lock (_search)
            {
                if (!_searched)
                {
                    // Watched before the search, so an assembly that loads during it is not missed.
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (Holds(assembly))
                            break;
                    }
                    _searched = true;
                }
                return _bridge;
            }
        }

        private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e) => Holds(e.LoadedAssembly);

        /// <summary>True, and the bridge kept and the watch stopped, when this assembly holds the bridge.</summary>
        private bool Holds(Assembly assembly)
        {
            Type? type;
            try
            {
                // Only the Magnetar plugin's own assembly, see LimiterAssemblyName.
                if (assembly.IsDynamic || !LimiterAssemblyName.Is(assembly.GetName().Name))
                    return false;
                type = assembly.GetType(BridgeTypeName, throwOnError: false);
            }
            catch (Exception e) when (e is FileLoadException or FileNotFoundException or BadImageFormatException or NotSupportedException)
            {
                return false; // An assembly whose types can't be looked at holds no bridge.
            }
            if (type == null)
                return false;
            _bridge = type;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            return true;
        }

        /// <summary>
        /// When the work must have started by, as a <see cref="Stopwatch"/> timestamp taken as the request arrives (0 for no
        /// limit): the expiry on Quasar's clock, when the request has one, and never later than the timeout from receiving
        /// it. Fixed now, so the time spent before the work is queued, finding the limiter and checking the world, counts
        /// against it too. A request that arrives after its expiry has waited on the way, behind another request say, while
        /// Quasar stopped waiting: it is refused, not run late.
        /// </summary>
        private static long DeadlineOf(int timeoutMs, long expiresUnixMs)
        {
            long now = Stopwatch.GetTimestamp();
            long ms = timeoutMs;
            if (expiresUnixMs > 0)
            {
                long left = expiresUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (left <= 0)
                    throw new TimeoutException("The request reached the server too late for its answer to get back before Quasar stops waiting for it, by this server's clock, so it was not run. If this keeps happening, this server's clock may be ahead of Quasar's: check that both are set right.");
                ms = timeoutMs > 0 ? Math.Min(timeoutMs, left) : left;
            }
            return ms > 0 ? now + ms * Stopwatch.Frequency / 1000 : 0;
        }

        // The last profile request applied, by Quasar's queue entry, and the answer it got. Game thread only, as ApplyOnce.
        private string _appliedRequestId = "";
        private string _appliedAnswer = "";

        /// <summary>
        /// Applies the profile, on the game thread, unless it is a repeat of the last request applied: Quasar sends an entry
        /// again when the answer reached it too late, and applying it twice could overwrite changes made since, while the
        /// history would show only the second time. The repeat gets the first answer. A failure is not kept, so a repeat of
        /// it is tried again.
        /// </summary>
        private string ApplyOnce(Type bridge, ProfileApplyRequest profile)
        {
            bool tracked = !string.IsNullOrEmpty(profile.RequestId);
            if (tracked && string.Equals(profile.RequestId, _appliedRequestId, StringComparison.Ordinal))
                return _appliedAnswer;
            // A limiter before 1.3.3 has only ApplyProfile, which throws a refusal instead of returning it.
            MethodInfo apply = bridge.GetMethod("ApplyProfileOrRefuse", BindingFlags.Public | BindingFlags.Static)
                               ?? bridge.GetMethod("ApplyProfile", BindingFlags.Public | BindingFlags.Static)
                               ?? throw new InvalidOperationException("This Advanced PB Limiter version has no ApplyProfile; update it.");
            string answer = (string)apply.Invoke(null, new object[] { profile.SettingsJson })!;
            if (tracked)
            {
                _appliedRequestId = profile.RequestId;
                _appliedAnswer = answer;
            }
            return answer;
        }

        /// <summary>Why the limiter is inactive until the server restarts, or null; a limiter before 1.3.4.5 never says.</summary>
        private static string? InactiveOf(Type? bridge)
        {
            MethodInfo? reason = bridge?.GetMethod("InactiveReason", BindingFlags.Public | BindingFlags.Static);
            return reason?.Invoke(null, null) as string;
        }

        /// <summary>True when the limiter will never apply a profile on this server; a limiter before 1.3.4.5 never says.</summary>
        private static bool InactiveForGood(Type? bridge)
        {
            MethodInfo? forGood = bridge?.GetMethod("InactiveForGood", BindingFlags.Public | BindingFlags.Static);
            return forGood?.Invoke(null, null) is true;
        }

        private static object? Invoke(Type bridge, string method, params object[] args)
        {
            var info = bridge.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
                       ?? throw new InvalidOperationException($"This Advanced PB Limiter version has no {method}; update it.");
            return info.Invoke(null, args);
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the game thread. It is skipped when the request is cancelled, or when
        /// <paramref name="deadline"/> (see <see cref="DeadlineOf"/>, 0 for none) passes before it starts: Quasar stops
        /// waiting soon after, and a late turn-off or profile would change the server without anyone being told.
        /// </summary>
        private static async Task<T> OnGameThread<T>(Func<T> work, long deadline, CancellationToken cancellationToken)
        {
            var game = MySandboxGame.Static ?? throw new InvalidOperationException("The game is not running.");

            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (deadline != 0)
            {
                long leftMs = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (leftMs <= 0)
                    throw new TimeoutException("The request waited on this server for longer than it allows before it could run, so it was not run.");
                giveUp.CancelAfter((int)Math.Min(leftMs, int.MaxValue));
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            int state = Waiting;
            using var registration = giveUp.Token.Register(() =>
            {
                if (Interlocked.CompareExchange(ref state, Abandoned, Waiting) == Waiting)
                    completion.TrySetCanceled(giveUp.Token);
            });
            game.Invoke(() =>
            {
                if (Interlocked.CompareExchange(ref state, Started, Waiting) != Waiting)
                    return;
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            }, "AdvancedPBLimiter.QuasarCompanion");

            try
            {
                return await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The game thread was busy for longer than the request allows, so the request was not run.", e);
            }
        }
    }
}
