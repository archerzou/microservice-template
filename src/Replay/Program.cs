using Contracts;
using RabbitMQ.Client;
using Transport;

if (args.Length != 2 || args[0] != "--count" || !int.TryParse(args[1], out var count) || count is < 1 or > 100)
{
    await Console.Error.WriteLineAsync("Usage: docker compose run --rm replay --count 1 (1 to 100 messages)");
    return 1;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await using var connection = await Broker.ConnectAsync(
    Environment.GetEnvironmentVariable("Broker__Host")!,
    Environment.GetEnvironmentVariable("Broker__User")!,
    Environment.GetEnvironmentVariable("Broker__Password")!, "dead-letter-replay", timeout.Token);
await using var channel = await Broker.OpenAsync(connection, timeout.Token);
for (var i = 0; i < count; i++)
{
    var delivery = await channel.BasicGetAsync(Broker.DeadQueue, autoAck: false, timeout.Token);
    if (delivery is null) { Console.WriteLine("No more dead letters."); break; }
    var properties = new BasicProperties(delivery.BasicProperties) { Persistent = true };
    // Preserve event identity. Confirm the replay before removing its dead-letter copy.
    await channel.BasicPublishAsync(Broker.Exchange, OrderPlacedV1.Name, mandatory: true, properties, delivery.Body, timeout.Token);
    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, timeout.Token);
    Console.WriteLine($"Replayed {properties.MessageId}. Check Notifications for the result.");
}
return 0;
