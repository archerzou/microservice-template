using Contracts;
using RabbitMQ.Client;

namespace Transport;

// The shared transport owns protocol setup only. Neither service shares a database or domain model.
public static class Broker
{
    public const string Exchange = "orders.events";
    public const string Queue = "notifications.order-placed.v1";
    public const string DeadQueue = "notifications.dead";

    public static Task<IConnection> ConnectAsync(string host, string user, string password, string name, CancellationToken ct) =>
        new ConnectionFactory
        {
            HostName = host, UserName = user, Password = password,
            ClientProvidedName = name, AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            RequestedHeartbeat = TimeSpan.FromSeconds(10)
        }.CreateConnectionAsync(ct);

    public static async Task<IChannel> OpenAsync(IConnection connection, CancellationToken ct)
    {
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        // Quorum queues survive a broker restart. This local example has one broker, so no HA claim.
        var args = new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-delivery-limit"] = -1 };
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, args, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false, args, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, OrderPlacedV1.Name, cancellationToken: ct);
        return channel;
    }

    public static BasicProperties Properties(Guid eventId) => new()
    {
        Persistent = true, ContentType = "application/json", Type = OrderPlacedV1.Name,
        MessageId = eventId.ToString(), CorrelationId = eventId.ToString()
    };
}
