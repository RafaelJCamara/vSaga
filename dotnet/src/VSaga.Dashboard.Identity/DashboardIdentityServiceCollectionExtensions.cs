using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Identity.EFCore;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity;

public static class DashboardIdentityServiceCollectionExtensions
{
    /// <summary>The Data Protection application name; cookies and tokens are only readable under the same one.</summary>
    public const string DataProtectionApplicationName = "VSaga.Dashboard";

    /// <summary>
    /// Registers the EF Core identity store over the database <paramref name="configureDatabase"/> points
    /// at (the provider's <c>Use*</c> call, with its migrations assembly), <see cref="IdentityStartup"/>, the
    /// <see cref="FirstRunState"/> its first-administrator step fills (that step is the API's to register), and
    /// Data Protection with its key ring in that store. Nothing touches the database here; the host calls
    /// <see cref="IdentityStartup.EnsureReadyAsync(CancellationToken)"/> once it is built.
    /// </summary>
    public static IServiceCollection AddDashboardIdentity(
        this IServiceCollection services,
        DashboardIdentitySettings settings,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configureDatabase);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(settings);
        services.AddDbContext<DashboardIdentityDbContext>(configureDatabase);
        services.AddScoped<IDashboardIdentityStore, EfCoreDashboardIdentityStore>();
        services.AddScoped<IDashboardKeyRingStore, EfCoreKeyRingStore>();
        services.AddSingleton<IdentityStartup>();
        services.AddSingleton<FirstRunState>();
        services.AddSingleton<IIdentityReadiness>(provider => provider.GetRequiredService<IdentityStartup>());
        services.AddSingleton<IdentityStoreXmlRepository>();

        services.AddDataProtection().SetApplicationName(DataProtectionApplicationName);
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
            new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = provider.GetRequiredService<IdentityStoreXmlRepository>()));
        return services;
    }
}
