using System.Net.Mail;

namespace Contracts;

// A public message, not an Orders entity. Add optional fields to v1; use v2 for breaking changes.
public sealed record OrderPlacedV1(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, string Email, decimal Total)
{
    public const string Name = "orders.order-placed.v1";

    public bool IsValid() => EventId != Guid.Empty && OrderId != Guid.Empty &&
        OccurredAt != default && IsValidOrder(Email, Total);

    public static bool IsValidOrder(string? email, decimal total) =>
        !string.IsNullOrWhiteSpace(email) && email.Length <= 254 &&
        MailAddress.TryCreate(email, out var parsed) && parsed.Address == email &&
        total is > 0 and <= 1_000_000 && decimal.Round(total, 2) == total;
}
