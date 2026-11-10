# Microservices starter

Two .NET 10 APIs, two PostgreSQL databases, and one RabbitMQ broker. Place an order and watch Notifications record it through a versioned event. Stop the broker, retry a request, or deliver the same event twice to see how each failure is handled.

This is a small code base you can change. It has no gateway, identity server, payment flow, or cloud setup. The notification is a database record; this sample sends no email.

| Layer | Choice |
|---|---|
| Runtime | .NET 10, ASP.NET Core minimal APIs |
| Data | PostgreSQL 18, Npgsql (raw SQL, no ORM) |
| Messaging | RabbitMQ 4.2, quorum queues, RabbitMQ .NET client (no MassTransit) |
| Patterns | Transactional outbox, inbox dedup, request idempotency, dead-letter + bounded retry |
| Host | Docker Compose; per-service databases on internal networks |
| Tests | xUnit contract tests, PowerShell smoke and recovery scripts |

For the patterns and the delivery guarantee in depth, see [ARCHITECTURE.md](ARCHITECTURE.md). To extend the template, see [docs/adding-a-service.md](docs/adding-a-service.md).

## Run it

Install Docker with Compose and PowerShell 7. The containers include .NET; you only need the .NET 10 SDK if you want to build or run the unit tests on your host.

From this folder:

```powershell
./run.ps1
```

The script creates fresh local passwords in an ignored `.env` file, then builds and starts the stack. Keep that file while you keep the database volumes. Run `docker compose down` to stop it without deleting data. This project uses its own Compose name and ports, so it can run next to the modular monolith starter.

| Service | Local URL |
|---|---|
| Orders API | http://localhost:5830/openapi/v1.json |
| Notifications API | http://localhost:5831/openapi/v1.json |
| RabbitMQ console | http://localhost:15683 |

The broker console user is `template`. Its password is `BROKER_PASSWORD` in `.env`. All published ports bind to localhost. Neither database has a host port.

Create an order:

```powershell
$key = [guid]::NewGuid().ToString()
$order = Invoke-RestMethod http://localhost:5830/orders -Method Post `
  -ContentType application/json -Headers @{ 'Idempotency-Key' = $key } `
  -Body '{"email":"reader@example.com","total":42.50}'
Invoke-RestMethod "http://localhost:5831/notifications/$($order.id)"
```

The second call can return 404 while the event is in flight. Try it again after a second. The `examples.http` file has the same requests for your editor.

## Follow one write

```text
POST /orders
     |
     v
Orders API --> Orders DB [order + outbox, one transaction]
                              |
                        outbox publisher
                              |
                        RabbitMQ confirms
                              |
                       OrderPlacedV1 event
                              |
Notifications API --> Notifications DB [inbox + notification, one transaction]
                              |
                        ACK after commit
```

Orders owns its order and outbox tables. Notifications owns its inbox and notification tables. They have separate database containers, credentials, and database networks. Neither API references the other API project. The shared `Contracts` project defines the message; `Transport` defines RabbitMQ setup only.

Read `src/Orders/Program.cs`, `OutboxPublisher.cs`, `src/Contracts/OrderPlacedV1.cs`, then `src/Notifications/OrderConsumer.cs` and `NotificationStore.cs`. Those files contain the whole workflow. [ARCHITECTURE.md](ARCHITECTURE.md) walks the same path with the reasoning behind each step.

## What survives a failure

- Retrying an HTTP request with the same UUID key returns the same order. Reusing that key for different input returns 409.
- Order creation and the outbox write commit together. If RabbitMQ is down, the API can still accept orders and the publisher keeps trying.
- The publisher marks an event as sent only after RabbitMQ confirms it. A crash between that confirm and the database commit can send it twice.
- Notifications writes its inbox row and notification in one transaction. A duplicate event does not make a second notification. An event ID reused with changed content goes to quarantine.
- The consumer retries a failed database operation five times, with waits of 1, 2, 4, and 8 seconds. Invalid messages go straight to `notifications.dead`. The dead-letter copy is confirmed before the original is acknowledged.
- A broker or process failure leaves unacknowledged messages available for delivery. A consumer restart resets the five-attempt budget; it does not lose the message.

These are at-least-once deliveries. The inbox makes this one database effect idempotent. Sending an email or charging a card inside the handler would need an outbox or an idempotency key at that external service too.

## Test the failure paths

```powershell
dotnet test Microservices.slnx -c Release
./tests/smoke.ps1
./tests/recovery.ps1
```

The unit tests protect the v1 wire shape and input bounds. The smoke script checks real HTTP retries, duplicate delivery, changed content under the same event ID, poison messages, broker restart recovery, a short database outage, and database isolation. It stops only this sample's broker or Notifications database, restores them in `finally`, and leaves its test rows for inspection.

Try the outbox by hand: stop `rabbitmq`, place an order, then start `rabbitmq`. The API still returns the order while the notification waits. Both APIs expose `/health/live` and `/health/ready`. Readiness checks each API's own database. It does not promise that the consumer is connected or that the outbox is empty; use broker metrics and outbox age for that.

## Recover a dead letter

Open `notifications.dead` in the RabbitMQ console and inspect its `failure-reason` header. Fix the cause before replaying. For example, restore the Notifications database after a long outage. Then replay one message:

```powershell
docker compose run --rm --build replay --count 1
```

The tool keeps the original event ID, waits for a publish confirm, and only then removes the dead-letter copy. A crash can produce a duplicate, which the inbox handles. It does not fix invalid JSON or bad data; those messages will return to quarantine. It reads the oldest message first, so inspect the queue before replaying a batch.

The recovery test keeps the database down until all five attempts fail, restores it, then replays the queued event. It checks that one notification is saved. Run it on this local sample; it replays existing dead letters too.

## Make it yours

Rename the two service projects to match two separate business jobs. Keep each service's data private. Add optional fields to `OrderPlacedV1` without breaking old consumers. For a breaking change, create a v2 contract and routing key, and run both versions during the change. [docs/adding-a-service.md](docs/adding-a-service.md) has the step-by-step for each of these, including adding a third service.

The SQL files create the starting schema on first run. They are not a migration system. Add versioned database migrations before you change a deployed schema. Give each runtime a restricted database role and keep schema changes in a separate deployment step.

Before hosting it, add authentication, authorization, HTTPS, secret storage, a private broker, restricted broker users, logs and metrics, backups, retention for inbox/outbox rows, and load tests for your traffic. This local stack uses one broker, so it cannot demonstrate high availability. The publisher holds a database lock while it waits for a confirm; batch publishing is a separate throughput tradeoff. The consumer handles one message at a time so the failure path stays easy to follow.

## Where this came from

This starter distills the service boundaries, integration events, outbox, and inbox patterns from Milan Jovanovic's Evently modular monolith course and its microservices extraction section. The code here was written for this download. It does not bundle the course project, slides, exercises, or paid assets. It uses the RabbitMQ .NET client directly; it has no MassTransit dependency.

For protocol details, see the [RabbitMQ .NET client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) and [publisher confirms and consumer acknowledgements](https://www.rabbitmq.com/docs/confirms).
