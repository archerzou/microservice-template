# Exercises retry exhaustion and confirmed replay. Uses this sample's existing dead letters too.
$ErrorActionPreference = 'Stop'
$PSDefaultParameterValues = @{ 'Invoke-RestMethod:NoProxy' = $true; 'Invoke-RestMethod:TimeoutSec' = 10 }
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    function WaitFor($check, $description) {
        $deadline = [DateTime]::UtcNow.AddSeconds(100)
        $lastFailure = 'Condition stayed false.'
        do {
            try { if (& $check) { Write-Information "PASS $description" -InformationAction Continue; return } }
            catch { $lastFailure = $_.Exception.Message }
            Start-Sleep -Seconds 1
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Timed out: $description. Last failure: $lastFailure"
    }
    $settings = @{}
    Get-Content -LiteralPath '.env' | ForEach-Object { $key, $value = $_ -split '=', 2; $settings[$key] = $value }
    $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("template:$($settings.BROKER_PASSWORD)"))
    $headers = @{ Authorization = "Basic $auth" }
    function DeadCount {
        [int](Invoke-RestMethod 'http://127.0.0.1:15683/api/queues/%2F/notifications.dead' -Headers $headers).messages
    }
    $before = DeadCount
    docker compose stop notifications-db | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the sample database.' }
    $order = Invoke-RestMethod 'http://127.0.0.1:5830/orders' -Method Post -ContentType 'application/json' `
        -Headers @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() } -Body '{"email":"recovery@example.com","total":1}'
    WaitFor { (DeadCount) -gt $before } 'five failed attempts preserve the event in quarantine'

    # Pause consumption while draining the finite dead-letter batch. Invalid old messages
    # would otherwise cycle back into the same queue during this test.
    docker compose stop notifications | Out-Null
    docker compose start notifications-db | Out-Null
    $count = DeadCount
    if ($count -lt 1 -or $count -gt 100) { throw 'Use a fresh local stack when the dead-letter queue exceeds 100 entries.' }
    docker compose run --rm --build replay --count $count
    if ($LASTEXITCODE -ne 0) { throw 'Replay failed.' }
    docker compose start notifications | Out-Null
    WaitFor { (Invoke-RestMethod "http://127.0.0.1:5831/notifications/$($order.id)").orderId -eq $order.id } 'replay creates the missed notification after database recovery'
    $saved = docker compose exec -T notifications-db psql -U notifications_app -d notifications -tAc "SELECT count(*) FROM notifications WHERE order_id = '$($order.id)'"
    if ($LASTEXITCODE -ne 0 -or $saved.Trim() -ne '1') { throw 'Expected exactly one recovered notification.' }
    Write-Host 'PASS one notification after retry exhaustion and replay'
} finally {
    docker compose start notifications-db notifications | Out-Null
    Pop-Location
}
