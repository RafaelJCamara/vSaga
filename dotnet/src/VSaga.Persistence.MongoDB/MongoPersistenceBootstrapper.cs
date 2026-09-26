using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// A retrying hosted service, never fail-fast: it creates the indexes, writes the schema marker once they
/// are in place, then probes the server on <see cref="VSagaMongoOptions.ProbeInterval"/> for as long as
/// the host runs, logging each change in the probe's verdict. The host starts whether or not MongoDB is
/// reachable -- stores simply fail until it is -- but <see cref="MongoPersistenceHealthCheck"/> stays
/// Unhealthy until a probe passes, which is what a compose file's <c>depends_on: service_healthy</c> gates
/// on: a saga host must not start against a database whose business-key index does not exist yet.
/// </summary>
public sealed class MongoPersistenceBootstrapper(MongoCollections collections, MongoServerProbe probe, VSagaMongoOptions options, ILogger<MongoPersistenceBootstrapper> logger)
    : BackgroundService
{
    private static readonly UpdateDefinition<SchemaMarkerDocument> WriteMarker =
        Builders<SchemaMarkerDocument>.Update.SetOnInsert(d => d.SchemaVersion, SagaInstanceDocument.CurrentSchemaVersion);

    private static readonly UpdateOptions Upsert = new() { IsUpsert = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? wasHealthy = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureLayoutAsync(stoppingToken);
                var report = await probe.ProbeAsync(stoppingToken);
                if (report.IsHealthy != wasHealthy)
                {
                    if (report.IsHealthy)
                        logger.LogInformation("MongoDB persistence is ready: {Report}", report.Describe());
                    else
                        logger.LogError("MongoDB persistence is not ready: {Report}", report.Describe());
                }

                wasHealthy = report.IsHealthy;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure here, such as the replica set still electing, is exactly what the
                // retry is for. The health check meanwhile reports whatever the last probe found.
                logger.LogWarning(ex, "MongoDB persistence bootstrap attempt failed; retrying in {Interval}.", options.ProbeInterval);
                wasHealthy = null;
            }

            try
            {
                await Task.Delay(options.ProbeInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Index creation is idempotent when the definition matches and an error when it does not -- which is
    /// the marker that a layout change is due, and stays loud rather than being papered over. The schema
    /// marker is written only once every index exists: it means "this database has the provider's layout".
    /// A marker of another version is left alone and reported by the probe.
    /// </summary>
    public async Task EnsureLayoutAsync(CancellationToken cancellationToken)
    {
        await MongoIndexes.CreateAllAsync(collections, cancellationToken);
        await collections.Meta.UpdateOneAsync(Builders<SchemaMarkerDocument>.Filter.Eq(d => d.Id, SchemaMarkerDocument.MarkerId), WriteMarker, Upsert, cancellationToken);
    }
}
