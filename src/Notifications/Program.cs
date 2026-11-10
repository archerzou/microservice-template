using Npgsql;
using Notifications;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required.")));
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddHostedService<OrderConsumer>();
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
    return Results.Ok(new { status = "ready" });
});
app.MapGet("/notifications/{orderId:guid}", async (Guid orderId, NpgsqlDataSource source, CancellationToken ct) =>
{
    await using var cmd = source.CreateCommand("SELECT id, email, subject FROM notifications WHERE order_id = @id");
    cmd.Parameters.AddWithValue("id", orderId);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    return await reader.ReadAsync(ct)
        ? Results.Ok(new { id = reader.GetGuid(0), orderId, email = reader.GetString(1), subject = reader.GetString(2), delivery = "Recorded locally; no email is sent." })
        : Results.NotFound();
});
await app.RunAsync();
