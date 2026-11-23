using Advanced_PB_Limiter.QuasarContracts;
using Microsoft.JSInterop;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// Calls the worker adapter's browser SDK operations. The Quasar 1.7 dashboard exposes <c>quasarSdk.request</c>; the
/// worker authorizes and audits every call.
/// </summary>
public static class SdkClient
{
    /// <summary>The plugin id Quasar uses for the adapter and the companion.</summary>
    public const string PluginId = ReportOperations.CompanionId;

    /// <summary>Quasar's own browser operations, such as the permission check.</summary>
    public const string CorePluginId = "quasar.core";

    public static Task<T> RequestAsync<T>(IJSRuntime js, string operation, CancellationToken cancellationToken)
    {
        return js.InvokeAsync<T>("quasarSdk.request", cancellationToken, PluginId, operation).AsTask();
    }

    public static Task<T> RequestAsync<T>(IJSRuntime js, string operation, object request, CancellationToken cancellationToken)
    {
        return js.InvokeAsync<T>("quasarSdk.request", cancellationToken, PluginId, operation, request).AsTask();
    }

    /// <summary>Whether the signed-in user holds <paramref name="policy"/>; the worker checks the pages and operations too.</summary>
    public static Task<bool> AllowedAsync(IJSRuntime js, string policy, CancellationToken cancellationToken)
    {
        return js.InvokeAsync<bool>("quasarSdk.request", cancellationToken, CorePluginId, "dashboard.permission",
            new { policy }).AsTask();
    }
}
