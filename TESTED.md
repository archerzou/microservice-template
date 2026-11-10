# Verification

Verified on October 12, 2026 with .NET SDK 10.0.301, Docker 29.6.1, Linux containers, and PowerShell 7 on Windows.

`dotnet build Microservices.slnx -c Release` passed with no warnings or errors. `dotnet test Microservices.slnx -c Release` passed all 10 contract tests. The two API images and the replay image built from the included Dockerfile with locked NuGet restores. The NuGet vulnerability check found no reported vulnerable packages at the time of this run.

The live Docker checks passed:

- Both APIs respond to their readiness endpoints, and Orders serves its OpenAPI document.
- Eight concurrent HTTP requests with one idempotency key return one order.
- A retry with changed input returns 409.
- Repeated event delivery creates one inbox row and one notification.
- Changed content under an existing event ID goes to quarantine.
- Invalid event data goes to quarantine and leaves no inbox row.
- Orders accepts a write during a broker outage and retains the event in its outbox.
- Restarting RabbitMQ delivers the waiting event.
- A short Notifications database outage is retried without a second notification.
- A long database outage exhausts all five attempts and preserves the event in quarantine.
- The replay tool recovers that event after the database is restored and records one notification.
- Each service has only its own tables. Orders credentials fail against the Notifications database, and Orders has no route onto its database network.

Run `tests/smoke.ps1` and `tests/recovery.ps1` to repeat the integration checks. They create local test rows, temporarily stop sample containers, and restore the stopped services. They send no email.
