// Persistence sample: VSaga.Persistence.EFCore + VSaga.Persistence.EFCore.SqlServer
//
// The checkout saga (VSaga.Samples.Persistence.Common) on EF Core against SQL Server. Every [SqlServer]
// block below is specific to this provider; the rest of the file is identical in all the persistence
// samples. Start the database first (see README.md):
//
//   docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.SqlServer/docker-compose.yml up -d --wait
//   dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.SqlServer

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

// [SqlServer] Register the provider. AddVSagaEfCore is provider-agnostic and takes EF Core's own
// DbContextOptionsBuilder, so the database hookup (UseSqlServer) is yours to make. MigrationsAssembly is
// not optional: the migrations live in VSaga.Persistence.EFCore.SqlServer, not in the DbContext's own
// assembly, and without it MigrateAsync below reports "no migrations were applied" and creates nothing.
var connectionString = builder.Configuration.GetConnectionString("VSaga")
    ?? throw new InvalidOperationException("ConnectionStrings:VSaga is not set (see appsettings.json).");
builder.Services.AddVSagaEfCore(db => db.UseSqlServer(connectionString,
    sql => sql.MigrationsAssembly("VSaga.Persistence.EFCore.SqlServer")));

using var host = builder.Build();

// [SqlServer] Apply the versioned migrations before the engine starts polling for timeouts and outbox
// rows. MigrateAsync, never EnsureCreatedAsync: the latter bypasses __EFMigrationsHistory, and the next
// migration then fails against the tables it created. MigrateAsync also creates the `vsaga` database when
// it does not exist yet. VSagaDbContext is Scoped, hence the scope.
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VSagaDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine($"SQL Server migrations applied ({(await db.Database.GetAppliedMigrationsAsync()).Count()} in __EFMigrationsHistory).");
}

await host.StartAsync();
try
{
    await CheckoutDemo.RunAsync(host.Services, "VSaga.Persistence.EFCore (SQL Server)", CancellationToken.None);

    // [SqlServer] The rows outlive this process. Run the sample again and the count keeps growing.
    // README.md has the sqlcmd queries for the tables.
    Console.WriteLine("SQL Server: these sagas are still in the database — run the sample again, or query them with sqlcmd (see README.md).");
}
finally
{
    await host.StopAsync();
}
