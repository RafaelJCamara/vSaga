using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VSaga.Abstractions.Transport;

namespace VSaga.Samples.Persistence.Common;

/// <summary>
/// The downstream service CheckoutSaga talks to: a plain <see cref="IMessageTransport"/> consumer, not a
/// saga. Its outcome is chosen by the order's <see cref="PaymentTokens">payment token</see>.
/// <para>
/// The in-memory transport dispatches synchronously, inside the publisher's own call. Replying from
/// the subscription handler would therefore run the saga's next step while the step that published
/// <see cref="ChargeCard"/> is still draining its post-commit publishes. The handler only queues the
/// charge; <see cref="ExecuteAsync"/> works through the queue and replies on its own loop, the way a
/// service behind a real broker would.
/// </para>
/// </summary>
internal sealed class PaymentGateway(IMessageTransport transport, ILogger<PaymentGateway> logger) : BackgroundService
{
    private const string ServiceName = "PaymentGateway";

    private readonly Channel<(ChargeCard Charge, string MessageId)> _pending =
        Channel.CreateUnbounded<(ChargeCard Charge, string MessageId)>();

    private IDisposable? _subscription;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var subscription = new TransportSubscription(ServiceName, [typeof(ChargeCard)], "vsaga.samples.payment-gateway");
        _subscription = await transport.SubscribeAsync(subscription, EnqueueAsync, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _pending.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    private async Task EnqueueAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        var charge = JsonSerializer.Deserialize<ChargeCard>(received.Body.Span)
                     ?? throw new InvalidOperationException("Could not deserialize ChargeCard.");

        await _pending.Writer.WriteAsync((charge, received.MessageId), cancellationToken);
        await received.Ack.AckAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (charge, messageId) in _pending.Reader.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
            await ReplyAsync(charge, messageId, stoppingToken);
        }
    }

    private Task ReplyAsync(ChargeCard charge, string causationId, CancellationToken cancellationToken)
    {
        var envelope = MessageEnvelope.From(ServiceName, charge.CorrelationId, causationId);

        switch (charge.PaymentToken)
        {
            case PaymentTokens.Approved:
                var authorizationCode = $"AUTH-{Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture)}";
                return transport.PublishAsync(new CardCharged(charge.CorrelationId, charge.OrderNumber, authorizationCode), envelope, cancellationToken);

            case PaymentTokens.Declined:
                return transport.PublishAsync(new CardDeclined(charge.CorrelationId, charge.OrderNumber, "Insufficient funds"), envelope, cancellationToken);

            default:
                logger.LogInformation("Order {OrderNumber}: charge accepted, reply withheld ({Token})", charge.OrderNumber, charge.PaymentToken);
                return Task.CompletedTask;
        }
    }
}
