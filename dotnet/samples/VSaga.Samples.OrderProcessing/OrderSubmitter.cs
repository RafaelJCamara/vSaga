using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSaga.Abstractions.Transport;
using VSaga.Samples.OrderProcessing.Contracts;

namespace VSaga.Samples.OrderProcessing;

/// <summary>Bound from the "OrderSubmitter" configuration section.</summary>
internal sealed class OrderSubmitterOptions
{
    /// <summary>Orders submitted once at startup so the dashboard has something to show. 0 starts with none.</summary>
    public int SeedOrders { get; set; } = 10;

    /// <summary>The ceiling on a single <c>POST /orders</c> call, so one request cannot flood the broker.</summary>
    public int MaxOrdersPerRequest { get; set; } = 50;
}

/// <summary>
/// Stands in for a real "place order" front door. Submits a small seed batch at startup, then only on demand
/// through <c>POST /orders</c>: it never submits on a timer, so a long-running stack does not keep piling up
/// sagas (docs/adr/0011-sample-orders-are-seeded-then-on-demand.md).
/// </summary>
internal sealed class OrderSubmitter(IMessageTransport transport, IOptions<OrderSubmitterOptions> options, ILogger<OrderSubmitter> logger) : BackgroundService
{
    // The seed is spread out so the list shows sagas at different stages rather than ten identical rows.
    private static readonly TimeSpan SeedSpacing = TimeSpan.FromSeconds(1);
    private int _orderNumber;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seed = options.Value.SeedOrders;
        if (seed <= 0)
            return;

        logger.LogInformation("Submitting {Count} seed orders; POST /orders submits more", seed);
        for (var i = 0; i < seed; i++)
        {
            if (i > 0)
                await Task.Delay(SeedSpacing, stoppingToken);
            await SubmitOrderAsync(stoppingToken);
        }
    }

    /// <summary>Submits <paramref name="count"/> orders now and returns their ids, in submission order.</summary>
    public async Task<IReadOnlyList<string>> SubmitAsync(int count, CancellationToken cancellationToken)
    {
        var orderIds = new List<string>(count);
        for (var i = 0; i < count; i++)
            orderIds.Add(await SubmitOrderAsync(cancellationToken));
        return orderIds;
    }

    private async Task<string> SubmitOrderAsync(CancellationToken cancellationToken)
    {
        var orderId = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Interlocked.Increment(ref _orderNumber):D4}";
        var amount = Math.Round((decimal)(Random.Shared.NextDouble() * 480 + 20), 2);
        var order = new OrderSubmitted(orderId, $"CUST-{Random.Shared.Next(1000, 9999)}", amount);

        logger.LogInformation("Submitting {OrderId} for {Amount:C}", order.OrderId, order.Amount);

        // No VSaga correlation exists yet — the saga engine mints one on receipt since OrderSubmitted
        // is registered as an initiating event. This id is only for the envelope; the sample doesn't
        // need to track it further.
        await transport.PublishAsync(order, MessageEnvelope.From("OrderSubmitter", Guid.NewGuid()), cancellationToken);

        // docs/design/mixed-sagas.md §7: its own fresh correlation id (never the order's), so
        // MixedFulfilmentSaga never shares a correlation id with OrderSaga/PostShipmentChoreography and
        // gets a clean Saga Map of its own.
        await transport.PublishAsync(new FulfilmentRequested(orderId, amount), MessageEnvelope.From("OrderSubmitter", Guid.NewGuid()), cancellationToken);

        return orderId;
    }
}
