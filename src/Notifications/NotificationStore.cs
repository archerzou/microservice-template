using Contracts;
using Npgsql;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;

namespace Notifications;

public sealed class InvalidMessageException(string message) : Exception(message);

public sealed class NotificationStore(NpgsqlDataSource source)
{
    public async Task ApplyAsync(OrderPlacedV1 message, CancellationToken ct)
    {
        // Hash the parsed contract so harmless JSON whitespace/property order changes still deduplicate.
        var canonical = new
        {
            message.EventId, OccurredAt = message.OccurredAt.UtcTicks, message.OrderId, message.Email,
            Total = message.Total.ToString("G29", CultureInfo.InvariantCulture)
        };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var inbox = new NpgsqlCommand("""
            INSERT INTO inbox (event_id, payload_hash) VALUES (@id, @hash)
            ON CONFLICT (event_id) DO NOTHING RETURNING event_id
            """, db, tx);
        inbox.Parameters.AddWithValue("id", message.EventId);
        inbox.Parameters.AddWithValue("hash", hash);
        if (await inbox.ExecuteScalarAsync(ct) is null)
        {
            await using var previous = new NpgsqlCommand("SELECT payload_hash FROM inbox WHERE event_id = @id", db, tx);
            previous.Parameters.AddWithValue("id", message.EventId);
            if ((string?)await previous.ExecuteScalarAsync(ct) != hash)
                throw new InvalidMessageException("Event ID was reused with different content.");
            return;
        }
        await using var notification = new NpgsqlCommand("""
            INSERT INTO notifications (id, order_id, email, subject) VALUES (@id, @order, @email, @subject)
            """, db, tx);
        notification.Parameters.AddWithValue("id", Guid.NewGuid());
        notification.Parameters.AddWithValue("order", message.OrderId);
        notification.Parameters.AddWithValue("email", message.Email);
        notification.Parameters.AddWithValue("subject", $"Order {message.OrderId} received");
        await notification.ExecuteNonQueryAsync(ct);
        // Both writes succeed, or neither does. External email delivery would need its own outbox.
        await tx.CommitAsync(ct);
    }
}
