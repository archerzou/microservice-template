using Contracts;
using Npgsql;
using RabbitMQ.Client;
using System.Text;
using Transport;

namespace Orders;

public sealed class OutboxPublisher(NpgsqlDataSource source, IConfiguration config, ILogger<OutboxPublisher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await Broker.ConnectAsync(config["Broker:Host"]!, config["Broker:User"]!, config["Broker:Password"]!, "orders.outbox", stoppingToken);
                await using var channel = await Broker.OpenAsync(connection, stoppingToken);
                while (!stoppingToken.IsCancellationRequested && connection.IsOpen && channel.IsOpen)
                {
                    var sent = await PublishNextAsync(channel, stoppingToken);
                    failures = 0;
                    if (!sent) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var seconds = Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)));
                log.LogWarning(exception, "Outbox delivery paused; retrying in {Seconds}s. Unsent rows stay in Orders.", seconds);
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<bool> PublishNextAsync(IChannel channel, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var pick = new NpgsqlCommand("""
            SELECT id, payload FROM outbox WHERE published_at IS NULL
            ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED
            """, db, tx);
        Guid eventId;
        string payload;
        await using (var reader = await pick.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            eventId = reader.GetGuid(0);
            payload = reader.GetString(1);
        }
        // Wait for the broker's confirm. A crash after this line can resend the message.
        // Notifications handles that with its inbox in the same transaction as its write.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await channel.BasicPublishAsync(Broker.Exchange, OrderPlacedV1.Name, mandatory: true,
            Broker.Properties(eventId), Encoding.UTF8.GetBytes(payload), timeout.Token);
        await using var mark = new NpgsqlCommand("UPDATE outbox SET published_at = now() WHERE id = @id", db, tx);
        mark.Parameters.AddWithValue("id", eventId);
        await mark.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
