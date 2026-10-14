using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Transport.RabbitMQ.Tests;

/// <summary>
/// A publish that cannot get to the broker at all (nothing listens, or it stopped answering) fails as a
/// <see cref="MessageTransportPublishException"/>, like a nack or an unroutable return, and not as whatever the
/// client library threw (a <c>BrokerUnreachableException</c>, or a <c>TaskCanceledException</c> after its own
/// timeout of about 25 seconds for a broker that went quiet). No container: port 1 refuses the connection.
/// </summary>
public sealed class RabbitMqTransportUnreachableBrokerTests
{
    private static RabbitMqOptions Unreachable() => new() { ConnectionString = "amqp://guest:guest@127.0.0.1:1/" };

    private static RabbitMqTransport NewTransport(RabbitMqOptions options, RabbitMqConnectionManager manager) =>
        new(manager, options, new DefaultRoutingKeyConvention(), NullLogger<RabbitMqTransport>.Instance);

    [Fact]
    public async Task Publish_WhenNothingListens_ThrowsPublishExceptionNamingTheMessageAndTheCause()
    {
        var options = Unreachable();
        await using var manager = new RabbitMqConnectionManager(options);
        var correlationId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            NewTransport(options, manager).PublishAsync(new PingMessage("hi"), MessageEnvelope.New(correlationId)));

        Assert.Equal(nameof(PingMessage), ex.MessageTypeName);
        Assert.Equal(correlationId, ex.CorrelationId);
        Assert.False(ex.IsUnroutable);
        Assert.NotNull(ex.InnerException);
        Assert.Contains("could not connect to RabbitMQ or open a channel", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ex.InnerException.GetType().Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendRaw_WhenNothingListens_ThrowsPublishException()
    {
        var options = Unreachable();
        await using var manager = new RabbitMqConnectionManager(options);

        await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            NewTransport(options, manager).SendRawAsync("some.queue", nameof(PingMessage), """{"text":"hi"}"""u8.ToArray(), MessageEnvelope.New(Guid.NewGuid())));
    }

    [Fact]
    public async Task Publish_WhenTheCallerCancels_IsACancellation_NotAPublishFailure()
    {
        var options = Unreachable();
        await using var manager = new RabbitMqConnectionManager(options);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var ex = await Record.ExceptionAsync(() =>
            NewTransport(options, manager).PublishAsync(new PingMessage("hi"), MessageEnvelope.New(Guid.NewGuid()), cancelled.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }
}
