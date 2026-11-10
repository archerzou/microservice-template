using Contracts;
using Npgsql;
using Orders;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required.")));
builder.Services.AddHostedService<OutboxPublisher>();
var app = builder.Build();
app.UseExceptionHandler();
await using (var db = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
await using (var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sql")), db))
    await cmd.ExecuteNonQueryAsync();

app.MapOpenApi();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (NpgsqlDataSource source, CancellationToken ct) =>
{
    await using var cmd = source.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ready", acceptsOrdersWhileBrokerIsDown = true });
});

app.MapPost("/orders", async (PlaceOrder request, HttpRequest http, NpgsqlDataSource source, CancellationToken ct) =>
{
    if (!Guid.TryParse(http.Headers["Idempotency-Key"], out var requestId) || requestId == Guid.Empty)
        return Results.Problem("Send a non-empty UUID in the Idempotency-Key header.", statusCode: 400);
    if (!OrderPlacedV1.IsValidOrder(request.Email, request.Total))
        return Results.Problem("Use a valid email and a total from 0.01 to 1000000, with at most two decimal places.", statusCode: 400);

    await using var db = await source.OpenConnectionAsync(ct);
    await using var tx = await db.BeginTransactionAsync(ct);
    var orderId = Guid.NewGuid();
    await using var insert = new NpgsqlCommand("""
        INSERT INTO orders (id, request_id, email, total) VALUES (@id, @request, @email, @total)
        ON CONFLICT (request_id) DO NOTHING RETURNING id
        """, db, tx);
    insert.Parameters.AddWithValue("id", orderId);
    insert.Parameters.AddWithValue("request", requestId);
    insert.Parameters.AddWithValue("email", request.Email);
    insert.Parameters.AddWithValue("total", request.Total);
    if (await insert.ExecuteScalarAsync(ct) is null)
    {
        // A concurrent request with this key waits on the unique constraint before reaching this read.
        await using var existing = new NpgsqlCommand("SELECT id, email, total FROM orders WHERE request_id = @key", db, tx);
        existing.Parameters.AddWithValue("key", requestId);
        await using var reader = await existing.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetString(1) == request.Email && reader.GetDecimal(2) == request.Total
            ? Results.Ok(new { id = reader.GetGuid(0), request.Email, request.Total })
            : Results.Problem("This Idempotency-Key was used for a different order.", statusCode: 409);
    }
    var message = new OrderPlacedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, orderId, request.Email, request.Total);
    await using var outbox = new NpgsqlCommand("INSERT INTO outbox (id, payload) VALUES (@id, @payload)", db, tx);
    outbox.Parameters.AddWithValue("id", message.EventId);
    outbox.Parameters.AddWithValue("payload", JsonSerializer.Serialize(message, JsonSerializerOptions.Web));
    await outbox.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Created($"/orders/{orderId}", new { id = orderId, request.Email, request.Total });
}).WithName("PlaceOrder");

app.MapGet("/orders/{id:guid}", async (Guid id, NpgsqlDataSource source, CancellationToken ct) =>
{
    await using var cmd = source.CreateCommand("SELECT email, total FROM orders WHERE id = @id");
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    return await reader.ReadAsync(ct) ? Results.Ok(new { id, email = reader.GetString(0), total = reader.GetDecimal(1) }) : Results.NotFound();
});
await app.RunAsync();
