# Building on this template

This is the practical companion to [ARCHITECTURE.md](../ARCHITECTURE.md). It
walks three common tasks: adding a field to an event, making a breaking change,
and adding a third service. Match the [conventions](../ARCHITECTURE.md#conventions-this-code-follows)
the existing code follows.

## 1. Add a non-breaking field to an event

Use this when a producer has more information to share and old consumers should
keep working unchanged.

1. Add an **optional** property to `src/Contracts/OrderPlacedV1.cs`. Keep it
   nullable or give it a default so deserializing an old message still succeeds.
   ```csharp
   public sealed record OrderPlacedV1(
       Guid EventId, DateTimeOffset OccurredAt, Guid OrderId,
       string Email, decimal Total, string? Currency = null);
   ```
2. Do **not** change the routing key or `Name`. It is still v1.
3. Populate the field in `Orders/Program.cs` where the message is built.
4. The `Old_consumer_accepts_new_optional_fields` contract test already proves an
   old consumer tolerates extra JSON. Add a test for the new field's behavior.
5. Consumers opt in by reading the field when they are ready. Nothing forces a
   coordinated deploy.

## 2. Make a breaking change (v2)

Use this when the shape or meaning changes incompatibly — a renamed field, a
removed field, a changed type.

1. Create `src/Contracts/OrderPlacedV2.cs` with a new name and routing key:
   ```csharp
   public const string Name = "orders.order-placed.v2";
   ```
2. In `Transport/Broker.cs`, bind a second queue (or a v2 queue) for the new
   routing key. Leave the v1 queue and binding in place.
3. The producer publishes **both** v1 and v2 during the migration (two outbox
   rows, or one row per version), so consumers on either version keep working.
4. Move each consumer to v2 on its own schedule.
5. When no consumer reads v1, stop publishing it and remove the v1 queue,
   binding, contract, and tests.

The point of versioning in the routing key and `Type` header is that these two
flows never collide on the wire.

## 3. Add a third service

Say you want a `Shipping` service that also reacts to `OrderPlacedV1`.

### Code

1. **New project.** `dotnet new web -o src/Shipping`, then add it to
   `Microservices.slnx`. Reference `Transport` and `Contracts`, never `Orders`
   or `Notifications`.
2. **Its own schema.** Add `src/Shipping/schema.sql` with an `inbox` table plus
   its own business tables. Copy the inbox-dedup pattern from
   `NotificationStore` — do **not** reach into another service's database.
3. **Its own queue.** A second consumer needs its own queue bound to the same
   exchange and routing key, so both services get every event. Give it a
   distinct name, e.g. `shipping.order-placed.v1`, and its own dead-letter
   queue. Generalize `Broker.OpenAsync` to take the queue names, or add a
   shipping-specific setup method.
4. **Reuse the consumer shape.** `OrderConsumer` is the template: `prefetch=1`,
   bounded retry with backoff, quarantine-before-ack, inbox write in the same
   transaction as the business write.

### Infrastructure

5. **New database + network** in `compose.yaml`, mirroring `notifications-db`:
   its own `shipping-data` **internal** network, its own credentials, no host
   port.
   ```yaml
   shipping:
     build: { context: ., args: { SERVICE: Shipping } }
     ports: ["127.0.0.1:5832:8080"]
     environment:
       ConnectionStrings__Database: Host=shipping-db;Database=shipping;Username=shipping_app;Password=${SHIPPING_PASSWORD:?Run ./run.ps1 first};Timeout=3;Command Timeout=5
       Broker__Host: rabbitmq
       Broker__User: template
       Broker__Password: ${BROKER_PASSWORD:?Run ./run.ps1 first}
     depends_on:
       shipping-db: { condition: service_healthy }
       rabbitmq: { condition: service_healthy }
     networks: [shipping-data, messages]
   ```
6. **New secret.** Add `SHIPPING_PASSWORD` to the generated list in `run.ps1`.

### Verify

7. Add contract and smoke coverage for the new consumer: duplicate delivery
   produces one shipment, poison data lands in the new dead-letter queue, a
   database outage is retried and recovered.

## If you are a producer, not a consumer

A service that *emits* events needs the outbox half instead:

- An `outbox` table and the `OutboxPublisher` pattern (publish with confirms,
  mark sent only after the confirm, commit).
- A contract in `src/Contracts` with validation on it.
- The business write and the outbox insert in **one transaction**.

## Checklist before you call it done

- [ ] New service references only `Transport` and `Contracts`.
- [ ] Its database is on an `internal` network with no host port and its own
      credentials.
- [ ] Every multi-row invariant is inside one transaction.
- [ ] Every event handler is idempotent (inbox or an external idempotency key).
- [ ] All SQL is parameterized; no customer data in logs or queue headers.
- [ ] `CancellationToken` is threaded through every async call.
- [ ] `dotnet build -c Release` is clean (warnings are errors here).
- [ ] Smoke coverage exists for duplicate, poison, and outage paths.
