# Architecture

This document is the reference behind the [README](README.md). It explains the
patterns, the delivery guarantee, the project layout, and the conventions the
code follows. Read it when you want to understand *why* a line is written the
way it is, or when you extend the template.

## Design goals

1. **One honest delivery path.** A single write travels from an HTTP request to a
   durable notification, crossing a process and a broker, and survives the
   failures that matter. Everything that does not serve that path is left out.
2. **Services that are genuinely independent.** Separate databases, credentials,
   and networks. No shared schema, no shared domain model, no project reference
   between the two services. The only coupling is a versioned message contract.
3. **Small enough to read in one sitting.** The whole workflow is five files.
   The failure handling is explicit and sequential, not hidden behind a
   framework.

## System view

```
            HTTP (localhost)
                  |
        +---------v---------+          +-----------------------+
        |     Orders API    |          |   Notifications API   |
        |  (minimal API +   |          |  (minimal API +       |
        |   OutboxPublisher)|          |   OrderConsumer)      |
        +----+---------+----+          +----+-------------+----+
             |         |                    |             |
     orders-data    messages            messages   notifications-data
     (internal)        |                    |          (internal)
             |         |                    |             |
        +----v----+  +-v--------------------v-+     +----v-----------+
        | orders  |  |        RabbitMQ        |     | notifications  |
        |   DB    |  | orders.events exchange |     |     DB         |
        +---------+  | quorum queues + DLQ    |     +----------------+
                     +------------------------+
```

- `orders-data` and `notifications-data` are **internal** Docker networks: the
  database containers have no host port and are unreachable across services.
- `messages` is the only shared network, and it carries the broker alone.
- Each API reaches exactly one database and the broker. Nothing else.

## The delivery guarantee

The system is **at-least-once with idempotent application**. A given
`OrderPlacedV1` event may be *delivered* more than once, but it produces exactly
one notification. That property is built from four cooperating pieces.

### 1. Request idempotency (producer edge)

`POST /orders` requires an `Idempotency-Key` (a non-empty UUID). The order row
has a `UNIQUE (request_id)` constraint, and the insert uses
`ON CONFLICT (request_id) DO NOTHING RETURNING id`.

- First call with a key: inserts and returns `201`.
- Retry with the same key and the same body: returns the existing order, `200`.
- Same key, different body: returns `409`.

Concurrent requests with the same key serialize on the unique index, so the
read-back after a conflict always sees the committed row. See
`src/Orders/Program.cs`.

### 2. Transactional outbox (producer → broker)

The order row and an `outbox` row are written in **one database transaction**.
The API never publishes to RabbitMQ inside the request. If the broker is down,
orders are still accepted; the event waits.

A background `OutboxPublisher` (`src/Orders/OutboxPublisher.cs`) then:

1. Picks the oldest unpublished row with `FOR UPDATE SKIP LOCKED` (safe to run
   more than one publisher).
2. Publishes with **publisher confirms** and `mandatory: true`.
3. Marks the row `published_at = now()` only after the broker confirms, then
   commits.

A crash between the broker's confirm and the `UPDATE` commit resends the event
on restart. That is the deliberate duplicate window the inbox absorbs.

### 3. Inbox dedup (broker → consumer write)

`NotificationStore.ApplyAsync` (`src/Notifications/NotificationStore.cs`) writes
an `inbox` row and the `notifications` row in **one transaction**:

- The inbox insert is `ON CONFLICT (event_id) DO NOTHING`. A duplicate event is
  recognized and the business write is skipped — one notification, always.
- The inbox stores a SHA-256 hash of the *canonical* event (parsed fields, not
  raw bytes, so whitespace and property order do not matter). An event ID
  reused with different content is rejected to quarantine, not silently applied.

### 4. Dead-letter + bounded retry (consumer failure path)

`OrderConsumer` (`src/Notifications/OrderConsumer.cs`) processes one message at a
time (`BasicQos(prefetch=1)`) so the failure path stays linear:

- Malformed or invalid-contract messages go **straight to quarantine**
  (`notifications.dead`), never retried.
- Transient database errors are retried **five times** with `1, 2, 4, 8s`
  backoff, then quarantined with the exception *type* only (no customer data in
  headers).
- The dead-letter copy is **publish-confirmed before the original is acked**, so
  a message can be duplicated by a crash but never lost.
- Messages are acked only after the transaction commits. A broker or process
  failure leaves them unacked and redeliverable; a restart resets the attempt
  budget.

### Why this still is not exactly-once

There is no exactly-once across a network. The inbox makes the *database* effect
idempotent. A side effect that leaves the database — sending an email, charging
a card — would need its own outbox or an idempotency key at that external
service. The README's "What survives a failure" section states this explicitly.

## Message contract versioning

