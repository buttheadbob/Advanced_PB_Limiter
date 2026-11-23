using System.IO;
using System.Reflection;
using Advanced_PB_Limiter.QuasarContracts;
using AdvancedPBLimiter.Quasar.Pages;
using AdvancedPBLimiter.Quasar.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;

namespace AdvancedPBLimiter.Quasar;

/// <summary>
/// Advanced PB Limiter for Quasar: the Live view, settings profiles and combined reports. The browser assembly holds the
/// pages; this worker adapter registers them, owns the companion channel and the profile store, and answers the SDK calls.
/// </summary>
// ReSharper disable once UnusedType.Global
public sealed class AdvancedPBLimiterQuasarPlugin : IQuasarPlugin
{
    public const string Route = LimiterRoutes.Live;

    // Material "memory"
    private const string Icon = "<path d=\"M0 0h24v24H0z\" fill=\"none\"/><path d=\"M15 9H9v6h6V9zm-2 4h-2v-2h2v2zm8-2V9h-2V7c0-1.1-.9-2-2-2h-2V3h-2v2h-2V3H9v2H7c-1.1 0-2 .9-2 2v2H3v2h2v2H3v2h2v2c0 1.1.9 2 2 2h2v2h2v-2h2v2h2v-2h2c1.1 0 2-.9 2-2v-2h2v-2h-2v-2h2zm-4 6H7V7h10v10z\"/>";

    public string Id => ReportOperations.CompanionId;

    public string DisplayName => "Advanced PB Limiter";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
        services.AddSingleton<LimiterReports>();
        string profiles = Path.Combine(context.InstallDirectory, "plugin-data", "advancedpblimiter", "profiles.json");
        services.AddSingleton(provider => new ProfileStore(profiles, provider.GetRequiredService<ILogger<ProfileStore>>()));
        services.AddSingleton<ProfileQueue>();
        services.AddHostedService(provider => provider.GetRequiredService<ProfileQueue>());
        QuasarUiOperations.Register(services, context);
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
        // The pages are served from the browser assembly and call the worker through the SDK operations registered above.
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(ReportsPage).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new QuasarNavItem("PB Limiter", Route, Icon, QuasarNavZones.Main, 810, QuasarPolicyNames.CanView);
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions()
    {
        yield break;
    }
}
