using VSaga.Abstractions.Transport;
using VSaga.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace VSaga.Samples.OrderProcessing.Participants;

internal sealed class PaymentParticipant(IMessageTransport transport, ILogger<PaymentParticipant> logger)
    : ParticipantService(transport, "PaymentService", logger)
{
    protected override string QueueName => "vsaga.participant.payment";

    private IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>? _handlers;

    // A getter (not a field initializer) so the handler delegates below can call the inherited
    // ReplyAsync — field initializers run before the base constructor finishes and can't reference
    // instance members yet (CS0236); this builds the dictionary lazily on first use instead.
    protected override IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers =>
        _handlers ??= new Dictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>
        {
            [typeof(ChargePayment)] = async (msg, received, ct) =>
            {
                var m = (ChargePayment)msg;
                await Task.Delay(Random.Shared.Next(150, 500), ct);

                var roll = Random.Shared.NextDouble();
                if (roll < 0.05)
                {
                    // Simulated hung payment gateway: never replies, so OrderSaga's Gathering reply
                    // timeout is what eventually moves this order to Failed/TimedOut.
                    logger.LogWarning("Order {OrderId}: payment gateway simulated as hung — no reply will be sent", m.OrderId);
                    return;
                }

                if (roll < 0.2)
                {
                    logger.LogWarning("Order {OrderId}: card declined", m.OrderId);
                    await ReplyAsync(new PaymentFailed(received.CorrelationId, m.OrderId, "Card declined"), received, ct);
                }
                else
                {
                    logger.LogInformation("Order {OrderId}: payment charged", m.OrderId);
                    await ReplyAsync(new PaymentCharged(received.CorrelationId, m.OrderId), received, ct);
                }
            },
            [typeof(RefundPayment)] = (msg, _, _) =>
            {
                var m = (RefundPayment)msg;
                logger.LogInformation("Order {OrderId}: payment refunded (compensation)", m.OrderId);
                return Task.CompletedTask;
            },
        };
}
