using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VSaga.Dashboard.Identity;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Program.cs exactly as it composes in production, real Postgres and RabbitMQ registrations included,
/// except that the identity database is a temp file of this factory's own: left to its default, a test run
/// would create the developer's real <c>{LocalApplicationData}/vSaga/dashboard/identity.db</c>.
/// </summary>
public sealed class RealCompositionFactory : WebApplicationFactory<Program>
{
    private readonly TestIdentityDatabase _identity = new();

    public override async ValueTask DisposeAsync()
    {
        // ConfigureAwait(false): xunit disposes a class fixture through the blocking Dispose(), which runs this.
        await base.DisposeAsync().ConfigureAwait(false);
        _identity.Delete();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting(DashboardIdentitySettings.SqlitePathKey, _identity.FilePath);
}
