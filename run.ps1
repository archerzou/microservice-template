# PowerShell 7, Windows/macOS/Linux. Secrets stay in the ignored local .env file.
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not (Test-Path -LiteralPath '.env')) {
        $settings = foreach ($name in @('ORDERS_PASSWORD', 'NOTIFICATIONS_PASSWORD', 'BROKER_PASSWORD')) {
            $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
            "$name=$([Convert]::ToHexString($bytes).ToLowerInvariant())"
        }
        $settings | Set-Content -LiteralPath '.env' -Encoding utf8
    }
    docker compose up -d --build --wait
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed.' }
    Write-Host 'Orders: http://localhost:5830/openapi/v1.json'
    Write-Host 'Notifications: http://localhost:5831/openapi/v1.json'
    Write-Host 'RabbitMQ: http://localhost:15683 (user template; password is BROKER_PASSWORD in .env)'
} finally { Pop-Location }
