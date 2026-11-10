# Uses only this Compose project. Leaves its data in place and restores services after fault tests.
# Requires PowerShell 7 and a running stack (./run.ps1).
$ErrorActionPreference = 'Stop'
$PSDefaultParameterValues = @{ 'Invoke-RestMethod:NoProxy' = $true; 'Invoke-WebRequest:NoProxy' = $true; 'Invoke-RestMethod:TimeoutSec' = 10; 'Invoke-WebRequest:TimeoutSec' = 10 }
Set-StrictMode -Version Latest
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    function Assert($condition, $message) { if (-not $condition) { throw $message }; Write-Information "PASS $message" -InformationAction Continue }
    function Sql($service, $user, $database, $query) {
        $value = docker compose exec -T $service psql -U $user -d $database -tAc $query
        if ($LASTEXITCODE -ne 0) { throw "SQL failed on $service" }
        return ($value -join "`n").Trim()
    }
    function Wait-Condition($check, $description, $seconds = 75) {
        $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
        $lastFailure = 'Condition stayed false.'
        do {
            try { if (& $check) { Write-Information "PASS $description" -InformationAction Continue; return } }
            catch { $lastFailure = $_.Exception.Message }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Timed out: $description. Last failure: $lastFailure"
    }
    function Place($key, $amount = 42.50) {
        Invoke-RestMethod 'http://127.0.0.1:5830/orders' -Method Post -ContentType 'application/json' `
            -Headers @{ 'Idempotency-Key' = $key } -Body (@{ email = 'smoke@example.com'; total = $amount } | ConvertTo-Json)
    }
    $settings = @{}
    Get-Content -LiteralPath '.env' | ForEach-Object { $key, $value = $_ -split '=', 2; $settings[$key] = $value }
    $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("template:$($settings.BROKER_PASSWORD)"))
    $headers = @{ Authorization = "Basic $auth" }
    function Publish($message) {
        $body = @{
            properties = @{ delivery_mode = 2; content_type = 'application/json'; type = 'orders.order-placed.v1'; message_id = $message.eventId }
            routing_key = 'orders.order-placed.v1'; payload = ($message | ConvertTo-Json -Compress); payload_encoding = 'string'
        } | ConvertTo-Json -Depth 8
        $result = Invoke-RestMethod 'http://127.0.0.1:15683/api/exchanges/%2F/orders.events/publish' -Headers $headers -Method Post -ContentType 'application/json' -Body $body
        Assert $result.routed 'message routed to the durable queue'
    }
    function DeadCount {
        $queue = Invoke-RestMethod 'http://127.0.0.1:15683/api/queues/%2F/notifications.dead' -Headers $headers
        return [int]$queue.messages
    }
    Wait-Condition { (Invoke-RestMethod 'http://127.0.0.1:5830/health/ready').status -eq 'ready' } 'Orders ready'
    Wait-Condition { (Invoke-RestMethod 'http://127.0.0.1:5831/health/ready').status -eq 'ready' } 'Notifications ready'
    $requestKey = [guid]::NewGuid().ToString()
    $order = Place $requestKey
    $again = Place $requestKey
    Assert ($again.id -eq $order.id) 'same HTTP key returns the same order'
    $conflict = Invoke-WebRequest 'http://127.0.0.1:5830/orders' -Method Post -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = $requestKey } -Body '{"email":"smoke@example.com","total":99}' -SkipHttpErrorCheck
    Assert ($conflict.StatusCode -eq 409) 'reusing a request key for different content returns 409'
    $concurrentKey = [guid]::NewGuid().ToString()
    $parallelOrders = 1..8 | ForEach-Object -Parallel {
        Invoke-RestMethod 'http://127.0.0.1:5830/orders' -NoProxy -TimeoutSec 10 -Method Post -ContentType 'application/json' `
            -Headers @{ 'Idempotency-Key' = $using:concurrentKey } -Body '{"email":"parallel@example.com","total":5}'
    } -ThrottleLimit 8
    Assert ((@($parallelOrders.id | Select-Object -Unique)).Count -eq 1) 'eight concurrent retries return one order'
    Assert ((Sql 'orders-db' 'orders_app' 'orders' "SELECT count(*) FROM orders WHERE request_id = '$concurrentKey'") -eq '1') 'concurrent retries persist one order'
    Wait-Condition { (Invoke-RestMethod "http://127.0.0.1:5831/notifications/$($order.id)").orderId -eq $order.id } 'event creates a notification'

    $wire = Sql 'orders-db' 'orders_app' 'orders' "SELECT payload FROM outbox WHERE payload::jsonb->>'orderId' = '$($order.id)'"
    $message = $wire | ConvertFrom-Json
    Publish $message
    Publish $message
    Wait-Condition { (Sql 'notifications-db' 'notifications_app' 'notifications' "SELECT count(*) FROM inbox WHERE event_id = '$($message.eventId)'") -eq '1' } 'duplicate event has one inbox row'
    Start-Sleep -Seconds 2
    Assert ((Sql 'notifications-db' 'notifications_app' 'notifications' "SELECT count(*) FROM notifications WHERE order_id = '$($order.id)'") -eq '1') 'duplicate event creates exactly one notification'
    Assert ((Sql 'orders-db' 'orders_app' 'orders' "SELECT count(*) FROM outbox WHERE payload::jsonb->>'orderId' = '$($order.id)'") -eq '1') 'HTTP retry creates exactly one outbox event'

    $beforeDead = DeadCount
    $changed = $wire | ConvertFrom-Json
    $changed.total = 99
    Publish $changed
    Wait-Condition { (DeadCount) -gt $beforeDead } 'same event ID with changed content is quarantined'
    $beforeDead = DeadCount
    $bad = $wire | ConvertFrom-Json
    $bad.eventId = [guid]::NewGuid().ToString()
    $bad.total = -1
    Publish $bad
    Wait-Condition { (DeadCount) -gt $beforeDead } 'invalid contract is quarantined'
    Assert ((Sql 'notifications-db' 'notifications_app' 'notifications' "SELECT count(*) FROM inbox WHERE event_id = '$($bad.eventId)'") -eq '0') 'invalid event leaves no inbox row'

    docker compose stop rabbitmq | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the test broker.' }
    $offlineOrder = Place ([guid]::NewGuid().ToString())
    Assert ((Sql 'orders-db' 'orders_app' 'orders' "SELECT count(*) FROM outbox WHERE published_at IS NULL AND payload::jsonb->>'orderId' = '$($offlineOrder.id)'") -eq '1') 'broker outage keeps the committed event in the outbox'
    docker compose start rabbitmq | Out-Null
    Wait-Condition { (Invoke-RestMethod "http://127.0.0.1:5831/notifications/$($offlineOrder.id)").orderId -eq $offlineOrder.id } 'outbox resumes after a broker restart' 120

    docker compose stop notifications-db | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the test database.' }
    $retryOrder = Place ([guid]::NewGuid().ToString())
    Start-Sleep -Seconds 2
    docker compose start notifications-db | Out-Null
    Wait-Condition { (Invoke-RestMethod "http://127.0.0.1:5831/notifications/$($retryOrder.id)").orderId -eq $retryOrder.id } 'consumer retries after a short database outage'
    Assert ((Sql 'notifications-db' 'notifications_app' 'notifications' "SELECT count(*) FROM notifications WHERE order_id = '$($retryOrder.id)'") -eq '1') 'retry commits one notification'

    $notifierTables = Sql 'notifications-db' 'notifications_app' 'notifications' "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('orders','outbox')"
    $orderTables = Sql 'orders-db' 'orders_app' 'orders' "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('notifications','inbox')"
    Assert ($notifierTables -eq '0' -and $orderTables -eq '0') 'each database contains only its service data'
    $null = docker compose exec -T -e "PGPASSWORD=$($settings.ORDERS_PASSWORD)" notifications-db psql -h 127.0.0.1 -U orders_app -d notifications -w -tAc 'SELECT 1' 2>&1
    Assert ($LASTEXITCODE -ne 0) 'Orders credentials cannot authenticate to Notifications database'
    $ordersContainer = docker compose ps -q orders
    $networks = (docker inspect $ordersContainer | ConvertFrom-Json)[0].NetworkSettings.Networks.PSObject.Properties.Name
    Assert (-not ($networks | Where-Object { $_ -like '*notifications-data*' })) 'Orders has no Notifications database network'
    Write-Host 'Smoke checks passed. No email was sent. Test rows and dead letters remain for inspection.'
} finally {
    docker compose start rabbitmq notifications-db | Out-Null
    Pop-Location
}
