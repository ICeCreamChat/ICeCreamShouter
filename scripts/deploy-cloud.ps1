$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Invoke-Checked {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $FilePath $($Arguments -join ' ')" }
}

Write-Host '[1/8] Checking Node.js...' -ForegroundColor Cyan
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw 'Node.js was not found. Install the LTS version from https://nodejs.org/ and run deploy-cloud.bat again.'
}

Write-Host '[2/8] Installing deployment tools...' -ForegroundColor Cyan
Invoke-Checked npm @('install')

Write-Host '[3/8] Checking Cloudflare login...' -ForegroundColor Cyan
$whoAmI = (& npx wrangler whoami 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $whoAmI -match 'not authenticated') {
    Write-Host 'A browser window will open with a device code. Log in to your own Cloudflare account and approve Wrangler.'
    Invoke-Checked npx @('wrangler', 'login', '--device')
    $whoAmI = (& npx wrangler whoami 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $whoAmI -match 'not authenticated') {
        throw 'Cloudflare authorization was not completed. Run deploy-cloud.bat again and approve the device code within five minutes.'
    }
}
Write-Host $whoAmI

$wranglerPath = Join-Path (Get-Location) 'wrangler.toml'
$wranglerText = Get-Content -LiteralPath $wranglerPath -Raw
if ($wranglerText.Contains('REPLACE_WITH_D1_DATABASE_ID')) {
    Write-Host '[4/8] Finding or creating the cloud database...' -ForegroundColor Cyan
    $listOutput = (& npx wrangler d1 list --json 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Cloudflare database list could not be read.' }
    $databases = $listOutput | ConvertFrom-Json
    $database = @($databases) | Where-Object { $_.name -eq 'cloud-remote-shouter' } | Select-Object -First 1
    $databaseId = if ($database) { if ($database.uuid) { $database.uuid } else { $database.id } } else { $null }
    if (-not $databaseId) {
        $createOutput = (& npx wrangler d1 create cloud-remote-shouter --location apac 2>&1 | Out-String)
        Write-Host $createOutput
        if ($LASTEXITCODE -ne 0) { throw 'Cloudflare database creation failed.' }
        $match = [regex]::Match($createOutput, '(?im)database_id\s*=\s*"([0-9a-f-]{36})"')
        if (-not $match.Success) { throw 'The database was created, but its ID could not be read. See docs/DEPLOYMENT_ZH.md for manual recovery.' }
        $databaseId = $match.Groups[1].Value
    }
    $wranglerText = $wranglerText.Replace('REPLACE_WITH_D1_DATABASE_ID', [string]$databaseId)
    Set-Content -LiteralPath $wranglerPath -Value $wranglerText -Encoding utf8
} else {
    Write-Host '[4/8] Existing database configuration found; skipping database creation.' -ForegroundColor Cyan
}

Write-Host '[5/8] Creating database tables...' -ForegroundColor Cyan
Invoke-Checked npx @('wrangler', 'd1', 'migrations', 'apply', 'cloud-remote-shouter', '--remote')

Write-Host '[6/8] Creating the Worker...' -ForegroundColor Cyan
Invoke-Checked npx @('wrangler', 'deploy')

Write-Host '[7/8] Setting the one-time system initialization secret...' -ForegroundColor Cyan
$secretBytes = New-Object byte[] 32
$randomGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $randomGenerator.GetBytes($secretBytes) } finally { $randomGenerator.Dispose() }
$secret = [Convert]::ToBase64String($secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$secret | & npx wrangler secret put BOOTSTRAP_TOKEN
if ($LASTEXITCODE -ne 0) { throw 'Failed to save the initialization secret.' }

Write-Host '[8/8] Publishing the final ICeCream Shouter version...' -ForegroundColor Cyan
$deployOutput = (& npx wrangler deploy 2>&1 | Out-String)
Write-Host $deployOutput
if ($LASTEXITCODE -ne 0) { throw 'Cloud deployment failed.' }
$urlMatch = [regex]::Match($deployOutput, 'https://[^\s]+\.workers\.dev')

Write-Host ''
Write-Host 'Deployment completed.' -ForegroundColor Green
if ($urlMatch.Success) {
    $systemUrl = $urlMatch.Value.TrimEnd('/')
    Write-Host "System URL: $systemUrl" -ForegroundColor Green
    $deliveryDirectory = Join-Path (Get-Location) 'dist\delivery'
    New-Item -ItemType Directory -Force -Path $deliveryDirectory | Out-Null
    Set-Content -LiteralPath (Join-Path $deliveryDirectory 'cloud-url.txt') -Value $systemUrl -Encoding utf8
    Write-Host "Saved deployment URL for prepare-delivery.bat: $deliveryDirectory\cloud-url.txt"
    Start-Process $systemUrl
} else {
    Write-Host 'System URL could not be detected automatically. prepare-delivery.bat will ask for it.' -ForegroundColor Yellow
}
Write-Host "First-login initialization secret: $secret" -ForegroundColor Yellow
Write-Host 'Open the system URL now, enter this secret, and set the ICe administrator password.' -ForegroundColor Yellow
Write-Host 'Keep the secret private. It is only used for the first initialization.'