`Contracts/OrderPlacedV1.cs` is a **public wire contract**, not an Orders
entity. The rules:

- The routing key and `Type` header carry the version: `orders.order-placed.v1`.
- **Additive, non-breaking changes** stay on v1. Add *optional* fields. Old
  consumers ignore unknown JSON (`ContractTests` pins this).
- **Breaking changes** get a new `OrderPlacedV2` record and a new routing key.
  Publish both during the migration; retire v1 when every consumer has moved.

Validation lives on the contract (`IsValid`, `IsValidOrder`) so the producer and
consumer agree on the bounds without sharing code paths.

## Project layout

```
src/
  Contracts/      OrderPlacedV1 — the only thing both services share. No I/O.
  Transport/      RabbitMQ setup only (exchange, queues, properties). No domain.
  Orders/         Minimal API, idempotent POST, outbox table + publisher.
  Notifications/  Minimal API, consumer, inbox dedup + notification write.
  Replay/         CLI tool to re-publish dead letters. Runs via compose profile.
tests/
  ContractTests/  xUnit; pins the v1 wire shape and validation bounds.
  smoke.ps1       Real HTTP retries, duplicate delivery, poison, broker restart.
  recovery.ps1    Exhausts retries, then replays the dead letter.
```

Dependency direction: `Orders` and `Notifications` each reference `Transport`
and `Contracts`. They never reference each other. `Transport` references
`Contracts` for the routing key and nothing else.

## Configuration reference

All configuration is environment variables (12-factor). `compose.yaml` wires
them; `run.ps1` generates the secrets into an ignored `.env` on first run.

| Variable | Used by | Purpose |
|---|---|---|
| `ConnectionStrings__Database` | both APIs | Npgsql connection string. `Timeout=3;Command Timeout=5` keep failures fast. |
| `Broker__Host` / `Broker__User` / `Broker__Password` | both APIs, Replay | RabbitMQ connection. |
| `ORDERS_PASSWORD` / `NOTIFICATIONS_PASSWORD` / `BROKER_PASSWORD` | compose | Secrets in `.env`; interpolated into the strings above. `:?` makes a missing value fail the stack. |

| Port (localhost only) | Service |
|---|---|
| 5830 | Orders API |
| 5831 | Notifications API |
| 15683 | RabbitMQ management console |

Databases expose **no** host port by design.

## Conventions this code follows

These are the standards a new service in this repo is expected to match.

- **One concern per file, sealed types.** `OutboxPublisher`, `OrderConsumer`,
  `NotificationStore` each own one job. Records are `sealed`.
- **Nullable enabled, warnings are errors, locked restores.** Set in
  `Directory.Build.props`. CI-grade defaults on by default.
- **`CancellationToken` threaded through every async call.** Shutdown is prompt
  and clean; no fire-and-forget.
- **Parameterized SQL only.** Every value goes through `NpgsqlParameter`. No
  string interpolation into SQL.
- **Explicit transactions around multi-row invariants.** Outbox+order,
  inbox+notification. Commit both or neither.
- **Fail fast on missing config.** Connection strings throw at startup if
  absent; compose refuses to start without secrets.
- **No secrets in the repo.** `.env` is generated and git-ignored.
- **Failures are logged with context, never customer data.** Dead-letter
  headers carry an exception type, not an email or SQL text.
- **Schema is applied idempotently** (`CREATE TABLE IF NOT EXISTS`) on startup —
  for the sample only. Production uses versioned migrations (see below).

## From sample to production

The README's "Make it yours" section is the checklist. The architectural points
behind it:

- **Migrations, not `schema.sql`.** The startup schema is a convenience, not a
  migration system. Add a real migration tool before you change a deployed
  schema, and give each runtime a *restricted* database role.
- **Retention.** Inbox and outbox rows grow forever here. Add a retention job
  that prunes published outbox rows and old inbox rows past your replay window.
- **Observability.** Add structured logs, metrics, and tracing. Alert on outbox
  age (oldest unpublished row) and dead-letter depth — readiness alone does not
  prove the pipeline is flowing.
- **Throughput.** The publisher holds a row lock while awaiting one confirm, and
  the consumer handles one message at a time, both to keep the failure path
  readable. Batch publishing and higher prefetch are the levers; each trades
  simplicity for throughput.
- **Availability.** One broker here means no HA claim. Quorum queues are already
  declared; a real cluster plus a connection list is the next step.
- **Edge.** Add authentication, authorization, HTTPS/TLS, a gateway, a private
  broker with per-service users, secret storage, and backups before hosting.

## Further reading

- [RabbitMQ .NET client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide)
- [Publisher confirms and consumer acknowledgements](https://www.rabbitmq.com/docs/confirms)
- Milan Jovanović's Evently course — the source of the boundary, event, outbox,
  and inbox patterns distilled here.
