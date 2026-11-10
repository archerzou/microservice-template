using Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text.Json;
using Transport;

namespace Notifications;

public sealed class OrderConsumer(NotificationStore store, IConfiguration config, ILogger<OrderConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await Broker.ConnectAsync(config["Broker:Host"]!, config["Broker:User"]!, config["Broker:Password"]!, "notifications.consumer", stoppingToken);
                await using var channel = await Broker.OpenAsync(connection, stoppingToken);
                await channel.BasicQosAsync(0, 1, global: false, stoppingToken);
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdownAsync += (_, _) => { ended.TrySetResult(); return Task.CompletedTask; };
                channel.ChannelShutdownAsync += (_, _) => { ended.TrySetResult(); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try { await HandleAsync(channel, delivery, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { ended.TrySetResult(); }
                    catch (Exception exception)
                    {
                        // If ACK or quarantine publish fails, reconnect. The original is still unacked.
                        log.LogWarning(exception, "Delivery interrupted. Reconnecting so the broker can redeliver.");
                        ended.TrySetResult();
                    }
                };
                await channel.BasicConsumeAsync(Broker.Queue, autoAck: false, consumer, stoppingToken);
                await ended.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { log.LogWarning(exception, "Consumer disconnected; retrying in 2s."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        // RabbitMQ owns delivery.Body; finish using it before this callback returns.
        OrderPlacedV1 message;
        try
        {
            message = JsonSerializer.Deserialize<OrderPlacedV1>(delivery.Body.Span, JsonSerializerOptions.Web)
                ?? throw new InvalidMessageException("Empty event.");
            if (delivery.BasicProperties.Type != OrderPlacedV1.Name || !message.IsValid() ||
                delivery.BasicProperties.MessageId != message.EventId.ToString())
                throw new InvalidMessageException("Invalid v1 contract or message ID.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidMessageException)
        {
            await QuarantineAsync(channel, delivery, exception.Message, ct);
            return;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await store.ApplyAsync(message, ct);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvalidMessageException exception)
            {
                await QuarantineAsync(channel, delivery, exception.Message, ct);
                return;
            }
            catch (Exception exception) when (attempt < 5)
            {
                log.LogWarning(exception, "Event {EventId}: attempt {Attempt}/5 failed.", message.EventId, attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            }
            catch (Exception exception)
            {
                // Store the exception type, not database details or customer data, in the queue header.
                await QuarantineAsync(channel, delivery, $"Five attempts failed: {exception.GetType().Name}", ct);
            }
        }
    }

    private async Task QuarantineAsync(IChannel channel, BasicDeliverEventArgs delivery, string reason, CancellationToken ct)
    {
        var properties = new BasicProperties(delivery.BasicProperties)
        {
            Persistent = true,
            Headers = new Dictionary<string, object?> { ["failure-reason"] = reason }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // Confirm the durable copy BEFORE ACK. A crash can duplicate a dead letter, but cannot drop it here.
        await channel.BasicPublishAsync("", Broker.DeadQueue, mandatory: true, properties, delivery.Body, timeout.Token);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        log.LogWarning("Event {EventId} moved to {Queue}: {Reason}", delivery.BasicProperties.MessageId, Broker.DeadQueue, reason);
    }
}
