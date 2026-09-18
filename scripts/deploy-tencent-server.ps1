$ErrorActionPreference = 'Stop'
if ($PSScriptRoot) { Set-Location (Split-Path -Parent $PSScriptRoot) }

function Read-WithDefault([string]$Prompt, [string]$Default) {
    $value = Read-Host "$Prompt（直接回车使用 $Default）"
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value.Trim()
}

function ConvertTo-PlainText([Security.SecureString]$SecureValue) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Read-AdminPassword {
    while ($true) {
        $first = ConvertTo-PlainText (Read-Host '请设置 ICe 管理员密码（至少10位，包含字母和数字）' -AsSecureString)
        $second = ConvertTo-PlainText (Read-Host '请再次输入相同密码' -AsSecureString)
        if ($first -ne $second) {
            Write-Host '两次输入不一致，请重新输入。' -ForegroundColor Yellow
            continue
        }
        if ($first.Length -lt 10 -or $first.Length -gt 128 -or $first -notmatch '[A-Za-z]' -or $first -notmatch '\d') {
            Write-Host '密码不符合要求：至少10位，并同时包含字母和数字。' -ForegroundColor Yellow
            continue
        }
        return $first
    }
}

function New-RandomToken([int]$Bytes = 32) {
    $buffer = New-Object byte[] $Bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-Sha256Hex([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

foreach ($command in @('ssh.exe', 'scp.exe', 'tar.exe')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Windows 缺少 $command。请在 Windows 设置的可选功能中安装 OpenSSH 客户端。"
    }
}

$domain = Read-WithDefault '请输入正式域名' 'icecreamchat.top'
if ($domain -notmatch '^([A-Za-z0-9-]+\.)+[A-Za-z]{2,}$') { throw '域名格式不正确。' }
$serverIp = Read-WithDefault '请输入腾讯云服务器公网 IP' '175.27.212.68'
if ($serverIp -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw '公网 IP 格式不正确。' }
$sshUser = Read-WithDefault '请输入服务器登录用户名' 'ubuntu'
if ($sshUser -notmatch '^[A-Za-z_][A-Za-z0-9_-]*$') { throw '服务器登录用户名格式不正确。' }

$addresses = @(Resolve-DnsName $domain -Type A -ErrorAction SilentlyContinue | Where-Object IPAddress | Select-Object -ExpandProperty IPAddress)
if ($addresses -notcontains $serverIp) {
    throw "$domain 当前没有解析到 $serverIp。请先在腾讯云 DNS 添加或修改 A 记录。"
}

$bootstrapRequired = $true
try {
    $bootstrapStatus = Invoke-RestMethod -Method Get -Uri "https://$domain/api/bootstrap" -TimeoutSec 15
    $bootstrapRequired = [bool]$bootstrapStatus.required
} catch {
    Write-Host '暂时无法读取服务器初始化状态，将按首次部署流程继续。' -ForegroundColor Yellow
}

$adminPassword = $null
if ($bootstrapRequired) {
    $adminPassword = Read-AdminPassword
} else {
    Write-Host '检测到系统已经初始化，将保留现有 ICe 账号和密码。' -ForegroundColor Green
}

Write-Host ''
Write-Host '正在构建新版教室端 EXE...' -ForegroundColor Cyan
$env:CRS_NO_PAUSE = '1'
& (Join-Path (Get-Location) 'build.bat')
if ($LASTEXITCODE -ne 0) { throw '教室端 EXE 构建失败。' }

$receiverProject = [xml](Get-Content -LiteralPath (Join-Path (Get-Location) 'Receiver\Receiver.csproj') -Raw)
$receiverVersion = [string]$receiverProject.Project.PropertyGroup.Version
if ($receiverVersion -notmatch '^\d+(\.\d+){1,3}$') { throw 'Receiver.csproj 中的版本号格式不正确。' }
$receiverExecutable = Join-Path (Get-Location) 'dist\receiver\ICeCreamShouter.exe'
if (-not (Test-Path -LiteralPath $receiverExecutable)) { throw '构建完成后没有找到教室端 EXE。' }
$receiverFile = Get-Item -LiteralPath $receiverExecutable
$receiverHash = Get-Sha256Hex $receiverExecutable

$bootstrapToken = New-RandomToken
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("cloud-remote-shouter-" + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $temporaryRoot 'package'
$archive = Join-Path $temporaryRoot 'cloud-remote-shouter.tar.gz'
New-Item -ItemType Directory -Path $packageRoot | Out-Null

try {
    Write-Host ''
    Write-Host '正在制作服务器安装包...' -ForegroundColor Cyan
    foreach ($relativeDirectory in @('server', 'web_controller', 'cloud\migrations', 'receiver_release')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot $relativeDirectory) | Out-Null
    }
    foreach ($relativeFile in @(
        'server\app.py',
        'server\requirements.txt',
        'server\install-ubuntu.sh',
        'web_controller\index.html',
        'web_controller\favicon.svg',
        'cloud\migrations\0001_initial.sql'
    )) {
        Copy-Item -LiteralPath (Join-Path (Get-Location) $relativeFile) -Destination (Join-Path $packageRoot $relativeFile) -Force
    }
    Copy-Item -LiteralPath $receiverExecutable -Destination (Join-Path $packageRoot 'receiver_release\ICeCreamShouter.exe') -Force
    $manifest = [ordered]@{
        version = $receiverVersion
        sha256 = $receiverHash
        size = [long]$receiverFile.Length
    } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $packageRoot 'receiver_release\manifest.json'),
        $manifest,
        (New-Object Text.UTF8Encoding($false))
    )
    & tar.exe -C $packageRoot -czf $archive server web_controller cloud receiver_release
    if ($LASTEXITCODE -ne 0) { throw '服务器安装包制作失败。' }

    Write-Host ''
    Write-Host "接下来会要求输入服务器 $sshUser 的登录密码。输入时屏幕不会显示字符，这是正常现象。" -ForegroundColor Yellow
    Write-Host '第一次用于上传，第二次用于安装。' -ForegroundColor Yellow
    & scp.exe -o StrictHostKeyChecking=accept-new $archive "${sshUser}@${serverIp}:/tmp/cloud-remote-shouter.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "安装包上传失败。请确认 $sshUser 登录密码和腾讯云防火墙的 SSH（22端口）。" }

    $remote = "rm -rf /tmp/cloud-remote-shouter-install && mkdir -p /tmp/cloud-remote-shouter-install && tar -xzf /tmp/cloud-remote-shouter.tar.gz -C /tmp/cloud-remote-shouter-install && sudo bash /tmp/cloud-remote-shouter-install/server/install-ubuntu.sh '$domain' '$bootstrapToken'"
    & ssh.exe -t -o StrictHostKeyChecking=accept-new "${sshUser}@${serverIp}" $remote
    if ($LASTEXITCODE -ne 0) { throw '服务器安装没有完成。请查看上方最后一条错误。' }

    if ($bootstrapRequired) {
        Write-Host ''
        Write-Host '正在初始化最高管理员 ICe...' -ForegroundColor Cyan
        $body = @{ bootstrapToken = $bootstrapToken; password = $adminPassword } | ConvertTo-Json
        try {
            Invoke-RestMethod -Method Post -Uri "https://$domain/api/bootstrap" -ContentType 'application/json; charset=utf-8' -Body $body | Out-Null
        } catch {
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -ne 409) { throw }
            Write-Host '系统已经初始化，保留现有 ICe 账号。' -ForegroundColor Yellow
        }
    }

    $delivery = Join-Path (Get-Location) 'dist\delivery'
    New-Item -ItemType Directory -Force -Path $delivery | Out-Null
    Set-Content -LiteralPath (Join-Path $delivery 'cloud-url.txt') -Value "https://$domain" -Encoding utf8

    Write-Host ''
    Write-Host '正在生成教室端和教师端交付包...' -ForegroundColor Cyan
    & (Join-Path (Get-Location) 'prepare-delivery.bat')
    if ($LASTEXITCODE -ne 0) { throw '交付包生成失败。' }

    Write-Host ''
    Write-Host '全部完成。' -ForegroundColor Green
    Write-Host "正式网址：https://$domain"
    Write-Host '管理员账号：ICe'
    Write-Host "已发布教室端版本：$receiverVersion"
    Write-Host "交付包：$delivery"
    Start-Process "https://$domain"
    Start-Process explorer.exe $delivery
}
finally {
    $adminPassword = $null
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
