// Persistence sample: VSaga.Persistence.EFCore + VSaga.Persistence.EFCore.Postgres
//
// The checkout saga (VSaga.Samples.Persistence.Common) on EF Core against Postgres. Every [Postgres]
// block below is specific to this provider; the rest of the file is identical in all four persistence
// samples. Start the database first (see README.md):
//
//   docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.Postgres/docker-compose.yml up -d --wait
//   dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.Postgres

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VSaga.Persistence.EFCore;
using VSaga.Samples.Persistence.Common;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddCheckoutSample();

// [Postgres] Register the provider. AddVSagaEfCore is provider-agnostic and takes EF Core's own
// DbContextOptionsBuilder, so the database hookup (UseNpgsql) is yours to make. MigrationsAssembly is
// not optional: the migrations live in VSaga.Persistence.EFCore.Postgres, not in the DbContext's own
// assembly, and without it MigrateAsync below reports "no migrations were applied" and creates nothing.
var connectionString = builder.Configuration.GetConnectionString("VSaga")
    ?? throw new InvalidOperationException("ConnectionStrings:VSaga is not set (see appsettings.json).");
builder.Services.AddVSagaEfCore(db => db.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")));

using var host = builder.Build();

// [Postgres] Apply the versioned migrations before the engine starts polling for timeouts and outbox
// rows. MigrateAsync, never EnsureCreatedAsync: the latter bypasses __EFMigrationsHistory, and the next
// migration then fails against the tables it created. VSagaDbContext is Scoped, hence the scope. On a
// fresh database EF Core logs one failed SELECT from __EFMigrationsHistory before creating that table;
// it is EF probing for the history table, not an error in the migration.
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VSagaDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine($"Postgres migrations applied ({(await db.Database.GetAppliedMigrationsAsync()).Count()} in __EFMigrationsHistory).");
}

await host.StartAsync();
try
{
    await CheckoutDemo.RunAsync(host.Services, "VSaga.Persistence.EFCore (Postgres)", CancellationToken.None);

    // [Postgres] The rows outlive this process. Run the sample again and the count keeps growing.
    // README.md has the psql queries for the tables.
    Console.WriteLine("Postgres: these sagas are still in the database — run the sample again, or query them with psql (see README.md).");
}
finally
{
    await host.StopAsync();
}
