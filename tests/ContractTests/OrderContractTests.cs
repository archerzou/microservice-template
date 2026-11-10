using Contracts;
using System.Text.Json;
using Xunit;

namespace ContractTests;

public class OrderContractTests
{
    [Fact]
    public void Released_v1_wire_shape_stays_readable()
    {
        const string wire = """
            {"eventId":"2f43c6a9-776b-4333-a550-f1707b09c9ab","occurredAt":"2026-10-12T12:00:00Z",
             "orderId":"a7a78d32-32a5-4346-ac28-ae1992b03bdf","email":"reader@example.com","total":42.50}
            """;
        var message = JsonSerializer.Deserialize<OrderPlacedV1>(wire, JsonSerializerOptions.Web)!;
        Assert.True(message.IsValid());
        Assert.Equal(42.50m, message.Total);
        Assert.Equal("reader@example.com", message.Email);
    }

    [Fact]
    public void Old_consumer_accepts_new_optional_fields()
    {
        const string wire = """
            {"eventId":"2f43c6a9-776b-4333-a550-f1707b09c9ab","occurredAt":"2026-10-12T12:00:00Z",
             "orderId":"a7a78d32-32a5-4346-ac28-ae1992b03bdf","email":"reader@example.com","total":42.50,
             "optionalReference":"new-producer"}
            """;
        Assert.True(JsonSerializer.Deserialize<OrderPlacedV1>(wire, JsonSerializerOptions.Web)!.IsValid());
    }

    [Theory]
    [InlineData("reader@example.com", "0")]
    [InlineData("reader@example.com", "-1")]
    [InlineData("reader@example.com", "1.001")]
    [InlineData("reader@example.com", "1000000.01")]
    [InlineData("not-an-email", "10")]
    [InlineData("Name <reader@example.com>", "10")]
    [InlineData("", "10")]
    public void Rejects_values_that_cannot_be_recorded_or_addressed(string email, string total)
    {
        Assert.False(OrderPlacedV1.IsValidOrder(email, decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Missing_identity_is_not_a_valid_event() =>
        Assert.False(new OrderPlacedV1(Guid.Empty, DateTimeOffset.UtcNow, Guid.NewGuid(), "reader@example.com", 1).IsValid());
}
