using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.QuasarContracts;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Sandbox;
using VRage.Plugins;

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

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        private Type? _bridge;

        public string PluginId => ReportOperations.CompanionId;

        public void Init(object gameInstance)
        {
        }

        public void Update()
        {
        }

        public void Dispose()
        {
        }

        public async Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(CompanionPluginRequest request, CancellationToken cancellationToken)
        {
            var response = new CompanionPluginResponse { CorrelationId = request.CorrelationId, SchemaVersion = 1 };
            try
            {
                switch (request.Operation)
                {
                    case ReportOperations.Snapshot:
                        response.PayloadJson = await GetSnapshotJson(cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.TurnOffBlock:
                        var block = Read<TurnOffBlockRequest>(request);
                        response.PayloadJson = await TurnOff("TurnOffBlock", block.EntityId, cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.TurnOffPlayer:
                        var player = Read<TurnOffPlayerRequest>(request);
                        response.PayloadJson = await TurnOff("TurnOffPlayer", player.IdentityId, cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.ProfileRead:
                        response.PayloadJson = await OnLoadedWorld(bridge => (string)Invoke(bridge, "GetProfileJson")!, cancellationToken).ConfigureAwait(false);
                        break;

                    case ReportOperations.ProfileApply:
                        var profile = Read<ProfileApplyRequest>(request);
                        response.PayloadJson = await OnLoadedWorld(bridge => (string)Invoke(bridge, "ApplyProfile", profile.SettingsJson)!, cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown operation '{request.Operation}'.");
                }

                response.Success = true;
            }
            catch (Exception e)
            {
                response.Success = false;
                response.Error = e is TargetInvocationException { InnerException: { } inner } ? inner.Message : e.Message;
            }
            return response;
        }

        private async Task<string> GetSnapshotJson(CancellationToken cancellationToken)
        {
            var bridge = FindBridge();
            if (bridge == null)
                return JsonConvert.SerializeObject(new ReportSnapshot { Installed = false }, Json);

            // During a world load the game thread is busy for minutes, and asking it would stall the agent.
            if (!(bool)Invoke(bridge, "IsOnline")!)
                return JsonConvert.SerializeObject(new ReportSnapshot { Installed = true, Online = false }, Json);

            return await OnGameThread(() => (string)Invoke(bridge, "GetReportJson")!, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> TurnOff(string method, long id, CancellationToken cancellationToken)
        {
            int turnedOff = await OnLoadedWorld(bridge => (int)Invoke(bridge, method, id)!, cancellationToken).ConfigureAwait(false);
            return JsonConvert.SerializeObject(new TurnOffResult { TurnedOff = turnedOff }, Json);
        }

        private Task<T> OnLoadedWorld<T>(Func<Type, T> work, CancellationToken cancellationToken)
        {
            var bridge = FindBridge() ?? throw new InvalidOperationException("Advanced PB Limiter is not loaded on this server.");
            if (!(bool)Invoke(bridge, "IsOnline")!)
                throw new InvalidOperationException("The world has not finished loading.");

            return OnGameThread(() => work(bridge), cancellationToken);
        }

        private static T Read<T>(CompanionPluginRequest request) where T : class
        {
            return JsonConvert.DeserializeObject<T>(request.PayloadJson ?? "", Json)
                   ?? throw new InvalidOperationException("The request is empty.");
        }

        private Type? FindBridge()
        {
            if (_bridge != null)
                return _bridge;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(BridgeTypeName, throwOnError: false);
                if (type != null)
                    return _bridge = type;
            }
            return null;
        }

        private static object? Invoke(Type bridge, string method, params object[] args)
        {
            var info = bridge.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
                       ?? throw new InvalidOperationException($"This Advanced PB Limiter version has no {method}; update it.");
            return info.Invoke(null, args);
        }

        private static async Task<T> OnGameThread<T>(Func<T> work, CancellationToken cancellationToken)
        {
            var game = MySandboxGame.Static ?? throw new InvalidOperationException("The game is not running.");

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled());
            game.Invoke(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            }, "AdvancedPBLimiter.QuasarCompanion");
            return await completion.Task.ConfigureAwait(false);
        }
    }
}
